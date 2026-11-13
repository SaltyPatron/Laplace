using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;

namespace Laplace.Ingestion;

/// <summary>What the substrate holds of one upload: the file, its parts, and the witness trunk that holds it.</summary>
public sealed record UploadedFile(
    string FileId, string MetadataId, string ContentId, string WitnessId, string Participant, byte[] Content);

/// <summary>
/// A participant's uploads, on the Native substrate (Wiki Sequence 22.5, 22.6): an upload is ordinary digital content,
/// a file its participant states. The bytes are kept at their relative path under the participant's own directory of
/// the installation's data root, and the Engine reads the file as a runtime witness states its files (<c>laplace
/// witness --as PARTICIPANT --class UserPromptContent --root DIR</c>): the file is [metadata, content] through the
/// recipe its name matches, its pathname the relative path, and the trunk [participant, file] the witness. Reading one
/// back finds the file under a trunk that begins with the participant and realizes its content (Native lp_realize).
/// Nothing here composes an identity, decides a recipe or writes a row.
/// </summary>
public sealed class UserUploads
{
    /// <summary>The class an uploaded file is stated at (Laplace-Native <c>trust_classes.toml</c>).</summary>
    public const string TrustClass = "UserPromptContent";

    private const int Gates = 64;
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, Gates).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly string _connectionString;
    private readonly string _root;
    private readonly Func<NativeRecipeRequest, Action<NativeRecipeProgress>, CancellationToken, Task<(int Code, string Error)>> _execute;

    public UserUploads(string connectionString, string? root = null)
        : this(connectionString, root, ExecuteAsync)
    {
    }

    internal UserUploads(string connectionString, string? root,
        Func<NativeRecipeRequest, Action<NativeRecipeProgress>, CancellationToken, Task<(int Code, string Error)>> execute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _root = Path.GetFullPath(root ?? Path.Combine(LaplaceInstall.DeclaredIngestRoot(), "Uploads"));
        _execute = execute;
    }

    /// <summary>The installation's uploads, on the Ingest connection: an upload is written.</summary>
    public static UserUploads Installed() => new(LaplaceDataSource.ConnectionStringFor(SubstrateAccess.Ingest));

    /// <summary>
    /// A participant names a directory of its own: the identifier law of the conversation surfaces, never a name
    /// the file system reads as this directory or its parent.
    /// </summary>
    public static bool IsParticipant(string? participant)
        => ConversationContent.IsValidIdentifier(participant) && participant is not ("." or "..");

    /// <summary>A relative path: forward-slash segments, none empty, none a parent, never rooted.</summary>
    public static bool IsRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string normalized = path.Replace('\\', '/');
        return !Path.IsPathRooted(normalized) && !normalized.Contains(':')
            && !normalized.Split('/').Any(static segment => segment is "" or "." or "..");
    }

    /// <summary>The participant's directory: the root every file it states is named from.</summary>
    public string DirectoryOf(string participant)
    {
        if (!IsParticipant(participant)) throw new ArgumentException("The participant is not a valid identifier.", nameof(participant));
        return Path.Combine(_root, participant);
    }

    /// <summary>
    /// Keeps the bytes at the relative path under the participant's directory and has the Engine read the file as
    /// the participant states it. Returns the file's ID, or throws with what the Engine said. The same bytes at the
    /// same path again are the same file, recorded once.
    /// </summary>
    public async Task<string> AdmitAsync(string participant, string relativePath, byte[] content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0) throw new ArgumentException("An upload holds bytes.", nameof(content));
        if (!IsRelativePath(relativePath)) throw new ArgumentException("The path is not relative.", nameof(relativePath));
        _ = new UTF8Encoding(false, true).GetCharCount(content);
        string directory = DirectoryOf(participant);
        string path = Path.GetFullPath(Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("The path leaves the participant's directory.", nameof(relativePath));

        var gate = _gates[(int)((uint)StringComparer.Ordinal.GetHashCode(path) % Gates)];
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string partial = path + ".partial-" + Guid.NewGuid().ToString("N");
            await File.WriteAllBytesAsync(partial, content, ct).ConfigureAwait(false);
            File.Move(partial, path, overwrite: true);

            string? fileId = null;
            var request = new NativeRecipeRequest(NativeRecipeMode.Ingest, null, [path],
                ConnectionInfo: NativeRecipeExecution.ConnectionInfoFromNpgsql(_connectionString),
                ExpandPrerequisites: false,
                Witness: new NativeRuntimeWitness(participant, TrustClass, Root: directory));
            var (code, error) = await _execute(request, progress =>
            {
                if (progress.FileId is { Length: 32 } id) fileId = id;   /* the one file this run reads */
            }, ct).ConfigureAwait(false);
            if (code != 0) throw new InvalidOperationException($"The Engine did not record the upload ({code}): {error}");
            return fileId ?? throw new InvalidOperationException("The Engine recorded the upload without naming its file.");
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// The participant's file by its ID, read off the Native substrate (catalog <c>content.participant_file</c>): null
    /// when no trunk of that participant holds it.
    /// </summary>
    public static async Task<UploadedFile?> ReadAsync(NpgsqlDataSource dataSource, string participant, Hash128 fileId,
        CancellationToken ct = default)
    {
        if (!IsParticipant(participant)) return null;
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var command = NpgsqlCatalog.Command(connection, null, "content.participant_file",
            fileId.ToBytes(), participant);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        string Hex(int i) => Convert.ToHexStringLower((byte[])reader.GetValue(i));
        return new UploadedFile(Convert.ToHexStringLower(fileId.ToBytes()), Hex(0), Hex(1), Hex(2), participant,
            reader.IsDBNull(3) ? [] : Encoding.UTF8.GetBytes(reader.GetString(3)));
    }

    private static async Task<(int Code, string Error)> ExecuteAsync(NativeRecipeRequest request,
        Action<NativeRecipeProgress> progress, CancellationToken ct)
    {
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        int code = await NativeRecipeExecution.Configured().ExecuteAsync(request, output, error, ct, progress)
            .ConfigureAwait(false);
        string said = Encoding.UTF8.GetString(error.ToArray()).Trim();
        return (code, said.Length <= 2000 ? said : said[^2000..]);
    }
}
