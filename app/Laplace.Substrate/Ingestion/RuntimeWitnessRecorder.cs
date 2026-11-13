using System.Text;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;

namespace Laplace.Ingestion;

/// <summary>
/// One claim a runtime witness states (Laplace-Engine <c>recipes/statement.recipe</c>): the claim
/// [subject, relation, object], or [subject, relation] when <see cref="Object"/> is empty; each part the text
/// it is, the relation the witness's own word for it; the score its outcome, 0 (it does not hold) to 1.
/// </summary>
public sealed record WitnessStatement(string Subject, string Relation, string Object, double Score);

/// <summary>What the Engine recorded: the file it read, and the witness trunk and file trunk it reported.</summary>
public sealed record RuntimeWitnessReceipt(string Path, string? WitnessId, string? FileId);

/// <summary>
/// What a witness named at run time states, recorded the way a source is (Laplace-Engine <c>laplace witness
/// --as NAME --class CLASS FILE</c>): the statements, or a file of content, are written under the declared ingest
/// root at a path named by the BLAKE3 of the bytes, and the Engine reads the file through the recipe its kind
/// matches, composes its trunk [the witness's name, the file's trunk], attests what it says at the class's prior
/// and folds the standings in the database. Stated again, the same bytes are the same file and the same trunk,
/// and nothing is witnessed again. Nothing here composes an identity, reads a standing or writes a row.
/// </summary>
public sealed class RuntimeWitnessRecorder
{
    private readonly string _connectionInfo;
    private readonly string _root;
    private readonly Func<NativeRecipeRequest, Stream, Stream, Action<NativeRecipeProgress>, CancellationToken, Task<int>> _execute;

    public RuntimeWitnessRecorder(string connectionString, string? root = null)
        : this(connectionString, root, static (request, output, error, progress, ct)
            => NativeRecipeExecution.Configured().ExecuteAsync(request, output, error, ct, progress))
    {
    }

    internal RuntimeWitnessRecorder(string connectionString, string? root,
        Func<NativeRecipeRequest, Stream, Stream, Action<NativeRecipeProgress>, CancellationToken, Task<int>> execute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionInfo = NativeRecipeExecution.ConnectionInfoFromNpgsql(connectionString);
        _root = root ?? Path.Combine(LaplaceInstall.DeclaredIngestRoot(), "Witnesses");
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    /// <summary>The statements of a witness, one claim a line (<see cref="StatementText"/>).</summary>
    public Task<RuntimeWitnessReceipt> StateAsync(NativeRuntimeWitness witness,
        IReadOnlyList<WitnessStatement> statements, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(statements);
        if (statements.Count == 0) throw new ArgumentException("A statement states at least one claim.", nameof(statements));
        return RecordAsync(witness, "witness.statement", Encoding.UTF8.GetBytes(StatementText(statements)), ct);
    }

    /// <summary>
    /// A file of content the witness gives (a note, a program, a measurement's receipt), read by the recipe its
    /// name's extension matches.
    /// </summary>
    public async Task<RuntimeWitnessReceipt> RecordAsync(NativeRuntimeWitness witness, string name, byte[] content,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(witness);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0) throw new ArgumentException("A witness records content.", nameof(content));
        if (name != Path.GetFileName(name) || name.StartsWith('.'))
            throw new ArgumentException("A witness's file is named by a plain file name.", nameof(name));

        string directory = Path.Combine(_root, Convert.ToHexStringLower(Hash128.Blake3(content).ToBytes()));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        if (!File.Exists(path))
        {
            string partial = path + ".partial";
            await File.WriteAllBytesAsync(partial, content, ct).ConfigureAwait(false);
            File.Move(partial, path, overwrite: true);
        }

        var request = new NativeRecipeRequest(NativeRecipeMode.Ingest, null, [path],
            ConnectionInfo: _connectionInfo, ExpandPrerequisites: false, Witness: witness);
        string? witnessId = null, fileId = null;
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        int code = await _execute(request, output, error, progress =>
        {
            if (progress.FileId is { Length: > 0 } file) fileId = file;
            if (progress.SourceId is { Length: > 0 } source) witnessId = source;
        }, ct).ConfigureAwait(false);
        if (code != 0)
            throw new InvalidOperationException($"The Engine did not record {witness.Name}'s {name} ({code}): {Tail(error)}");
        return new RuntimeWitnessReceipt(path, witnessId, fileId);
    }

    /// <summary>
    /// The statement file's text: a claim a line, its parts between tabs, a tab, line break or backslash in a part
    /// written after a backslash (statement.recipe <c>escaped \</c>), the score in [0, 1] as a number.
    /// </summary>
    public static string StatementText(IReadOnlyList<WitnessStatement> statements)
    {
        var text = new StringBuilder();
        foreach (var s in statements)
        {
            if (string.IsNullOrEmpty(s.Subject) || string.IsNullOrEmpty(s.Relation))
                throw new ArgumentException("A claim has a subject and a relation.", nameof(statements));
            if (!double.IsFinite(s.Score) || s.Score is < 0 or > 1)
                throw new ArgumentException("A claim's score is from 0 to 1.", nameof(statements));
            text.Append(Escape(s.Subject)).Append('\t').Append(Escape(s.Relation)).Append('\t')
                .Append(Escape(s.Object ?? "")).Append('\t')
                .Append(s.Score.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }
        return text.ToString();
    }

    private static string Escape(string part)
    {
        var escaped = new StringBuilder(part.Length);
        foreach (char c in part)
        {
            if (c is '\\' or '\t' or '\n' or '\r') escaped.Append('\\');
            escaped.Append(c);
        }
        return escaped.ToString();
    }

    private static string Tail(MemoryStream error)
    {
        string said = Encoding.UTF8.GetString(error.ToArray()).Trim();
        return said.Length <= 2000 ? said : said[^2000..];
    }

    /// <summary>The installation's own witnesses, on the substrate's Ingest connection: a statement writes.</summary>
    public static RuntimeWitnessRecorder Installed()
        => new(Laplace.SubstrateCRUD.Npgsql.LaplaceDataSource.ConnectionStringFor(Laplace.SubstrateCRUD.Npgsql.SubstrateAccess.Ingest));
}
