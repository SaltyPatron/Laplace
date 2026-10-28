using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Laplace.Engine.Core;
using Npgsql;

namespace Laplace.Decomposers.Abstractions;

public enum NativeRecipeMode { Ingest, Plan, Claims, NoLoad }

public sealed record NativeRecipeRequest(
    NativeRecipeMode Mode, string? Source, IReadOnlyList<string> Paths,
    string? ConnectionInfo = null, int Threads = 0, bool ExpandPrerequisites = true);

/// <summary>
/// Executes the canonical Engine compiler and record pipeline. Managed code passes
/// configuration and byte streams; the Engine owns grammar loading, composition,
/// containment, pruning, COPY and source registration. Native failures remain in
/// the child process and its actual exit status is returned to the caller.
/// </summary>
public sealed class NativeRecipeExecution
{
    public string Executable { get; }
    public string Recipes { get; }
    public string Grammars { get; }
    public string Tier0 { get; }
    public string Highway { get; }

    public static NativeRecipeExecution Configured()
    {
        string executable = ConfiguredExecutable();
        string bundledGrammars = Path.Combine(Path.GetDirectoryName(executable)!, "grammars");
        string grammars = Directory.Exists(bundledGrammars) ? bundledGrammars
            : LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_GRAMMARS")
                ?? throw new DirectoryNotFoundException("The Native Engine grammar bundle is not installed.");
        return new NativeRecipeExecution(executable, EtlManifest.RecipesRoot, grammars,
            LaplaceInstall.ResolveT0Perfcache(), LaplaceInstall.ResolveHighwayPerfcache());
    }

