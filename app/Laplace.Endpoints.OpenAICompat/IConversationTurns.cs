using Laplace.Decomposers.Abstractions;
using Laplace.Ingestion;

namespace Laplace.Endpoints.OpenAICompat;

/// <summary>
/// The conversation surfaces' one program: a turn of a session answered and witnessed by the Engine
/// (<see cref="ConversationTurns"/>). The endpoints carry the caller's session to it and the receipt back.
/// </summary>
internal interface IConversationTurns
{
    /// <summary>Why no turn can run here (the Engine is not installed), or null.</summary>
    string? Unavailable { get; }

    Task<NativeTurnReceipt> TurnAsync(string participant, string session, string prompt,
        Action<NativeTurnStep>? step, CancellationToken ct);
}

/// <summary>The installation's Engine, on the substrate's Ingest connection: a turn writes.</summary>
internal sealed class InstalledConversationTurns : IConversationTurns
{
    private readonly Lazy<ConversationTurns> _turns = new(ConversationTurns.Installed);

    public string? Unavailable
    {
        get
        {
            try { return _turns.Value.Unavailable(); }
            catch (Exception missing) when (missing is InvalidOperationException or ArgumentException)
            { return missing.Message; }
        }
    }

    public Task<NativeTurnReceipt> TurnAsync(string participant, string session, string prompt,
        Action<NativeTurnStep>? step, CancellationToken ct)
        => _turns.Value.TurnAsync(participant, session, prompt, read: false, step, ct);
}
