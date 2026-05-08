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
/// F2 IUnitDecomposition implementation. A unit-bearing literal like
/// "440Hz", "1024B", "3.14m", "60BPM" splits into a number portion and a
/// unit portion at the first non-numeric rune. Each portion routes through
/// F1 to a content-addressed composition entity:
///
///   "440Hz"  → ("440" → composition_440, "Hz" → composition_Hz)
///   "3.14m"  → ("3.14" → composition_3.14, "m" → composition_m)
///
/// This implementation returns the composition entity for the WHOLE literal
/// (e.g., "440Hz" as a single text composition). Splitting into separate
/// number/unit entities + a quantity edge joining them is a higher-tier
/// composition handled by callers that need dimensional analysis.
///
/// Per CLAUDE.md invariant 1+4: the unit "Hz" is itself a composition of
/// codepoints [h('H'), h('z')]. The same Hz entity is referenced from audio
/// frequency specs, EM spectra, biological rhythms — automatic cross-modal
/// dedup.
/// </summary>
public sealed class UnitDecomposer : IUnitDecomposition
{
    private readonly TextDecomposer _text;

    public UnitDecomposer(TextDecomposer text)
    {
        _text = text;
    }

    public async Task<AtomId> DecomposeAsync(
        string quantityLiteral,
        AtomId provenanceSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quantityLiteral);
        if (!ContainsNumericPrefix(quantityLiteral))
        {
            throw new ArgumentException(
                $"Quantity literal must begin with a numeric portion: '{quantityLiteral}'.",
                nameof(quantityLiteral));
        }
        return await _text.DecomposeAsync(quantityLiteral, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Splits a unit-bearing literal into its leading numeric portion and
    /// trailing unit portion. Whitespace between them (e.g., "3.14 m") is
    /// stripped from the unit side; the empty unit is permitted (a bare
    /// number is a degenerate unit-bearing literal).
    /// </summary>
    public static (string Number, string Unit) Split(string quantityLiteral)
    {
        if (string.IsNullOrEmpty(quantityLiteral)) { return (string.Empty, string.Empty); }
        var split = 0;
        var consumedDigit = false;
        foreach (var rune in quantityLiteral.EnumerateRunes())
        {
            if (Rune.IsDigit(rune))
            {
                consumedDigit = true;
                split += rune.Utf16SequenceLength;
                continue;
            }
            if (rune.Value is '+' or '-' or '.' or '/' or 'e' or 'E')
            {
                split += rune.Utf16SequenceLength;
                continue;
            }
            break;
        }
        if (!consumedDigit) { return (string.Empty, quantityLiteral); }

        var number = quantityLiteral[..split];
        var unit   = quantityLiteral[split..].TrimStart();
        return (number, unit);
    }

    private static bool ContainsNumericPrefix(string literal)
    {
        var (number, _) = Split(literal);
        return NumberDecomposer.IsValidNumberLiteral(number);
    }
}
