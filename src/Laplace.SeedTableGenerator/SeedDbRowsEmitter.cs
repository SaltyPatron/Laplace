namespace Laplace.SeedTableGenerator;

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using Laplace.Core.Abstractions;

/// <summary>
/// Emits the bulk-loadable TSVs for the foundational substrate seed. ONE
/// TSV per receiving table, schema-aligned with the laplace_pg extension's
/// canonical schema (laplace_pg--0.1.0.sql) so PG COPY consumes them without
/// transformation. Per substrate invariant 1: every column that holds
/// identity is BLAKE3 bytes (emitted as `\x` + lowercase hex); per invariant 2:
/// every position is a POINT4D (emitted as POINT4D's text input format which
/// the type's input function parses).
///
/// Emitted files (under the configured output directory):
///   - entity_tier0.tsv         — codepoint atom rows
///   - physicality_atoms.tsv    — codepoint-atom partition positions on S^3
///
/// Phase A of the seed pipeline. Phase B (concept entities, entity_child
/// compositions, edge / edge_member property attestations, ISO 639 entities,
/// decomposition edges) lives in separate emitter classes.
///
/// PG COPY format: one tab-separated row per line, no header. Bytea columns
/// use the `\x` hex prefix per Postgres' bytea_output=hex convention.
/// Composite-type columns (point4d) use the type's text representation —
/// POINT4D(x y z w). NULL is emitted as the literal `\N` per COPY default.
/// </summary>
public static class SeedDbRowsEmitter
{
    /// <summary>physicality_type_hash for the substrate-atom partition.
    /// Computed as the BLAKE3 Merkle composition of the codepoint LINESTRING
    /// for the literal string "codepoint_s3_substrate". Per CLAUDE.md
    /// invariant 7: physicality_type IS itself a substrate entity.</summary>
    public static AtomId ComputeAtomPhysicalityTypeHash(
        IReadOnlyDictionary<int, AtomId> codepointHashes,
        IIdentityHashing hashing)
    {
        return ComposeNameHash("codepoint_s3_substrate", codepointHashes, hashing);
    }

    public static void Emit(
        IReadOnlyList<CodepointEntry> entries,
        IReadOnlyDictionary<int, AtomId> codepointHashes,
        IIdentityHashing hashing,
        string outputDir)
    {
        var atomPhysicalityTypeHash = ComputeAtomPhysicalityTypeHash(codepointHashes, hashing);

        EmitEntityTier0Tsv(entries, outputDir);
        EmitPhysicalityAtomsTsv(entries, atomPhysicalityTypeHash, outputDir);
    }

    /// <summary>
    /// entity_tier0.tsv — one row per codepoint atom. Schema (matches the
    /// entity table partition entity_tier0):
    ///   1. entity_hash       (bytea, \x + 64 hex)
    ///   2. tier              (smallint, always 0)
    ///   3. codepoint         (integer, the Unicode codepoint)
    ///   4. content           (bytea, \x + UTF-8 bytes hex of the codepoint)
    ///   5. centroid_4d       (point4d, POINT4D(x y z w))
    ///   6. trajectory        (linestring4d, NULL for tier-0)
    ///   7. prime_flags       (bigint)
    ///   8. structural_flags  (smallint, 0 at tier-0)
    /// </summary>
    private static void EmitEntityTier0Tsv(IReadOnlyList<CodepointEntry> entries, string outputDir)
    {
        var path = Path.Combine(outputDir, "entity_tier0.tsv");
        using var w = CHeaderWriter.OpenWriter(path);
        var sb = new StringBuilder(256);

        foreach (var e in entries)
        {
            var utf8 = EncodeUtf8(e.Codepoint);

            sb.Clear();
            sb.Append(@"\x").Append(ToHexLower(e.EntityHash.AsSpan())).Append('\t');
            sb.Append('0').Append('\t');
            sb.Append(e.Codepoint.ToString(CultureInfo.InvariantCulture)).Append('\t');
            sb.Append(@"\x").Append(ToHexLower(utf8)).Append('\t');
            sb.Append("POINT4D(")
              .Append(FormatDouble(e.Position.X)).Append(' ')
              .Append(FormatDouble(e.Position.Y)).Append(' ')
              .Append(FormatDouble(e.Position.Z)).Append(' ')
              .Append(FormatDouble(e.Position.W))
              .Append(')').Append('\t');
            sb.Append(@"\N").Append('\t'); // trajectory: NULL for tier-0
            sb.Append(unchecked((long)e.PrimeFlags).ToString(CultureInfo.InvariantCulture)).Append('\t');
            sb.Append('0'); // structural_flags
            w.WriteLine(sb.ToString());
        }
    }

