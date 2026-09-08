using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

// Endpoint tests replace the persistence transport explicitly. Production has no
// availability override; database tests exercise the actual writer and commit.
internal sealed class RecordingConversationWitness : IConversationWitness
{
    private readonly ConcurrentDictionary<string,(string Tenant,Hash128 Session,string Prompt)> _prompts = new();
    public bool IsAvailable => true;

    public Task<string> RecordPromptAsync(string tenant,string? userKey,Hash128 sessionId,
        string prompt,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Assert.NotEqual(Hash128.Zero,sessionId);
        var occurrence = Guid.NewGuid().ToString("N");
        Assert.True(_prompts.TryAdd(occurrence,(tenant,sessionId,prompt)));
        return Task.FromResult(occurrence);
    }

    public Task RecordResponseAsync(string tenant,string? userKey,Hash128 sessionId,
        string prompt,string? reply,string occurrenceKey,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Assert.True(_prompts.TryRemove(occurrenceKey,out var input));
        Assert.Equal((tenant,sessionId,prompt),input);
        return Task.CompletedTask;
    }
}
