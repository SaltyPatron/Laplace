using System.Diagnostics;
using System.Text;
using Laplace.Engine.Core;
using Laplace.Ops;

namespace Laplace.Chess.Service;

internal sealed record ChessNativeWitnessRequest(string? Witness, string TrustClass, string Path, string? SourceName = null);

/// <summary>
/// Records a game Laplace played (a play session, a Lichess game, a lab match, a self-play game) the way
/// every game is recorded: as one PGN file the Engine reads via native <c>laplace witness</c> at
/// AppDerived (the witness is the trunk [Laplace, the file's trunk]).
/// The Engine composes the record [line, tags, layer], attests each [position, move, position] with the
/// result scored for the side that moved, and folds the standings; nothing here composes an identity or
/// writes a substrate row. A game is admitted after it ends (Sequence 27.9).
/// </summary>
public sealed class ChessGameRecorder
{
    /// <summary>The witness of the games Laplace plays, as <c>recipes/laplace-chess/source</c> names it.</summary>
    public const string Witness = "Laplace";

    /// <summary>The class an application's own output states at (Laplace-Native <c>trust_classes.toml</c>).</summary>
    public const string TrustClass = "AppDerived";

    /// <summary>The class curated external game collections (Chess.com, Lichess) state at.</summary>
    public const string CuratedCorpusTrustClass = "StructuredCorpus";

    /// <summary>The witness for admin-curated FIDE-to-provider player identity links.</summary>
    public const string CuratedFideLinkWitness = "Laplace Curated Chess";

    /// <summary>The class admin-curated FIDE-to-provider player identity links state at.</summary>
    public const string CuratedFideLinkTrustClass = "AcademicCurated";

    /// <summary>The declared recipe source name for curated FIDE-to-provider identity links (<c>recipes/fide-links</c>).</summary>
    public const string CuratedFideLinksSource = "fide-links";

    private readonly string _connectionString;
    private readonly string _directory;
    private readonly Func<ChessNativeWitnessRequest, Stream, Stream, CancellationToken, Task<int>> _execute;

    public ChessGameRecorder(string connectionString, string? directory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _directory = directory ?? GamesDirectory();
        _execute = ExecuteNativeCliAsync;
    }

