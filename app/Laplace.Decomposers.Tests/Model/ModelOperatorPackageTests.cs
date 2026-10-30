using System.Diagnostics;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Decomposers.Model.Tests;

public sealed class ModelOperatorPackageTests(ITestOutputHelper output)
{
    [SkippableFact]
    [Trait("Category", "RequiresQualification")]
    public void PackageOperators_ExecuteOverTheCompleteCanonicalVocabulary()
    {
        string? package = Environment.GetEnvironmentVariable("LAPLACE_MODEL_TEST_PACKAGE");
        string? declared = Environment.GetEnvironmentVariable("LAPLACE_MODEL_TEST_NATIVE_COUNTS");
        Skip.If(string.IsNullOrWhiteSpace(package) || string.IsNullOrWhiteSpace(declared),
            "LAPLACE_MODEL_TEST_PACKAGE and LAPLACE_MODEL_TEST_NATIVE_COUNTS select numerical qualification");
        var expected = declared!.Split(',').Select(item => item.Split('='))
            .ToDictionary(pair => pair[0], pair => int.Parse(pair[1], System.Globalization.CultureInfo.InvariantCulture));
        var clock = Stopwatch.StartNew();
        using var snapshot = SourceEntityIdConventions.OpenModelContentSnapshot(package!)!;
        byte[] tokenizerBytes = snapshot.Read(Path.Combine(package!, "tokenizer.json"), stream =>
        {
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return bytes.ToArray();
        });
        var tokens = LlamaTokenizerParser.Parse(tokenizerBytes);
        var headers = SafetensorsContainerParser.ParseModel(snapshot);
        var config = snapshot.Read(Path.Combine(package!, "config.json"), stream =>
        {
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return ModelConfigReader.Read(bytes.ToArray());
        });
        var manifest = ModelManifest.Recognize(headers, config, LlamaTokenizerParser.IdSpace(tokenizerBytes), Path.GetFileName(package));
        Hash128[] vocabulary = tokens.Where(t => t.TokenId >= 0 && t.TokenId < manifest.Config.VocabSize)
            .Select(t => t.EntityId).Distinct().ToArray();
        var canonical = vocabulary.Select((id, row) => (id, row)).ToDictionary(pair => pair.id, pair => pair.row);
        var estate = new ModelCircuitEstate(new(package!, manifest, tokens, snapshot.SourceId, snapshot), canonical);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var contexts = new HashSet<Hash128>();
        var addresses = new HashSet<Hash128>();
        foreach (Hash128 relation in new[] { ModelDecomposer.SimilarToTypeId, ModelDecomposer.AttendsTypeId,
                     ModelDecomposer.OvRelatesTypeId, ModelDecomposer.CompletesToTypeId })
        {
            foreach (ModelCircuitDescriptor descriptor in estate.Enumerate(relation))
            {
                Assert.Equal(vocabulary.Length, descriptor.Contraction.EntityCount);
                var (scores, order) = descriptor.Contraction.Salience(vocabulary);
                Assert.Equal(vocabulary.Length, scores.Length);
                Assert.Equal(vocabulary.Length, order.Length);
                Assert.True(contexts.Add(descriptor.ContextId), $"reused numerical context {descriptor.SourcePath}/{descriptor.Plane}/{descriptor.Head}");
                Assert.True(addresses.Add(ModelCoordinates.CircuitId(snapshot.SourceId, "package", descriptor.Plane,
                    descriptor.Layer, descriptor.Head, descriptor.SourcePath.Length == 0 ? null : descriptor.SourcePath,
                    descriptor.Operator.Length == 0 ? null : descriptor.Operator)), "source circuit addresses collided");
                counts[descriptor.Plane] = counts.GetValueOrDefault(descriptor.Plane) + 1;
            }
            output.WriteLine($"computed relation={relation}; circuits={contexts.Count}; elapsed={clock.Elapsed}");
        }
        snapshot.VerifySourceId();
        Assert.Empty(estate.Skipped);
        Assert.Equal(expected.OrderBy(pair => pair.Key), counts.OrderBy(pair => pair.Key));
        var report = new { package, observedAt = DateTimeOffset.UtcNow, elapsedMs = clock.Elapsed.TotalMilliseconds,
            source = Convert.ToHexString(snapshot.SourceId.ToBytes()).ToLowerInvariant(),
            tokenizerRows = tokens.Count, canonicalEntities = vocabulary.Length,
            circuits = counts, numericalContexts = contexts.Count, sourceAddresses = addresses.Count,
            peakNativeResidentBytes = estate.PeakNativeResidentBytes };
        output.WriteLine(JsonSerializer.Serialize(report));
        if (Environment.GetEnvironmentVariable("LAPLACE_MODEL_TEST_REPORT") is { Length: > 0 } path)
        {
            Assert.True(Path.IsPathFullyQualified(path));
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [SkippableFact]
    [Trait("Category", "RequiresQualification")]
    public void PackageOperands_StayWithinTheirSourceScopes()
    {
        string? package = Environment.GetEnvironmentVariable("LAPLACE_MODEL_TEST_PACKAGE");
        Skip.If(string.IsNullOrWhiteSpace(package), "LAPLACE_MODEL_TEST_PACKAGE selects a locally held package");
        string? expected = Environment.GetEnvironmentVariable("LAPLACE_MODEL_TEST_MLP_COUNT");
        Assert.True(int.TryParse(expected, out int expectedMlps) && expectedMlps > 0,
            "LAPLACE_MODEL_TEST_MLP_COUNT must name the expected source operator count");
        var clock = Stopwatch.StartNew();
        var tensors = SafetensorsContainerParser.ParseModel(package!);
        var config = ModelConfigReader.Read(Path.Combine(package!, "config.json"));
        ModelManifest manifest = ModelManifest.Recognize(tensors, config, null, Path.GetFileName(package));
        var mlps = manifest.Anatomy.Operators.Where(o => o.Family == "mlp").ToArray();
        int attention = manifest.Anatomy.Operators.Count(o => o.Family == "attention");
        int completeAttention = manifest.Anatomy.Operators.Count(o => o.Family == "attention"
            && o.Slots.All(s => !s.IsAmbiguous));
        Assert.Equal(expectedMlps, mlps.Length);
        Assert.All(mlps, op => Assert.DoesNotContain(op.Slots, slot => slot.IsAmbiguous));
        foreach (OperatorInstance op in manifest.Anatomy.Operators.Where(o => o.Scope != "model"))
        {
            Assert.NotEmpty(op.SourcePath);
            foreach (SlotBinding slot in op.Slots.Where(s => s.IsBound))
                Assert.StartsWith(op.SourcePath + ".", slot.Tensor!);
        }
        clock.Stop();
        var report = new
        {
            package, observedAt = DateTimeOffset.UtcNow, elapsedMs = clock.Elapsed.TotalMilliseconds,
            headerTensors = tensors.Count, operators = manifest.Anatomy.Operators.Count,
            mlps = mlps.Length, attention, completeAttention, width = manifest.Config.HiddenSize,
            unrecognized = manifest.Anatomy.Unrecognized.Count,
            ambiguous = manifest.Anatomy.Operators.Sum(o => o.Slots.Count(s => s.IsAmbiguous)),
            unrecognizedTensors = manifest.Anatomy.Unrecognized,
            scopes = manifest.Anatomy.Operators.Select(o => new
                { o.SourcePath, o.Operator, o.InstanceSymbols, o.ContextSymbols, o.Slots }).ToArray(),
        };
        if (Environment.GetEnvironmentVariable("LAPLACE_MODEL_TEST_REPORT") is { Length: > 0 } reportPath)
        {
            Assert.True(Path.IsPathFullyQualified(reportPath), "LAPLACE_MODEL_TEST_REPORT must be an absolute output path");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        output.WriteLine(JsonSerializer.Serialize(new
        {
            report.package, report.observedAt, report.elapsedMs, report.headerTensors,
            report.operators, report.mlps, report.attention, report.completeAttention, report.width,
            report.unrecognized, report.ambiguous,
        }));
        if (Environment.GetEnvironmentVariable("LAPLACE_MODEL_TEST_ATTENTION_COUNT") is { } expectedAttention)
        {
            Assert.True(int.TryParse(expectedAttention, out int count) && count >= 0);
            Assert.Equal(count, attention);
        }
    }
}
