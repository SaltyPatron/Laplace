namespace Laplace.Cognition.Abstractions;

using System.Threading;
using System.Threading.Tasks;
using Laplace.Core.Abstractions;

/// <summary>
/// One Observe → Orient → Decide → Act cycle. The Gödel Engine (the
/// behavioral engine) is composed of OODA cycles at three scales (micro per
/// traversal step, meso per query, macro background scheduled long-horizon).
/// </summary>
public interface IOodaLoop
{
    Task<OodaResult> ExecuteAsync(OodaContext context, CancellationToken cancellationToken);
}

public record OodaContext(
    AtomId? CurrentEntity,
    string Goal,
    int IterationDepth);

public record OodaResult(
    string Decision,
    string Action,
    AtomId? NextEntity,
    bool ShouldHalt);