    private static string ConfiguredExecutable()
    {
        string name = OperatingSystem.IsWindows() ? "laplace.exe" : "laplace";
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "native", "recipe-engine", "bin", name),
            Path.Combine(AppContext.BaseDirectory, name),
        };
        // Pre-install checks use the selected build. An app-local bundle above
        // remains authoritative for a published application.
        var build = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_ENGINE_BUILD");
        if (!string.IsNullOrWhiteSpace(build)) candidates.Add(Path.Combine(build, "core", "recipe_engine", "runtime", "bin", name));
        var prefix = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_INSTALL_PREFIX");
        if (!string.IsNullOrWhiteSpace(prefix)) candidates.Add(Path.Combine(prefix, "bin", name));
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("The canonical Native recipe Engine is not installed or built.");
    }

    public NativeRecipeExecution(string executable, string recipes, string grammars, string tier0, string highway)
    {
        Executable = ExistingFile(executable);
        Recipes = Path.GetFullPath(recipes);
        if (!Directory.Exists(Recipes) || !File.Exists(Path.Combine(Recipes, "order")))
            throw new DirectoryNotFoundException($"Canonical recipe estate not found: {Recipes}");
        Grammars = Path.GetFullPath(grammars);
        if (!Directory.Exists(Grammars))
            throw new DirectoryNotFoundException($"Compiled Engine grammars not found: {Grammars}");
        Tier0 = ExistingFile(tier0);
        Highway = ExistingFile(highway);
    }

    // Reuse the caller's declared database and transport settings. Values go
    // through the child environment, never a shell or process argument vector.
    public static string ConnectionInfoFromNpgsql(string connectionString)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Database))
            throw new ArgumentException("The substrate host and database must be declared.", nameof(connectionString));
        static string Pair(string name, string value) => name + "='"
            + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
        var fields = new List<string>
        {
            Pair("host", settings.Host),
            Pair("port", settings.Port.ToString(CultureInfo.InvariantCulture)),
            Pair("dbname", settings.Database),
            Pair("connect_timeout", settings.Timeout.ToString(CultureInfo.InvariantCulture)),
            Pair("sslmode", settings.SslMode switch
            {
                SslMode.Disable => "disable", SslMode.Allow => "allow", SslMode.Prefer => "prefer",
                SslMode.Require => "require", SslMode.VerifyCA => "verify-ca", SslMode.VerifyFull => "verify-full",
                _ => throw new ArgumentOutOfRangeException(nameof(connectionString)),
            }),
        };
        if (!string.IsNullOrEmpty(settings.Username)) fields.Add(Pair("user", settings.Username));
        if (!string.IsNullOrEmpty(settings.Password)) fields.Add(Pair("password", settings.Password));
        if (!string.IsNullOrEmpty(settings.RootCertificate)) fields.Add(Pair("sslrootcert", settings.RootCertificate));
        if (!string.IsNullOrEmpty(settings.SslCertificate)) fields.Add(Pair("sslcert", settings.SslCertificate));
        if (!string.IsNullOrEmpty(settings.SslKey)) fields.Add(Pair("sslkey", settings.SslKey));
        if (!string.IsNullOrEmpty(settings.Options)) fields.Add(Pair("options", settings.Options));
        return string.Join(" ", fields);
    }

    internal ProcessStartInfo StartInfo(NativeRecipeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Paths);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Threads);
        if (!Enum.IsDefined(request.Mode)) throw new ArgumentOutOfRangeException(nameof(request.Mode));
        var paths = request.Paths.Select(Path.GetFullPath).ToArray();
        string? source = string.IsNullOrWhiteSpace(request.Source) ? null : request.Source;
        if (source is not null && (!char.IsAsciiLetterOrDigit(source[0])
            || source.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))))
            throw new ArgumentException("Use the canonical source name from recipes/order.", nameof(request));
        if (source is null && paths.Length == 0)
            throw new ArgumentException("Name a source or its input paths.", nameof(request));
        if (request.Mode == NativeRecipeMode.Claims && paths.Length == 0)
            throw new ArgumentException("Claim inspection requires explicit input paths.", nameof(request));

        var start = ProcessInfo(Executable, request.ConnectionInfo);
        start.ArgumentList.Add("ingest");
        start.ArgumentList.Add("-r"); start.ArgumentList.Add(Recipes);
        start.ArgumentList.Add("-t"); start.ArgumentList.Add(Tier0);
        // A root request owns prerequisite expansion. An entry in an explicit
        // source sequence is the same source job the Native coordinator starts.
        // Ambient child markers never change the scope of a root request.
        start.Environment.Remove("LAPLACE_INGEST_ONE");
        if (!request.ExpandPrerequisites) start.Environment["LAPLACE_INGEST_ONE"] = "1";
        start.Environment["LAPLACE_HIGHWAY"] = Highway;
        start.Environment["LAPLACE_GRAMMARS"] = Grammars;
        // Share the host resource plan with the Native working window. File
        // boundaries and parser feed sizes do not own persistence budgets.
        start.Environment["LAPLACE_WORKING_SET_BYTES"] = MemoryTopology.WorkingSetFlushEnvelopeBytes
            .ToString(CultureInfo.InvariantCulture);
        if (request.Threads > 0)
        {
            start.ArgumentList.Add("-j");
            start.ArgumentList.Add(request.Threads.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (request.Mode != NativeRecipeMode.Ingest)
            start.ArgumentList.Add(request.Mode switch
            {
                NativeRecipeMode.Plan => "--plan",
                NativeRecipeMode.Claims => "--claims-tsv",
                NativeRecipeMode.NoLoad => "--no-load",
                _ => throw new ArgumentOutOfRangeException(nameof(request.Mode)),
            });
        if (source is not null && paths.Length > 0)
        {
            start.ArgumentList.Add("-s"); start.ArgumentList.Add(source);
        }
        foreach (var path in paths) start.ArgumentList.Add(path);
        // A source name is the Engine's positional selection when no paths were
        // supplied. It retains dependency ordering from recipes/order.
        if (paths.Length == 0) start.ArgumentList.Add(source!);
        return start;
    }

    public Task<int> ExecuteAsync(NativeRecipeRequest request,
        Stream standardOutput, Stream standardError, CancellationToken ct = default,
        Action<NativeRecipeProgress>? progress = null)
    {
        if (progress is not null && request.Mode != NativeRecipeMode.Ingest)
            throw new ArgumentException("Run receipts observe an actual ingest.", nameof(request));
        return ExecuteAsync(StartInfo(request), standardOutput, standardError, ct, progress);
    }

    // Database inventory shares the same Native operation and selected target
    // as laplace status. It does not require recipes or grammar bundles.
    public static Task<int> StatusAsync(string connectionInfo, bool joins,
        Stream standardOutput, Stream standardError, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionInfo);
        var start = ProcessInfo(ConfiguredExecutable(), connectionInfo);
        start.ArgumentList.Add("status");
        start.Environment["LAPLACE_TIER0"] = LaplaceInstall.ResolveT0Perfcache();
        start.Environment["LAPLACE_HIGHWAY"] = LaplaceInstall.ResolveHighwayPerfcache();
        if (joins) start.ArgumentList.Add("--joins");
        return ExecuteAsync(start, standardOutput, standardError, ct);
    }

    private static ProcessStartInfo ProcessInfo(string executable, string? connectionInfo)
    {
        string work = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_WORK_ROOT")
            ?? LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_WORK")
            ?? throw new InvalidOperationException("Native execution requires the declared work directory.");
        work = Path.GetFullPath(work);
        if (!Directory.Exists(work)) throw new DirectoryNotFoundException($"The declared work directory is not available: {work}");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = work,
        };
        // The Engine and its source jobs write receipts and room.tsv here.
        // Published executables and their content-addressed packages are immutable.
        start.Environment["LAPLACE_WORK"] = work;
        start.Environment["LAPLACE_WORK_ROOT"] = work;
        if (connectionInfo is not null) start.Environment["LAPLACE_CONNINFO"] = connectionInfo;
        return start;
    }

    private static async Task<int> ExecuteAsync(ProcessStartInfo start,
        Stream standardOutput, Stream standardError, CancellationToken ct = default,
        Action<NativeRecipeProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        ct.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        string? progressPath = null;
        if (progress is not null)
        {
            string work = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_WORK_ROOT")
                ?? LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_WORK")
                ?? throw new InvalidOperationException("Native progress requires the declared work directory.");
            if (!Directory.Exists(work)) throw new DirectoryNotFoundException($"The declared work directory is not available: {work}");
            progressPath = Path.Combine(work, "ingest-progress-" + Guid.NewGuid().ToString("N") + ".jsonl");
            using (File.Create(progressPath)) { }
            process.StartInfo.Environment["LAPLACE_INGEST_PROGRESS_PATH"] = progressPath;
        }
        Task output = Task.CompletedTask, error = Task.CompletedTask, observations = Task.CompletedTask;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("The Native recipe Engine did not start.");
            using var cancellation = ct.Register(() => Stop(process));
            output = PumpAsync(process.StandardOutput.BaseStream, standardOutput, process, ct);
            error = PumpAsync(process.StandardError.BaseStream, standardError, process, ct);
            observations = progressPath is null ? Task.CompletedTask
                : ObserveAsync(progressPath, progress!, process, ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(output, error, observations).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        catch
        {
            Stop(process);
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (InvalidOperationException) { /* Process start itself failed. */ }
            try { await Task.WhenAll(output, error, observations).ConfigureAwait(false); }
            catch { /* Retain the original process, cancellation or stream failure. */ }
            throw;
        }
        finally
        {
            if (progressPath is not null) File.Delete(progressPath);
        }
    }

    private static async Task ObserveAsync(string path, Action<NativeRecipeProgress> progress,
        Process process, CancellationToken ct)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            using var pending = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                int count = await file.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                for (int i = 0; i < count; i++)
                {
                    if (buffer[i] != (byte)'\n') { pending.WriteByte(buffer[i]); continue; }
                    var observation = JsonSerializer.Deserialize<NativeRecipeProgress>(pending.GetBuffer().AsSpan(0, checked((int)pending.Length)), options)
                        ?? throw new InvalidDataException("Native progress contained no observation.");
                    pending.SetLength(0);
                    if (observation.FilesTotal < 0 || observation.FilesDone < 0
                        || observation.FilesDone > observation.FilesTotal || observation.Entities < 0
                        || observation.Physicalities < 0 || observation.BytesDone < 0)
                        throw new InvalidDataException("Native progress contained invalid counters.");
                    if (observation.Event is not ("inventory" or "file-start" or "file-known" or "file-composed" or "progress" or "complete"))
                        throw new InvalidDataException("Native progress contained an unknown event.");
                    progress(observation);
                }
                if (count != 0) continue;
                if (process.HasExited)
                {
                    if (pending.Length != 0) throw new InvalidDataException("Native progress ended inside an observation.");
                    return;
                }
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
        catch { Stop(process); throw; }
    }

    private static async Task PumpAsync(Stream input, Stream output, Process process, CancellationToken ct)
    {
        try { await input.CopyToAsync(output, ct).ConfigureAwait(false); }
        catch { Stop(process); throw; }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* It exited between the check and kill. */ }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
    }

    private static string ExistingFile(string path)
    {
        var absolute = Path.GetFullPath(path);
        return File.Exists(absolute) ? absolute
            : throw new FileNotFoundException("Required Native recipe artifact not found.", absolute);
    }
}
