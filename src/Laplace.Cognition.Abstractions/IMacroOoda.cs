namespace Laplace.Cognition.Abstractions;

using System.Threading;
using System.Threading.Tasks;
using Laplace.Core.Abstractions;

/// <summary>
/// Background or scheduled long-horizon OODA. Hypothesis-driven exploration
/// via Fréchet trajectory matching across analogous entity neighborhoods.
/// Frayed-edge surveys. Source-ingestion proposals when incompleteness signals
/// fire. Persistent task state across sessions — long-horizon goals like
/// "Cure Cancer" can churn for days/weeks accumulating partial answers.
/// </summary>
public interface IMacroOoda
{
    Task<string> SubmitAsync(string goal, CancellationToken cancellationToken);

    Task<MacroOodaState> GetStateAsync(string taskId, CancellationToken cancellationToken);

    Task CancelAsync(string taskId, CancellationToken cancellationToken);
}

public record MacroOodaState(
    string TaskId,
    string Goal,
    string Status,
    int CompletedCycles,
    AtomId? CurrentBestAnswer,
    System.DateTimeOffset LastActivity);