    internal ChessGameRecorder(
        string connectionString, string? directory,
        Func<ChessNativeWitnessRequest, Stream, Stream, CancellationToken, Task<int>> execute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _directory = directory ?? GamesDirectory();
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    /// <summary>Where Laplace's games are kept: the laplace-chess source's root.</summary>
    public static string GamesDirectory()
        => ChessRuntimeConfiguration.Read("LAPLACE_CHESS_GAMES") is { Length: > 0 } configured
            ? Path.GetFullPath(configured.Trim())
            : Path.Combine(LaplaceInstall.DeclaredIngestRoot(), "Games", "Chess", "laplace");

    /// <summary>The file a game's PGN is kept in: its text's BLAKE3, so one game is one file.</summary>
    public string PathFor(string pgn)
        => Path.Combine(_directory, Convert.ToHexStringLower(Hash128.Blake3(Encoding.UTF8.GetBytes(pgn)).ToBytes()) + ".pgn");

    /// <summary>
    /// Writes the game's PGN and has the Engine record it. Returns the file read. A failed read throws
    /// with what the Engine said: a game that is not recorded is never reported as recorded.
    /// </summary>
    public async Task<string> RecordAsync(string pgn, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pgn);
        Directory.CreateDirectory(_directory);
        string path = PathFor(pgn);
        if (!File.Exists(path))
        {
            string partial = path + ".partial";
            await File.WriteAllTextAsync(partial, pgn, new UTF8Encoding(false), ct).ConfigureAwait(false);
            File.Move(partial, path, overwrite: true);
        }
        await IngestFileAsync(Witness, path, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// Has the Engine read a PGN file: stated by the runtime witness <paramref name="witness"/> at
    /// <see cref="TrustClass"/> (Laplace's own games), or, when it is null, as a file of no corpus by the
    /// generic pgn recipe, its trunk its own witness. A failed read throws with what the Engine said.
    /// </summary>
    public Task IngestFileAsync(string? witness, string path, CancellationToken ct = default)
        => IngestFileAsync(witness, path, TrustClass, ct);

    /// <summary>
    /// Has the Engine read a file stated by the runtime witness <paramref name="witness"/> at an explicit
    /// <paramref name="trustClass"/> (for example <see cref="CuratedCorpusTrustClass"/> for curated Chess.com/Lichess
    /// archives or <see cref="CuratedFideLinkTrustClass"/> for curated FIDE identity links), or, when
    /// <paramref name="witness"/> is null, via <c>laplace ingest</c>.
    /// </summary>
    public async Task IngestFileAsync(string? witness, string path, string trustClass, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustClass);
        var request = new ChessNativeWitnessRequest(witness, trustClass, path);
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        int code = await _execute(request, output, error, ct).ConfigureAwait(false);
        if (code != 0)
            throw new InvalidOperationException($"The Engine did not record {path} ({code}): {Tail(error)}");
    }

    /// <summary>
    /// Has the Engine ingest a single file under a declared source recipe (<c>laplace ingest -d &lt;conn&gt; -s &lt;source&gt; &lt;path&gt;</c>),
    /// such as <c>fide-links</c> (<c>recipes/fide-links/links.recipe</c>).
    /// </summary>
    public async Task IngestSourceFileAsync(string sourceName, string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var request = new ChessNativeWitnessRequest(null, TrustClass, path, SourceName: sourceName.Trim());
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        int code = await _execute(request, output, error, ct).ConfigureAwait(false);
        if (code != 0)
            throw new InvalidOperationException($"The Engine did not ingest {path} under source '{sourceName}' ({code}): {Tail(error)}");
    }

    private async Task<int> ExecuteNativeCliAsync(
        ChessNativeWitnessRequest request, Stream output, Stream error, CancellationToken ct)
    {
        string exe = ResolveNativeBinary()
            ?? throw new InvalidOperationException(
                "Native C engine executable ('laplace') not found; set LAPLACE_ENGINE_EXE or LAPLACE_BUILD.");

        string connInfo = ToLibpqConnInfo(_connectionString);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        EnsureWritableWorkDir(psi);

        string fullPath = Path.GetFullPath(request.Path);
        string rootDir = Path.GetDirectoryName(fullPath) ?? _directory;
        if (!string.IsNullOrWhiteSpace(request.SourceName))
        {
            psi.ArgumentList.Add("ingest");
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(connInfo);
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(request.SourceName);
            psi.ArgumentList.Add(fullPath);
        }
        else if (!string.IsNullOrWhiteSpace(request.Witness))
        {
            psi.ArgumentList.Add("witness");
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(connInfo);
            psi.ArgumentList.Add("--as");
            psi.ArgumentList.Add(request.Witness);
            psi.ArgumentList.Add("--class");
            psi.ArgumentList.Add(request.TrustClass);
            psi.ArgumentList.Add("--lineage");
            psi.ArgumentList.Add(request.Witness);
            psi.ArgumentList.Add("--root");
            psi.ArgumentList.Add(rootDir);
            psi.ArgumentList.Add(fullPath);
        }
        else
        {
            psi.ArgumentList.Add("ingest");
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(connInfo);
            psi.ArgumentList.Add(fullPath);
        }

        using var proc = CapturedProcess.Start(psi);
        CapturedProcess.Result res;
        try
        {
            res = await proc.Completion.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await proc.TerminateAsync().ConfigureAwait(false);
            throw;
        }
        if (!string.IsNullOrEmpty(res.Stdout))
        {
            var outBytes = Encoding.UTF8.GetBytes(res.Stdout);
            await output.WriteAsync(outBytes, ct).ConfigureAwait(false);
        }
        if (!string.IsNullOrEmpty(res.Stderr))
        {
            var errBytes = Encoding.UTF8.GetBytes(res.Stderr);
            await error.WriteAsync(errBytes, ct).ConfigureAwait(false);
        }
        return res.ExitCode;
    }

    private static void EnsureWritableWorkDir(ProcessStartInfo psi)
    {
        var work = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_WORK")
            ?? LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_WORK_ROOT");
        if (string.IsNullOrWhiteSpace(work))
        {
            var opsLogs = Environment.GetEnvironmentVariable("LAPLACE_OPS_LOG_DIR");
            work = !string.IsNullOrWhiteSpace(opsLogs)
                ? Path.GetDirectoryName(Path.GetFullPath(opsLogs.Trim()))
                : null;
        }
        if (string.IsNullOrWhiteSpace(work))
            work = Path.Combine(Path.GetTempPath(), "laplace-work");
        else
            work = Path.GetFullPath(work.Trim());

        try
        {
            Directory.CreateDirectory(Path.Combine(work, "logs", "ingest", "runs"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            work = Path.Combine(Path.GetTempPath(), "laplace-work");
            try { Directory.CreateDirectory(Path.Combine(work, "logs", "ingest", "runs")); }
            catch { }
        }
        psi.Environment["LAPLACE_WORK"] = work;
    }

    internal static string ToLibpqConnInfo(string npgsqlConn)
    {
        var formatted = LaplaceEngineRuntime.FormatLibpqConnInfo(npgsqlConn);
        return !string.IsNullOrWhiteSpace(formatted) ? formatted : LaplaceEngineRuntime.ResolveLibpqConnInfo();
    }

    internal static string? ResolveNativeBinary()
    {
        var explicitExe = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_ENGINE_EXE")
            ?? LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_ENGINE_BIN");
        var exeName = OperatingSystem.IsWindows() ? "laplace.exe" : "laplace";
        if (!string.IsNullOrWhiteSpace(explicitExe))
            return File.Exists(explicitExe.Trim()) ? Path.GetFullPath(explicitExe.Trim()) : null;

        foreach (var probe in new[]
        {
            Path.Combine(AppContext.BaseDirectory, exeName),
            Path.Combine(AppContext.BaseDirectory, "bin", exeName),
        })
            if (File.Exists(probe)) return Path.GetFullPath(probe);

        var prefix = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_INSTALL_PREFIX");
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            var installed = Path.Combine(prefix.Trim(), "bin", exeName);
            if (File.Exists(installed)) return Path.GetFullPath(installed);
        }

        var declaredBuild = LaplaceInstall.TryGetProcessOrMachineSetting("LAPLACE_BUILD");
        if (!string.IsNullOrWhiteSpace(declaredBuild))
        {
            foreach (var sub in new[]
            {
                Path.Combine("Laplace-Engine", "icx-release", exeName),
                Path.Combine("Laplace-Engine", "icx-release", "src", exeName),
            })
            {
                var candidate = Path.Combine(declaredBuild.Trim(), sub);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }

        if (LaplaceInstall.TryDefaultBuildRoot(out var buildRoot))
        {
            foreach (var sub in new[]
            {
                Path.Combine("Laplace-Engine", "icx-release", exeName),
                Path.Combine("icx-release", exeName),
                Path.Combine("engine", exeName),
            })
            {
                var candidate = Path.Combine(buildRoot, sub);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static string Tail(MemoryStream error)
    {
        string said = Encoding.UTF8.GetString(error.ToArray()).Trim();
        return said.Length <= 2000 ? said : said[^2000..];
    }
}
