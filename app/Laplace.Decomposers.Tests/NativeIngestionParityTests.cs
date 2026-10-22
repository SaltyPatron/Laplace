using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Npgsql;
using Xunit;

namespace Laplace.Decomposers.Tests;

public sealed class NativeIngestionParityTests
{
    [SkippableFact]
    [Trait("Category", "RequiresDatabase")]
    [Trait("Category", "RequiresQualification")]
    public async Task StandaloneAndMonorepoPersistIdenticalNativeProducts()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LAPLACE_PARITY_CONNECTION")),
            "Cross-stack qualification requires two built CLIs and an isolated PostgreSQL cluster.");
        string configuration = Required("LAPLACE_PARITY_CONNECTION");
        string standalone = Required("LAPLACE_PARITY_ENGINE");
        string mono = Required("LAPLACE_PARITY_MONO");
        string receipt = Required("LAPLACE_PARITY_RECEIPT");
        Assert.True(LaplaceInstall.TryRepoRoot(out string repository));
        string fixture = Path.Combine(repository, "app", "Laplace.Decomposers.Tests", "Fixtures", "NativeRecipes");
        string workspace = Directory.GetParent(repository)!.FullName;
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cili"] = Path.Combine(workspace, "Laplace-Engine", "tests", "fixtures", "highway-identities", "cili"),
            ["propbank"] = Path.Combine(workspace, "Laplace-Engine", "tests", "fixtures", "highway-identities", "propbank"),
            ["princeton-wordnet"] = Path.Combine(fixture, "wordnet"),
            ["conceptnet"] = Path.Combine(fixture, "conceptnet"),
            ["tatoeba"] = Path.Combine(fixture, "tatoeba"),
            ["opensubtitles"] = Path.Combine(fixture, "opensubtitles"),
            ["unicode"] = Path.Combine(repository, "app", "Laplace.Substrate.Tests", "Fixtures", "NativeRecipes", "normalization"),
        };
        int[] expectedClaims = [2, 80, 29, 4, 3, 2, 16];
        Directory.CreateDirectory(receipt);
        string estate = Path.Combine(receipt, "recipes");
        Assert.False(Directory.Exists(estate), "Use a fresh receipt directory for cold-database parity.");
        string canonical = NativeRecipeExecution.Configured().Recipes;
        foreach (string file in Directory.EnumerateFiles(canonical, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(estate, Path.GetRelativePath(canonical, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        foreach (var (source, path) in inputs)
        {
            Assert.True(Directory.Exists(path), path);
            string declaration = Path.Combine(estate, source, "source");
            string text = await File.ReadAllTextAsync(declaration);
            text = Regex.Replace(text, @"(?m)^root .*\r?$", "root " + path.Replace('\\', '/'));
            text = Regex.Replace(text, @"(?m)^after .*\r?\n", "");
            await File.WriteAllTextAsync(declaration, text);
        }
        await File.WriteAllTextAsync(Path.Combine(estate, "order"), string.Join('\n', inputs.Keys) + "\n");

        var adminSettings = new NpgsqlConnectionStringBuilder(configuration) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(adminSettings.ConnectionString);
        await admin.OpenAsync();
        string suffix = Guid.NewGuid().ToString("N");
        string[] databases = ["lp_parity_native_" + suffix, "lp_parity_mono_" + suffix];
        var snapshots = new List<string[]>();
        try
        {
            for (int lane = 0; lane < databases.Length; lane++)
            {
                string database = databases[lane];
                await using (var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin))
                    await create.ExecuteNonQueryAsync();
                var settings = new NpgsqlConnectionStringBuilder(configuration) { Database = database, Pooling = false };
                await using var connection = new NpgsqlConnection(settings.ConnectionString);
                await connection.OpenAsync();
                await using (var initialize = new NpgsqlCommand("CREATE EXTENSION postgis; CREATE EXTENSION laplace", connection))
                    await initialize.ExecuteNonQueryAsync();
                int sourceIndex = 0, expectedTotal = 0;
                foreach (string source in inputs.Keys)
                {
                    var start = new ProcessStartInfo(lane == 0 ? standalone : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                    };
                    if (lane == 1) start.ArgumentList.Add(mono);
                    start.ArgumentList.Add("ingest");
                    if (lane == 0)
                    {
                        start.ArgumentList.Add("-r"); start.ArgumentList.Add(estate);
                        start.ArgumentList.Add("-t"); start.ArgumentList.Add(LaplaceInstall.ResolveT0Perfcache());
                    }
                    start.ArgumentList.Add(source);
                    start.Environment.Remove("LAPLACE_INGEST_ONE");
                    start.Environment["LAPLACE_RECIPES"] = estate;
                    start.Environment["LAPLACE_WORK"] = Path.Combine(receipt, "work-" + lane);
                    start.Environment["LAPLACE_HIGHWAY"] = LaplaceInstall.ResolveHighwayPerfcache();
                    start.Environment["LAPLACE_GRAMMARS"] = NativeRecipeExecution.Configured().Grammars;
                    start.Environment["LAPLACE_DB"] = settings.ConnectionString;
                    start.Environment["LAPLACE_CONNINFO"] =
                        $"host={Quote(settings.Host!)} port={settings.Port} dbname={Quote(database)} user={Quote(settings.Username!)} sslmode=disable";
                    if (!string.IsNullOrEmpty(settings.Password)) start.Environment["PGPASSWORD"] = settings.Password;
                    using var process = Process.Start(start)!;
                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    Task<string> errors = process.StandardError.ReadToEndAsync();
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                    try { await process.WaitForExitAsync(deadline.Token); }
                    catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
                    string log = await output + await errors;
                    await File.WriteAllTextAsync(Path.Combine(receipt, $"{lane}-{source}.log"), log);
                    Assert.True(process.ExitCode == 0, log);
                    expectedTotal += expectedClaims[sourceIndex++];
                    await using var count = new NpgsqlCommand("SELECT count(*) FROM public.consensus", connection);
                    Assert.Equal(expectedTotal, Convert.ToInt32(await count.ExecuteScalarAsync()));
                }
                var rows = new List<string>();
                await using var snapshot = new NpgsqlCommand(Snapshot, connection);
                await using var reader = await snapshot.ExecuteReaderAsync();
                while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
                Assert.Contains(rows, row => row.StartsWith("c|", StringComparison.Ordinal));
                Assert.Equal(inputs.Count, rows.Count(row => row.StartsWith("w|", StringComparison.Ordinal)));
                await File.WriteAllLinesAsync(Path.Combine(receipt, "snapshot-" + lane + ".txt"), rows);
                snapshots.Add(rows.ToArray());
            }
            string[] differences = snapshots[0].Except(snapshots[1], StringComparer.Ordinal)
                .Concat(snapshots[1].Except(snapshots[0], StringComparer.Ordinal)).ToArray();
            if (differences.Length != 0)
                await File.WriteAllLinesAsync(Path.Combine(receipt, "differences.txt"), differences);
            Assert.True(snapshots[0].SequenceEqual(snapshots[1], StringComparer.Ordinal),
                $"Persisted rows differ: {snapshots[0].Length}/{snapshots[1].Length}; "
                + string.Join("; ", differences.Take(8).Select(row => row[..Math.Min(140, row.Length)])));
            string fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', snapshots[0]))));
            await File.WriteAllTextAsync(Path.Combine(receipt, "acceptance.txt"),
                $"PASS {inputs.Count} sources; {snapshots[0].Length} identical persisted rows; sha256={fingerprint}\n");
        }
        finally
        {
            foreach (string database in databases)
            {
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} for the Native database parity tier.");
    private static string Quote(string text) => "'" + text.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    private const string Snapshot = """
        SELECT row FROM (
          SELECT 'e|'||id::text||'|'||tier||'|'||encode(ST_AsEWKB(coord),'hex')||'|'||hilbert||'|'||mask::text AS row FROM public.entity
          UNION ALL SELECT 'p|'||entity::text||'|'||tier||'|'||encode(ST_AsEWKB(path),'hex')||'|'||hilbert||'|'||mask::text FROM public.physicality
          UNION ALL SELECT 'c|'||claim::text||'|'||encode(float8send(rating),'hex')||'|'||encode(float8send(deviation),'hex')||'|'||encode(float8send(volatility),'hex')||'|'||matches FROM public.consensus
          UNION ALL SELECT 'w|'||id::text||'|'||coalesce(lineage::text,'-')||'|'||encode(float8send(trust),'hex') FROM public.witness
        ) records ORDER BY row COLLATE "C"
        """;
}
