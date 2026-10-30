using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Decomposers.Model.Tests;

public sealed class ModelOperatorPackageTests(ITestOutputHelper output)
{
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
