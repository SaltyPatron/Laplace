using System.Diagnostics;
using System.Globalization;
using Laplace.Engine.Core;
using Npgsql;

namespace Laplace.Decomposers.Abstractions;

public enum NativeRecipeMode { Ingest, Plan, Claims, NoLoad }

public sealed record NativeRecipeRequest(
    NativeRecipeMode Mode, string? Source, IReadOnlyList<string> Paths,
    string? ConnectionInfo = null, int Threads = 0);

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
        // This is a new root request. Only the Engine's own dependency-order
        // coordinator may mark a subsequent child as an individual source job.
        start.Environment.Remove("LAPLACE_INGEST_ONE");
        start.Environment["LAPLACE_HIGHWAY"] = Highway;
        start.Environment["LAPLACE_GRAMMARS"] = Grammars;
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
        Stream standardOutput, Stream standardError, CancellationToken ct = default)
        => ExecuteAsync(StartInfo(request), standardOutput, standardError, ct);

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
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        if (connectionInfo is not null) start.Environment["LAPLACE_CONNINFO"] = connectionInfo;
        return start;
    }

    private static async Task<int> ExecuteAsync(ProcessStartInfo start,
        Stream standardOutput, Stream standardError, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        ct.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("The Native recipe Engine did not start.");
        using var cancellation = ct.Register(() => Stop(process));
        Task output = PumpAsync(process.StandardOutput.BaseStream, standardOutput, process, ct);
        Task error = PumpAsync(process.StandardError.BaseStream, standardError, process, ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        catch
        {
            Stop(process);
            try { await Task.WhenAll(output, error).ConfigureAwait(false); }
            catch { /* Retain the original process, cancellation or stream failure. */ }
            throw;
        }
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
