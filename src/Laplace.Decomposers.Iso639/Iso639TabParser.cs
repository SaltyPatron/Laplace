namespace Laplace.Decomposers.Iso639;

using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Parses the four tab-separated files of the ISO 639-3 dataset.
/// Phase 3 / Track E / E1.
///
/// Pure data extraction — no substrate side effects. The Iso639Decomposer
/// (Track E / E5) consumes these records and emits substrate entities +
/// property edges via the canonical pipeline (D6 / D7).
/// </summary>
public sealed class Iso639TabParser
{
    /// <summary>Parse iso-639-3.tab. Yields one record per data line.</summary>
    public static IEnumerable<Iso639LanguageRecord> ParseLanguages(string path)
    {
        using var reader = new StreamReader(path);
        var header = reader.ReadLine();
        if (header is null)
        {
            yield break;
        }
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split('\t');
            if (parts.Length < 7)
            {
                continue;
            }
            yield return new Iso639LanguageRecord(
                Id:            parts[0],
                Part2b:        EmptyToNull(parts[1]),
                Part2t:        EmptyToNull(parts[2]),
                Part1:         EmptyToNull(parts[3]),
                Scope:         ParseScope(parts[4]),
                Type:          ParseLanguageType(parts[5]),
                ReferenceName: parts[6],
                Comment:       parts.Length > 7 ? EmptyToNull(parts[7]) : null);
        }
    }

    public static IEnumerable<Iso639NameRecord> ParseNames(string path)
    {
        using var reader = new StreamReader(path);
        var header = reader.ReadLine();
        if (header is null)
        {
            yield break;
        }
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }
            yield return new Iso639NameRecord(parts[0], parts[1], parts[2]);
        }
    }

    public static IEnumerable<Iso639MacrolanguageRecord> ParseMacrolanguages(string path)
    {
        using var reader = new StreamReader(path);
        var header = reader.ReadLine();
        if (header is null)
        {
            yield break;
        }
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }
            yield return new Iso639MacrolanguageRecord(
                MacrolanguageId: parts[0],
                IndividualId:    parts[1],
                Status:          parts[2] == "R" ? Iso639MacrolanguageStatus.Retired
                                                 : Iso639MacrolanguageStatus.Active);
        }
    }

    private static string? EmptyToNull(string s) => string.IsNullOrEmpty(s) ? null : s;

    private static Iso639Scope ParseScope(string s) => s switch
    {
        "I" => Iso639Scope.Individual,
        "M" => Iso639Scope.Macrolanguage,
        "S" => Iso639Scope.Special,
        _   => throw new FormatException($"Unknown ISO 639-3 scope code: '{s}'"),
    };

    private static Iso639LanguageType ParseLanguageType(string s) => s switch
    {
        "A" => Iso639LanguageType.Ancient,
        "C" => Iso639LanguageType.Constructed,
        "E" => Iso639LanguageType.Extinct,
        "H" => Iso639LanguageType.Historical,
        "L" => Iso639LanguageType.Living,
        "S" => Iso639LanguageType.Special,
        _   => throw new FormatException($"Unknown ISO 639-3 type code: '{s}'"),
    };
}
