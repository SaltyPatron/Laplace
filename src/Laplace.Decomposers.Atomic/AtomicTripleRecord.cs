namespace Laplace.Decomposers.Atomic;

/// <summary>
/// One ATOMIC 2020 commonsense triple parsed from
/// <c>{train,dev,test}.tsv</c>. Format per Hwang 2021:
/// <code>
///   head_event \t relation \t tail
/// </code>
/// Head events use placeholder names <c>PersonX</c> / <c>PersonY</c> /
/// <c>PersonZ</c> for participants. Tails of literal <c>"none"</c> indicate
/// "no answer" and are excluded by the decomposer at emission time, not at
/// parse time, so the parser remains a pure streaming reader.
/// </summary>
public sealed record AtomicTripleRecord(string Head, string Relation, string Tail);
