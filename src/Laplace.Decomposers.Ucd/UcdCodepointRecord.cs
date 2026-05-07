namespace Laplace.Decomposers.Ucd;

using System.Collections.Generic;

/// <summary>
/// One <c>&lt;char&gt;</c> from <c>ucd.all.flat.xml</c> (UAX #42 — Unicode
/// Character Database in XML). Carries every UCD property as an attribute,
/// PLUS Unihan properties for CJK Unified Ideographs, PLUS name aliases.
/// One canonical source for every per-codepoint substrate property edge —
/// supersedes parsing the per-property `.txt` files individually.
///
/// Phase 3 / Track E / E2.
///
/// Range entries (large blocks like CJK Unified Ideographs Extension B,
/// PUA, Surrogates) carry FirstCodepoint/LastCodepoint instead of a single
/// codepoint; the seeder enumerates the range at seed time, applying the
/// shared properties to each codepoint in [first, last].
///
/// All UCD attributes are exposed verbatim via <see cref="Properties"/>.
/// Convenience accessors hoist the most common ones (gc, sc, blk, age,
/// dt, dm, nv, na, na1) for ergonomic queries; use <see cref="Properties"/>
/// to access anything else by its UCD short name.
///
/// Sentinel conventions per UAX #42:
///   "#"   in a mapping field means "same codepoint as cp"
///   ""    means absent / no value
///   "NaN" means absent numeric value
/// </summary>
public sealed record UcdCodepointRecord(
    int FirstCodepoint,
    int LastCodepoint,
    IReadOnlyDictionary<string, string> Properties,
    IReadOnlyList<UcdNameAlias> NameAliases)
{
    /// <summary>True for range entries (e.g., CJK Ideographs blocks).</summary>
    public bool IsRange => FirstCodepoint != LastCodepoint;

    public string? Get(string ucdShortName) =>
        Properties.TryGetValue(ucdShortName, out var v) ? v : null;

    /// <summary>General_Category (e.g., "Lu", "Ll", "Nd", "Cc"). UCD short name "gc".</summary>
    public string? GeneralCategory => Get("gc");

    /// <summary>Script (e.g., "Latn", "Cyrl", "Hani"). UCD short name "sc".</summary>
    public string? Script => Get("sc");

    /// <summary>Script_Extensions list. UCD short name "scx".</summary>
    public string? ScriptExtensions => Get("scx");

    /// <summary>Block (e.g., "ASCII", "CJK_Unified_Ideographs"). UCD short name "blk".</summary>
    public string? Block => Get("blk");

    /// <summary>Age — Unicode version of introduction. UCD short name "age".</summary>
    public string? Age => Get("age");

    /// <summary>Decomposition_Type (e.g., "none", "can", "compat", "font"). UCD short name "dt".</summary>
    public string? DecompositionType => Get("dt");

    /// <summary>Decomposition_Mapping (sequence of codepoint hex strings). UCD short name "dm".</summary>
    public string? DecompositionMapping => Get("dm");

    /// <summary>Numeric_Value (e.g., "1/2", "100", "NaN"). UCD short name "nv".</summary>
    public string? NumericValue => Get("nv");

    /// <summary>Name (Unicode 2.0+). UCD short name "na".</summary>
    public string? Name => Get("na");

    /// <summary>Unicode 1 Name. UCD short name "na1".</summary>
    public string? Name1 => Get("na1");

    /// <summary>Canonical_Combining_Class (string form, e.g., "0", "230"). UCD short name "ccc".</summary>
    public string? CanonicalCombiningClass => Get("ccc");

    /// <summary>Bidi_Class (e.g., "L", "R", "ON"). UCD short name "bc".</summary>
    public string? BidiClass => Get("bc");
}

public sealed record UcdNameAlias(string Alias, string Type);
