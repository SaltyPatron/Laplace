using System.Collections.Concurrent;
using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

// Endpoint tests replace the Engine process, the physical provider, and keep every endpoint
// law above it: the session the caller resolves, the steps streamed in order, the receipt carried
// back. Production runs the installed Engine (InstalledConversationTurns); the database check is
// the deployed pipeline's.
internal sealed class RecordingConversationTurns : IConversationTurns
{
    public ConcurrentQueue<(string Participant, string Session, string Prompt)> Turns { get; } = new();
    public string? Unavailable => null;

    public Task<NativeTurnReceipt> TurnAsync(string participant, string session, string prompt,
        Action<NativeTurnStep>? step, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Assert.False(string.IsNullOrWhiteSpace(participant));
        Assert.False(string.IsNullOrWhiteSpace(session));
        Turns.Enqueue((participant, session, prompt));
        var steps = new List<NativeTurnStep>();
        if (!prompt.Contains("unknown-topic", StringComparison.OrdinalIgnoreCase))
        {
            string[] words = ["the", " whale", " sings"];
            for (int i = 0; i < words.Length; i++)
            {
                var s = new NativeTurnStep(i + 1, "answers", $"{i + 1:x32}", words[i], 2.0, i, 0.5);
                steps.Add(s);
                step?.Invoke(s);
                if (i == 0 && prompt.Contains("trigger-stream-error", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The Engine's turn failed (1): the database went away.");
            }
        }
        string response = string.Concat(steps.Select(s => s.Text));
        bool witnessed = response.Length > 0;
        return Task.FromResult(new NativeTurnReceipt(
            participant, session, new string('a', 32), witnessed ? new string('b', 32) : null,
            witnessed ? new string('c', 32) : null, 1, 0, response,
            witnessed ? "complete: every obligation is grounded" : "unresolved: what follows grounds nothing that is owed",
            witnessed, null, witnessed ? null : "nothing emitted: nothing to witness", steps,
            "RESOLVE    prompt\nREALIZE    \"" + response + "\"\n"));
    }
}

/// <summary>A host with no Engine installed: every turn is refused before it is billed.</summary>
internal sealed class MissingConversationTurns : IConversationTurns
{
    public string? Unavailable => "The canonical Native recipe Engine is not installed or built.";

    public Task<NativeTurnReceipt> TurnAsync(string participant, string session, string prompt,
        Action<NativeTurnStep>? step, CancellationToken ct) =>
        throw new InvalidOperationException(Unavailable);
}
