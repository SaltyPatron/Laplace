using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Chess.Service;

/// <summary>
/// Records a game Laplace played (a play session, a Lichess game, a lab match, a self-play game) the way
/// every game is recorded: as one PGN file the Engine reads, stated by the runtime witness Laplace at
/// AppDerived (Laplace-Engine <c>laplace witness</c>; the witness is the trunk [Laplace, the file's trunk]).
/// The Engine composes the record [line, tags, layer], attests each [position, move, position] with the
/// result scored for the side that moved, and folds the standings; nothing here composes an identity or
/// writes a row. A game is admitted after it ends (Sequence 27.9).
///
/// Each game is its own file under the laplace-chess source's root (<c>$LAPLACE_DATA/Games/Chess/laplace</c>,
/// or <c>LAPLACE_CHESS_GAMES</c>), named by the BLAKE3 of its text, so recording the same game again states
/// the same file under the same lineage and adds no second voice.
/// </summary>
public sealed class ChessGameRecorder
{
    /// <summary>The witness of the games Laplace plays, as <c>recipes/laplace-chess/source</c> names it.</summary>
    public const string Witness = "Laplace";

    /// <summary>The class an application's own output states at (Laplace-Native <c>trust_classes.toml</c>).</summary>
    public const string TrustClass = "AppDerived";

    private readonly string _connectionString;
    private readonly string _directory;
    private readonly Func<NativeRecipeRequest, Stream, Stream, CancellationToken, Task<int>> _execute;

    public ChessGameRecorder(string connectionString, string? directory = null)
        : this(connectionString, directory, static (request, output, error, ct)
            => NativeRecipeExecution.Configured().ExecuteAsync(request, output, error, ct))
    {
    }

    internal ChessGameRecorder(
        string connectionString, string? directory,
        Func<NativeRecipeRequest, Stream, Stream, CancellationToken, Task<int>> execute)
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
    public async Task IngestFileAsync(string? witness, string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var request = new NativeRecipeRequest(NativeRecipeMode.Ingest, null, [path],
            ConnectionInfo: NativeRecipeExecution.ConnectionInfoFromNpgsql(_connectionString),
            ExpandPrerequisites: false,
            Witness: witness is null ? null : new NativeRuntimeWitness(witness, TrustClass));
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        int code = await _execute(request, output, error, ct).ConfigureAwait(false);
        if (code != 0)
            throw new InvalidOperationException($"The Engine did not record {path} ({code}): {Tail(error)}");
    }

    private static string Tail(MemoryStream error)
    {
        string said = Encoding.UTF8.GetString(error.ToArray()).Trim();
        return said.Length <= 2000 ? said : said[^2000..];
    }
}
