namespace Laplace.Decomposers.Math;

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Laplace.Core.Abstractions;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Text;
using Laplace.Pipeline.Abstractions;

/// <summary>
/// F2 INumberDecomposition implementation. Numbers are NOT separate atom
/// types; they are content-addressed compositions of digit codepoint atoms.
/// "440" decomposes to [h('4'), h('0')] with rle counts [2, 1] — the same
/// composition entity regardless of where 440 appears (frequency, port,
/// calorie count, line number, etc.). This is what makes "how many things
/// intersect with 3.14?" first-class across math/code/text/sensor data.
///
/// Implementation: route the number literal through F1 ITextDecomposition.
/// The composition hash is identical to what F1 would produce for the same
/// literal as text — content addressing erases the distinction. F2 adds
/// shape validation (only digits + sign + decimal + fraction + exponent
/// markers permitted) so non-numeric input is rejected at the API boundary.
/// </summary>
public sealed class NumberDecomposer : INumberDecomposition
{
    private readonly TextDecomposer _text;

    public NumberDecomposer(TextDecomposer text)
    {
        _text = text;
    }

    public async Task<AtomId> DecomposeAsync(
        string numberLiteral,
        AtomId provenanceSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(numberLiteral);
        if (!IsValidNumberLiteral(numberLiteral))
        {
            throw new ArgumentException(
                $"Not a valid number literal: '{numberLiteral}'. " +
                "Permitted runes: digits 0-9, sign +/-, decimal '.', " +
                "fraction '/', exponent e/E.",
                nameof(numberLiteral));
        }
        return await _text.DecomposeAsync(numberLiteral, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True if every rune in <paramref name="literal"/> is part of an
    /// accepted number grammar: ASCII digits, ASCII Latin digits in other
    /// scripts (we accept the canonical ASCII set + Unicode Decimal Number
    /// general category), sign, decimal point, fraction slash, exponent
    /// marker. Empty input is invalid.
    /// </summary>
    public static bool IsValidNumberLiteral(string literal)
    {
        if (string.IsNullOrEmpty(literal)) { return false; }
        var seenDigit = false;
        foreach (var rune in literal.EnumerateRunes())
        {
            if (Rune.IsDigit(rune))
            {
                seenDigit = true;
                continue;
            }
            if (rune.Value is '+' or '-' or '.' or '/' or 'e' or 'E') { continue; }
            return false;
        }
        return seenDigit;
    }
}