    /// <summary>
    /// physicality_atoms.tsv — one row per codepoint atom in the substrate-
    /// atom physicality partition. Schema (matches physicality table):
    ///   1. physicality_type_hash (bytea, the codepoint_s3_substrate type)
    ///   2. entity_hash           (bytea, the codepoint atom hash)
    ///   3. entity_tier           (smallint, 0)
    ///   4. position              (point4d)
    ///   5. bbox                  (box4d, NULL — tier-0 atoms are points)
    ///   6. hilbert_index         (bigint)
    /// </summary>
    private static void EmitPhysicalityAtomsTsv(
        IReadOnlyList<CodepointEntry> entries,
        AtomId atomPhysicalityTypeHash,
        string outputDir)
    {
        var path = Path.Combine(outputDir, "physicality_atoms.tsv");
        using var w = CHeaderWriter.OpenWriter(path);
        var sb = new StringBuilder(256);
        var typeHashHex = ToHexLower(atomPhysicalityTypeHash.AsSpan());

        foreach (var e in entries)
        {
            sb.Clear();
            sb.Append(@"\x").Append(typeHashHex).Append('\t');
            sb.Append(@"\x").Append(ToHexLower(e.EntityHash.AsSpan())).Append('\t');
            sb.Append('0').Append('\t');
            sb.Append("POINT4D(")
              .Append(FormatDouble(e.Position.X)).Append(' ')
              .Append(FormatDouble(e.Position.Y)).Append(' ')
              .Append(FormatDouble(e.Position.Z)).Append(' ')
              .Append(FormatDouble(e.Position.W))
              .Append(')').Append('\t');
            sb.Append(@"\N").Append('\t'); // bbox NULL
            sb.Append(unchecked((long)e.HilbertIndex).ToString(CultureInfo.InvariantCulture));
            w.WriteLine(sb.ToString());
        }
    }

    internal static AtomId ComposeNameHash(
        string name,
        IReadOnlyDictionary<int, AtomId> codepointHashes,
        IIdentityHashing hashing)
    {
        var children = new List<AtomId>(name.Length);
        var counts   = new List<int>(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            if (codepointHashes.TryGetValue(rune.Value, out var hash))
            {
                children.Add(hash);
                counts.Add(1);
            }
        }
        return hashing.CompositionId(children, counts);
    }

    internal static string ToHexLower(System.ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    internal static string FormatDouble(double v) =>
        v.ToString("G17", CultureInfo.InvariantCulture);

    internal static byte[] EncodeUtf8(int codepoint)
    {
        if (codepoint < 0 || codepoint > 0x10FFFF)
        {
            return System.BitConverter.GetBytes(codepoint);
        }
        if (System.Text.Rune.IsValid(codepoint))
        {
            var rune = new System.Text.Rune(codepoint);
            System.Span<byte> buf = stackalloc byte[4];
            var written = rune.EncodeToUtf8(buf);
            return buf[..written].ToArray();
        }
        return new byte[]
        {
            (byte)((codepoint >> 24) & 0xFF),
            (byte)((codepoint >> 16) & 0xFF),
            (byte)((codepoint >>  8) & 0xFF),
            (byte)( codepoint        & 0xFF),
        };
    }
}
