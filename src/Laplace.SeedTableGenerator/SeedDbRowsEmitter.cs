namespace Laplace.SeedTableGenerator;

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

/// <summary>
/// Emits the minimal entity_tier0 rows for the DB shadow population
/// (db-bootstrap COPY into entity partition). Per substrate invariant 1
/// (content-addressed identity) the DB needs the row to exist for FK
/// uniformity from edge_member.participant_hash → entity.entity_hash;
/// per invariant 2 (position is content-derived) the row carries no
/// position — that lives in physicality, populated separately.
///
/// TSV columns (no header — PG COPY ... FORMAT csv DELIMITER E'\t' style):
///   1. entity_hash_hex   — 64-char lowercase hex
///   2. tier              — 0 (always for codepoint atoms)
///   3. codepoint_int     — Unicode codepoint integer
///   4. content_hex       — UTF-8 bytes hex-encoded (the canonical content
///                          the entity_hash was computed over)
///   5. canonical_hash_hex — same as entity_hash_hex (denormalized for the
///                          index-only verification scans the schema notes)
///
/// One row per codepoint in the canonical ordering. Bootstrap script
/// COPYs this directly into entity_tier0 partition.
/// </summary>
public static class SeedDbRowsEmitter
{
    public static void Emit(IReadOnlyList<CodepointEntry> entries, string outputDir)
    {
        var path = Path.Combine(outputDir, "seed_db_rows.tsv");
        using var w = CHeaderWriter.OpenWriter(path);
        var sb = new StringBuilder(160);

        foreach (var e in entries)
        {
            var hashHex = ToHex(e.EntityHash.AsSpan());
            var utf8    = EncodeUtf8(e.Codepoint);

            sb.Clear();
            sb.Append(hashHex).Append('\t');
            sb.Append('0').Append('\t');
            sb.Append(e.Codepoint.ToString(CultureInfo.InvariantCulture)).Append('\t');
            sb.Append(ToHex(utf8)).Append('\t');
            sb.Append(hashHex);
            w.WriteLine(sb.ToString());
        }
    }

    private static string ToHex(System.ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static byte[] EncodeUtf8(int codepoint)
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
