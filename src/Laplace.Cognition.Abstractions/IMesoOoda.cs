namespace Laplace.Cognition.Abstractions;

/// <summary>
/// Per-query OODA: query decomposition (Tree-of-Thought), partial-result
/// synthesis, retry with reflection (Reflexion), reason+act+observe (ReAct),
/// N parallel paths with consensus aggregation (Self-Consistency via Voronoi
/// over result trajectories).
/// </summary>
public interface IMesoOoda : IOodaLoop;
