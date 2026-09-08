using Laplace.Engine.Core;

namespace Laplace.Endpoints.OpenAICompat;

internal interface IConversationWitness
{
    bool IsAvailable { get; }
    Task<string> RecordPromptAsync(string tenant, string? userKey, Hash128 sessionId,
        string prompt, CancellationToken ct);
    Task RecordResponseAsync(string tenant, string? userKey, Hash128 sessionId,
        string prompt, string? reply, string occurrenceKey, CancellationToken ct);
}
