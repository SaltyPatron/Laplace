using Laplace.Decomposers.Abstractions;
using Laplace.SubstrateCRUD.Npgsql;

namespace Laplace.Ingestion;

/// <summary>
/// The turns of a conversation, through the one forward program (Wiki Sequence 21. Sessions). Every surface
/// that converses, the OpenAI-compatible endpoints, MCP and the CLI, binds the same program to a session:
/// the Engine's <c>laplace turn</c> admits the prompt as content, reads the session's turns as discourse,
/// answers, and witnesses the turn itself, the participant's [session, turn] at its ordinal under
/// UserPromptContent and Laplace's [response, prompt] under ResponseContent. A turn another program
/// answered is recorded the same way by <c>laplace said</c>. Nothing here composes an identity, reads a
/// standing or writes a row.
///
/// The session is [participant, name]: the participant is a witness, the name the caller's session key.
/// Turns of one session take their ordinals in order, so a session's turns run one at a time; turns of
/// different sessions run at once.
/// </summary>
public sealed class ConversationTurns
{
    private const int Gates = 64;
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, Gates).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly string _connectionInfo;
    private readonly Lazy<NativeRecipeExecution> _engine;

    public ConversationTurns(string connectionString, Func<NativeRecipeExecution>? engine = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionInfo = NativeRecipeExecution.ConnectionInfoFromNpgsql(connectionString);
        _engine = new Lazy<NativeRecipeExecution>(engine ?? NativeRecipeExecution.Configured);
    }

    /// <summary>The installed Engine, or why there is none: a surface that cannot run a turn says so.</summary>
    public string? Unavailable()
    {
        try { _ = _engine.Value; return null; }
        catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        { return missing.Message; }
    }

    /// <summary>
    /// A turn of <paramref name="session"/>, answered by the forward program and witnessed by the Engine;
    /// with <paramref name="read"/>, answered and witnessed nowhere. <paramref name="step"/> sees each step
    /// as it is taken.
    /// </summary>
    public async Task<NativeTurnReceipt> TurnAsync(string participant, string session, string prompt,
        bool read = false, Action<NativeTurnStep>? step = null, CancellationToken ct = default)
    {
        var gate = Gate(participant, session);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _engine.Value.TurnAsync(_connectionInfo, participant, session, prompt, read, step, ct)
                .ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>A turn <paramref name="responder"/> answered, recorded as a turn of the session is.</summary>
    public async Task<NativeTurnReceipt> SaidAsync(string participant, string session, string responder,
        string prompt, string response, CancellationToken ct = default)
    {
        var gate = Gate(participant, session);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _engine.Value.SaidAsync(_connectionInfo, participant, session, responder, prompt, response, ct)
                .ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private SemaphoreSlim Gate(string participant, string session)
        => _gates[(int)((uint)HashCode.Combine(StringComparer.Ordinal.GetHashCode(participant),
            StringComparer.Ordinal.GetHashCode(session)) % Gates)];

    /// <summary>The installation's own conversation surfaces: the Ingest connection, since a turn is written.</summary>
    public static ConversationTurns Installed()
        => new(LaplaceDataSource.ConnectionStringFor(SubstrateAccess.Ingest));
}
