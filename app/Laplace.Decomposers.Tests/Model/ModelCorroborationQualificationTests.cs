using System.Diagnostics;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Model;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Decomposers.Tests.Model;

public sealed class ModelCorroborationQualificationTests(ITestOutputHelper output)
{
    // Explicitly selected existing database and checkpoint: this qualification
    // creates no database, writes no testimony and changes no model artifacts.
    [SkippableFact]
    public async Task CandidateSignificance_UsesRealWeightsAndReportsDatabaseCoverage()
    {
        string? connection = Environment.GetEnvironmentVariable("LAPLACE_MODEL_CORROBORATION_DB");
        string? directory = Environment.GetEnvironmentVariable("LAPLACE_MODEL_CORROBORATION_DIR");
        Skip.If(string.IsNullOrEmpty(connection) || string.IsNullOrEmpty(directory),
            "Select an existing database and local checkpoint for model corroboration qualification.");
        var clock = Stopwatch.StartNew();
        using var snapshot = SourceEntityIdConventions.OpenModelContentSnapshot(directory!)
            ?? throw new InvalidDataException("selected checkpoint has no weights");
        byte[] config = snapshot.Read(Path.Combine(directory!, "config.json"), ReadBytes);
        byte[] tokenizer = snapshot.Read(Path.Combine(directory!, "tokenizer.json"), ReadBytes);
        var manifest = ModelManifest.Recognize(SafetensorsContainerParser.ParseModel(snapshot),
            ModelConfigReader.Read(config), LlamaTokenizerParser.IdSpace(tokenizer), "qualification");
        var tokens = LlamaTokenizerParser.Parse(tokenizer);
        Hash128[] vocabulary = tokens.Select(t => t.EntityId).Distinct()
            .OrderBy(id => id, Comparer<Hash128>.Create(static (a, b) => a.CompareToBytewise(b))).ToArray();
        var canonical = vocabulary.Select((id, row) => (id, row)).ToDictionary(p => p.id, p => p.row);
        var settings = new NpgsqlConnectionStringBuilder(connection!)
        {
            Options = "-c default_transaction_read_only=on",
            CommandTimeout = 60,
        };
        await using var db = NpgsqlDataSource.Create(settings.ConnectionString);
        var reader = new NpgsqlSubstrateReader(db);
        var page = await reader.ReadCircuitPairProposalsAsync(vocabulary,
            ModelDecomposer.SimilarToTypeId, true, null, null, 256);
        // Query the durable database directly, bypassing the reader's known
        // perf-cache floor so cache presence cannot stand in for stored rows.
        await using var presence = NpgsqlCatalog.Command(db, "ingest.entities_exist_bitmap",
            (object)vocabulary.Select(id => id.ToBytes()).ToArray());
        byte[] present = (byte[])(await presence.ExecuteScalarAsync()
            ?? throw new InvalidDataException("database returned no presence bitmap"));
        int[] persistedRows = Enumerable.Range(0, vocabulary.Length)
            .Where(i => (present[i >> 3] & (1 << (i & 7))) != 0).ToArray();
        Assert.True(persistedRows.Length >= 2, "fewer than two checkpoint entities exist in the selected database");
        // These inspection pairs are actual stored entities, not invented graph
        // nominations. Report nomination coverage separately, including zero.
        var pairs = page.Rows.Select(p => (Subject: canonical[p.Subject], Object: canonical[p.Object]))
            .Concat(persistedRows.Take(8).SelectMany(a => persistedRows.Where(b => a != b)
                .Select(b => (Subject: a, Object: b)))).Distinct().ToArray();
        int[] subjects = pairs.Select(p => p.Subject).ToArray();
        int[] objects = pairs.Select(p => p.Object).ToArray();
        var estate = new ModelCircuitEstate(new(directory!, manifest, tokens, snapshot.SourceId, snapshot), canonical);
        int circuits = 0;
        foreach (var circuit in estate.Enumerate(ModelDecomposer.SimilarToTypeId))
        {
            circuits++;
            var old = circuit.Contraction.Score(subjects, objects);
            var selected = circuit.Contraction.SignificantCandidates(subjects, objects, symmetric: true);
            Assert.DoesNotContain((short)AttestationOutcome.Refute, selected.Outcomes);
            for (int i = 0; i < subjects.Length; i++)
            {
                Assert.True(selected.Outcomes[i] is (short)AttestationOutcome.Confirm or (short)AttestationOutcome.Draw);
                if (selected.Outcomes[i] == (short)AttestationOutcome.Draw)
                    Assert.Equal(500_000_000, selected.Scores[i]);
            }

            // Compare a bounded suffix of the existing full-pair producer with
            // candidate scoring over the same canonical-entity null/family.
            SignificantPairPage dense = circuit.Contraction.SignificantPairs(
                Math.Max(0, vocabulary.Length - 32), vocabulary.Length * 32, symmetric: true);
            var replay = circuit.Contraction.SignificantCandidates(dense.Rows, dense.Cols, symmetric: true);
            Assert.All(replay.Outcomes, value => Assert.Equal((short)AttestationOutcome.Confirm, value));
            for (int i = 0; i < dense.Rows.Length; i++)
                Assert.InRange(Math.Abs(dense.ScoresFp1e9[i] - replay.Scores[i]), 0, 1);
            output.WriteLine($"source={snapshot.SourceId}; vocabulary={vocabulary.Length}; " +
                $"persisted_entities={persistedRows.Length}; retained_nominations={page.Rows.Count}; inspected_pairs={pairs.Length}; " +
                $"old_positive={old.Outcomes.Count(x => x == (short)AttestationOutcome.Confirm)}; " +
                $"old_negative={old.Outcomes.Count(x => x == (short)AttestationOutcome.Refute)}; " +
                $"significant={selected.Outcomes.Count(x => x == (short)AttestationOutcome.Confirm)}; " +
                $"full_pair_suffix_hits={dense.Rows.Length}; elapsed_ms={clock.Elapsed.TotalMilliseconds:F1}");
        }
        Assert.Equal(1, circuits);
        snapshot.VerifySourceId();
    }

    private static byte[] ReadBytes(Stream stream)
    {
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
