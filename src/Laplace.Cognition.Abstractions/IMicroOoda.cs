namespace Laplace.Cognition.Abstractions;

/// <summary>
/// Per-traversal-step OODA wrapper around <c>ITraversal</c>. Each edge
/// consideration is its own OODA cycle: observe edge properties (type,
/// rating, RD, geometry), orient (does following advance the goal?), decide
/// (follow / backtrack / flag-for-later), act. Cost-free annotations attach
/// to the explanation trace.
/// </summary>
public interface IMicroOoda : IOodaLoop;
