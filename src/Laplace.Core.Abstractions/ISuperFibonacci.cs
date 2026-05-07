namespace Laplace.Core.Abstractions;

/// <summary>
/// Super-Fibonacci spiral on S³ (Marc Alexa, CVPR 2022). Quasi-uniform low-
/// discrepancy sampling. Used to place every Unicode codepoint atom (full
/// 1,114,112 across 17 planes) at install time, ordered by
/// (script, general_category, UCA primary collation weight, Unihan radical for
/// CJK, codepoint integer). Single P/Invoke surface for the native
/// <c>SuperFibonacciService</c>.
/// </summary>
public interface ISuperFibonacci
{
    /// <summary>
    /// 4D unit-quaternion position for rank <paramref name="i"/> of
    /// <paramref name="total"/> samples on S³.
    /// </summary>
    Point4D At(int i, int total);

    /// <summary>
    /// Batch placement — emits the full sequence in one P/Invoke call.
    /// Used at codepoint seed time to place all 1,114,112 atoms.
    /// </summary>
    Point4D[] Range(int startInclusive, int endExclusive, int total);
}
