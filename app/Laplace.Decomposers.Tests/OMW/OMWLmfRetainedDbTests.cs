using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Xunit;

namespace Laplace.Decomposers.Tests.OMW;

[Trait("Category", "RequiresDatabase")]
[Trait("Tier", "db")]
public sealed class OMWLmfRetainedDbTests
{
    [SkippableFact]
    public async Task SelectedItalianLexiconHasNativeClaimBodiesStandingsContainmentAndFileTrunkDeduplication()
    {
        string? connection = Environment.GetEnvironmentVariable("LAPLACE_OMW_RETAINED_DB");
        Skip.If(string.IsNullOrWhiteSpace(connection),
            "Set LAPLACE_OMW_RETAINED_DB to an explicitly retained isolated Native verification database.");
        string? data = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_DATA");
        Skip.If(string.IsNullOrWhiteSpace(data), "LAPLACE_DATA must declare the existing corpus estate.");
        string root = Path.Combine(data!, "OMW-2.0", "omw-2.0", "omw-it");
        Skip.IfNot(Directory.Exists(root), "complete OMW 2.0 Italian lexicon is not mounted");
        string[] files = ["LICENSE", "README", "citation.bib", "omw-it.xml"];
        files = files.Select(name => Path.Combine(root, name)).ToArray();
        Assert.All(files, path => Assert.True(File.Exists(path), path));

        // No database/role creation, migration, service control or private cluster.
        // Ingestion and subsequent reads target this same declared database.
        var settings = new NpgsqlDataSourceBuilder(connection!);
        Assert.DoesNotContain(settings.ConnectionStringBuilder.Database,
            new[] { "laplace", "laplace-mono", "laplace-app", "laplace-desktop-app", "laplace-desktop-substrate" },
            StringComparer.OrdinalIgnoreCase);
        settings.ConnectionStringBuilder.CommandTimeout = 0;
        await using var source = settings.Build();
        var execution = NativeRecipeExecution.Configured();
        var request = new NativeRecipeRequest(NativeRecipeMode.Ingest,
            "open-multilingual-wordnet", files,
            NativeRecipeExecution.ConnectionInfoFromNpgsql(connection!), Threads: 4);
        await IngestAsync(execution, request);
        string witness = await WitnessAsync(source);
        ClaimProof[] before = await ClaimsAsync(source, witness);
        Assert.Equal(9, before.Select(proof => proof.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(before, proof =>
        {
            Assert.True(proof.Structure, "missing canonical entity/path: " + proof.Id);
            Assert.True(proof.Standing, "missing standing: " + proof.Id);
            Assert.True(proof.Contained, "claim is not contained by the registered source trunk: " + proof.Id);
            Assert.True(proof.Matches > 0, "claim has no games: " + proof.Id);
            Assert.True(proof.Rating > 1500, "an explicit false property is a positive source statement: " + proof.Id);
        });

        string repeated = await IngestAsync(execution, request);
        Assert.Contains("4 files recorded already, found by the OS's record of them, not read", repeated);
        Assert.Equal(witness, await WitnessAsync(source));
        Assert.Equal(before, await ClaimsAsync(source, witness));
    }

    private static async Task<string> IngestAsync(NativeRecipeExecution execution, NativeRecipeRequest request)
    {
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        int code = await execution.ExecuteAsync(request, output, error);
        string text = Encoding.UTF8.GetString(output.ToArray()) + Encoding.UTF8.GetString(error.ToArray());
        Assert.True(code == 0, text);
        return text;
    }

    private static async Task<string> WitnessAsync(NpgsqlDataSource source)
    {
        await using var command = NpgsqlCatalog.Command(source, "verify.native_source_witness", "Open Multilingual Wordnet");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "Native source trunk was not registered");
        string result = reader.GetString(0);
        Assert.False(await reader.ReadAsync(), "more than one current source trunk is registered");
        return result;
    }

    private static async Task<ClaimProof[]> ClaimsAsync(NpgsqlDataSource source, string witness)
    {
        string[] sense = ["tenda per doccia", "ita", "i58874"];
        (string[] Subject, string? Relation, string[] Object)[] expected =
        [
            (["tenda per doccia"], "lexical sense", sense),
            (["tenda per doccia"], null, ["n"]),
            (sense, "reference", ["omw-it-04209239-n"]),
            (sense, "is lexicalized", ["false"]),
            (["omw-it-04209239-n"], null, ["tenda per doccia"]),
            (["omw-it-04209239-n"], null, ["i58874"]),
            (["omw-it-04209239-n"], null, ["n"]),
            (["omw-it-04209239-n"], "is lexicalized", ["false"]),
            (["omw-it-04209239-n"], "definition of the synset",
                ["le tende che impediscono all'acqua di uscire dall'area della doccia"]),
        ];
        var result = new List<ClaimProof>(expected.Length);
        foreach (var claim in expected)
        {
            await using var command = NpgsqlCatalog.Command(source, "verify.native_claim",
                claim.Subject, claim.Relation, claim.Object, witness);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            result.Add(new ClaimProof(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2),
                reader.GetBoolean(3), reader.GetInt64(4), reader.GetDouble(5)));
            Assert.False(await reader.ReadAsync());
        }
        return result.ToArray();
    }

    private sealed record ClaimProof(string Id, bool Structure, bool Standing, bool Contained, long Matches, double Rating);
}
