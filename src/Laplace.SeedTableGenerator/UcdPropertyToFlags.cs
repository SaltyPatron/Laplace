namespace Laplace.SeedTableGenerator;

using System;

using Laplace.Core.Abstractions;

/// <summary>
/// Maps UCD per-codepoint properties to the substrate's PrimeFlags bitmask.
/// Tier-0 codepoint atoms get a SPARSE flag set — at this layer the
/// substrate only knows what UCD tells it about a single codepoint. Rich
/// grammatical/semantic flags (Noun, Animate, Past, etc.) populate at
/// tier-1+ when decomposers ingest source-tagged corpora.
/// </summary>
public static class UcdPropertyToFlags
{
    /// <summary>
    /// Derive the prime_flags bitmask for a single codepoint from its UCD
    /// general_category (+ any other deterministically-derivable signals).
    /// </summary>
    public static ulong FromUcd(string generalCategory)
    {
        ulong flags = PrimeFlags.Text;          // every Unicode codepoint is a TEXT-modality element by construction

        if (string.IsNullOrEmpty(generalCategory))
        {
            return flags;
        }

        // UCD general_category short codes are 2 chars: first letter is the
        // major class, second is the sub-class (e.g., "Lu" = Letter,
        // uppercase; "Po" = Punctuation, other). Match on the major class.
        var major = char.ToUpperInvariant(generalCategory[0]);
        switch (major)
        {
            case 'P':                            // Pc Pd Pe Pf Pi Po Ps — punctuation
                flags |= PrimeFlags.Punctuation;
                break;
            case 'N':                            // Nd Nl No — numeric
                flags |= PrimeFlags.Numeral | PrimeFlags.Quantity;
                break;
            case 'S':                            // Sc Sk Sm So — symbols
                if (generalCategory.Length > 1 && char.ToUpperInvariant(generalCategory[1]) == 'M')
                {
                    flags |= PrimeFlags.Math;    // Sm = math symbol
                }
                break;
            // Letters (L*), marks (M*), separators (Z*), and others (C*)
            // get only the modality bit at tier-0; richer categorization
            // emerges from decomposer-ingested data.
        }

        return flags;
    }
}
