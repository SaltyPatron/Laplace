namespace Laplace.Cognition.Abstractions;

using System.Threading;
using System.Threading.Tasks;
using Laplace.Core.Abstractions;

/// <summary>
/// THE Gödel Engine — the behavioral engine. Surface where all reasoning
/// patterns live, composed of OODA cycles at three scales (micro per
/// traversal step, meso per query, macro background scheduled long-horizon).
/// AGI/ASI capability emerges here.
///
/// Subsumes patterns conventional AI bolts on as separate techniques:
/// Chain-of-Thought, Tree-of-Thought, ReAct, Reflexion, Self-Consistency,
/// Graph-of-Thought, hypothesis-driven reasoning, self-questioning
/// (Gödelian incompleteness), goal decomposition, honest abstention,
/// long-horizon churning, analogy, abduction, meta-cognition.
///
/// Without this engine the substrate is a database. With it the substrate is
/// the cognition surface for AGI/ASI.
/// </summary>
public interface IGodelEngine
{
    /// <summary>
    /// Execute a query through the Gödel Engine. Selects appropriate
    /// behavioral patterns based on goal characteristics, runs OODA cycles at
    /// the appropriate scales, and returns ranked results with full
    /// provenance trace + OODA-cycle annotations + active behavioral pattern
    /// information.
    /// </summary>
    Task<GodelResult> ExecuteAsync(
        GodelQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Submit a long-horizon task to MacroOoda. The task persists across
    /// session restarts; results accumulate over time; frayed-edge signals
    /// surface for ingestion proposals. Use for goals like "Cure Cancer"
    /// that require sustained churning, hypothesis exploration, and
    /// substrate enrichment.
    /// </summary>
    Task<GodelTaskHandle> SubmitLongHorizonAsync(
        GodelQuery query,
        CancellationToken cancellationToken);
}

public record GodelQuery(
    string Description,
    AtomId? Context,
    int? MaxDepth,
    double? CostBudget,
    IReadOnlySet<string>? PreferredBehaviors);

public record GodelResult(
    AtomId? AnswerEntity,
    IReadOnlyList<TraversalTrace> Traces,
    IReadOnlyList<string> ActiveBehaviors,
    IReadOnlyList<IncompletenessSignal> Incompleteness);

public record TraversalTrace(
    string OodaScale,
    string Step,
    AtomId? CurrentEntity,
    double? Cost);

public record IncompletenessSignal(
    string Kind,
    string Detail,
    AtomId? RelatedEntity);

public record GodelTaskHandle(string TaskId, string PersistencePath);
