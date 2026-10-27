using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Cli.Tests;

/// <summary>Signal the real CLI while its Native publisher is blocked in a private database.</summary>
[Trait("Category", "RequiresDatabase")]
[Trait("Tier", "db")]
public sealed class NativeIngestCancellationTests(ITestOutputHelper output)
{
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [SkippableTheory]
    [InlineData(15)]
    [InlineData(2)]
    public async Task ProcessSignalClosesNativeRunAndFileReceipts(int signal)
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "This process/publication fixture requires Linux");
        string cli = Environment.GetEnvironmentVariable("LAPLACE_NATIVE_INGEST_TEST_CLI")
            ?? typeof(Program).Assembly.Location;
        cli = Path.GetFullPath(cli);
        output.WriteLine("CLI: {0}; SHA256: {1}", cli,
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(cli))));
        var native = NativeRecipeExecution.Configured();
        string database = "laplace_native_cancel_" + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(LaplaceInstall.PostgresConnectionString())
        {
            Database = database,
            Pooling = false,
        };
        var adminSettings = new NpgsqlConnectionStringBuilder(settings.ConnectionString) { Database = "postgres" };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        await using var admin = new NpgsqlConnection(adminSettings.ConnectionString);
        await admin.OpenAsync(token);
        string scratch = Directory.CreateTempSubdirectory("laplace-native-cancel-").FullName;
        bool created = false;
        Process? process = null;
        Task<string>? stdout = null, stderr = null;
        try
        {
            await ExecuteAsync(admin, $"CREATE DATABASE {Quote(database)}", token);
            created = true;
            string file = Path.Combine(scratch, "abandon.xml");
            await File.WriteAllTextAsync(file, """
                <?xml version="1.0" encoding="UTF-8"?>
                <frameset><predicate lemma="abandon">
                <roleset id="abandon.01" name="leave behind">
                <roles><role n="0" descr="agent"/></roles>
                </roleset></predicate></frameset>
                """, token);
            var deploy = new ProcessStartInfo(native.Executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            deploy.Environment["LAPLACE_CONNINFO"] = NativeRecipeExecution.ConnectionInfoFromNpgsql(settings.ConnectionString);
            deploy.Environment["LAPLACE_TIER0"] = native.Tier0;
            deploy.Environment["LAPLACE_HIGHWAY"] = native.Highway;
            deploy.Environment["LAPLACE_WORK"] = scratch;
            deploy.ArgumentList.Add("deploy");
            await RunAsync(deploy, token);
            await using var connection = new NpgsqlConnection(settings.ConnectionString);
            await connection.OpenAsync(token);
            await ExecuteAsync(connection, """
                CREATE EXTENSION IF NOT EXISTS laplace_geom;
                CREATE EXTENSION IF NOT EXISTS laplace_substrate;
                """, token);
            await using var blocker = new NpgsqlConnection(settings.ConnectionString);
            await blocker.OpenAsync(token);
            await using var transaction = await blocker.BeginTransactionAsync(token);
            // SHARE permits the reader's SELECTs but holds source publication at
            // its INSERT. The real parser/COPY/receipt pipeline runs before signal.
            await using (var command = new NpgsqlCommand("LOCK TABLE public.witness IN SHARE MODE", blocker, transaction))
                await command.ExecuteNonQueryAsync(token);
            string receipt = Path.Combine(scratch, "run.json");
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in new[] { cli, "ingest", "propbank", file }) start.ArgumentList.Add(arg);
            start.Environment["LAPLACE_DB"] = settings.ConnectionString;
            start.Environment["LAPLACE_WORK_ROOT"] = scratch;
            start.Environment["LAPLACE_WORK"] = scratch;
            start.Environment["LAPLACE_INGEST_RUN_RECEIPT_PATH"] = receipt;
            process = Process.Start(start)!;
            stdout = process.StandardOutput.ReadToEndAsync();
            stderr = process.StandardError.ReadToEndAsync();
            await UntilAsync(() => Task.FromResult(File.Exists(receipt)), process, token);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(receipt, token));
            Guid run = document.RootElement.GetProperty("run_id").GetGuid();
            int nativePid = 0;
            await UntilAsync(async () =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT a.pid FROM pg_stat_activity a
                    WHERE a.datname=current_database() AND a.wait_event_type='Lock'
                      AND a.pid<>pg_backend_pid()
                      AND EXISTS(SELECT FROM laplace.ingest_file_journal WHERE run_id=$1)
                    LIMIT 1
                    """, connection);
                command.Parameters.AddWithValue(run);
                nativePid = (int?)await command.ExecuteScalarAsync(token) ?? 0;
                return nativePid != 0;
            }, process, token);
            await using var before = new NpgsqlCommand("SELECT id::text FROM public.entity", connection);
            var committed = new List<string>();
            await using (var reader = await before.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token)) committed.Add(reader.GetString(0));
            Assert.NotEmpty(committed);
            Assert.Equal(0, kill(process.Id, signal));
            await process.WaitForExitAsync(token);
            output.WriteLine("stdout: {0}", await stdout);
            output.WriteLine("stderr: {0}", await stderr);
            Assert.NotEqual(0, process.ExitCode);
            await using (var state = new NpgsqlCommand("""
                SELECT r.status,r.ended_at IS NOT NULL,
                    (SELECT count(*) FROM laplace.ingest_file_journal f WHERE f.run_id=r.run_id),
                    NOT EXISTS(SELECT FROM laplace.ingest_file_journal f WHERE f.run_id=r.run_id
                        AND (f.status<>'cancelled' OR f.ended_at IS NULL))
                FROM laplace.ingest_run_journal r WHERE r.run_id=$1
                """, connection))
            {
                state.Parameters.AddWithValue(run);
                await using var reader = await state.ExecuteReaderAsync(token);
                Assert.True(await reader.ReadAsync(token));
                Assert.Equal("cancelled", reader.GetString(0));
                Assert.True(reader.GetBoolean(1));
                Assert.True(reader.GetInt64(2) > 0);
                Assert.True(reader.GetBoolean(3));
            }
            await using (var retained = new NpgsqlCommand("""
                SELECT count(*) FROM unnest($1::text[]) expected(id)
                WHERE NOT EXISTS(SELECT FROM public.entity e WHERE e.id=expected.id::blake3)
                """, connection))
            {
                retained.Parameters.AddWithValue(committed.ToArray());
                Assert.Equal(0L, await retained.ExecuteScalarAsync(token));
            }
            await UntilAsync(async () =>
            {
                await using var command = new NpgsqlCommand("SELECT NOT EXISTS(SELECT FROM pg_stat_activity WHERE pid=$1)", connection);
                command.Parameters.AddWithValue(nativePid);
                return (bool)(await command.ExecuteScalarAsync(token))!;
            }, null, token);
            output.WriteLine("signal={0}; run={1}; retained={2}; Native backend stopped; run/files cancelled",
                signal, run, committed.Count);
        }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                process.Dispose();
            }
            if (stdout is not null) output.WriteLine(await stdout);
            if (stderr is not null) output.WriteLine(await stderr);
            if (created)
                await ExecuteAsync(admin, $"DROP DATABASE {Quote(database)} WITH (FORCE)", CancellationToken.None);
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static string Quote(string name) => new NpgsqlCommandBuilder().QuoteIdentifier(name);

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task RunAsync(ProcessStartInfo start, CancellationToken token)
    {
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        Assert.True(process.ExitCode == 0, (await stderr) + (await stdout));
    }

    private static async Task UntilAsync(Func<Task<bool>> predicate, Process? process, CancellationToken token)
    {
        while (!await predicate())
        {
            token.ThrowIfCancellationRequested();
            Assert.False(process?.HasExited ?? false, "The CLI exited before the test reached its signal boundary");
            await Task.Delay(50, token);
        }
    }
}
