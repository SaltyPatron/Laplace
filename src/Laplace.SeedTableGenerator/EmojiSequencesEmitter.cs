namespace Laplace.SeedTableGenerator;

using System.Collections.Generic;
using System.Globalization;
using System.IO;

using Laplace.Core.Abstractions;
using Laplace.Decomposers.Ucd;

/// <summary>
/// Emits multi-codepoint emoji sequence entries with their composition
/// entity hashes (computed via Merkle composition of constituent codepoint
/// hashes with RLE counts of 1). Single-codepoint emoji entries are
/// covered by the codepoint table — only true multi-cp sequences appear
/// here.
/// </summary>
public static class EmojiSequencesEmitter
{
    public static void Emit(
        IEnumerable<EmojiSequenceEntry> emojiEntries,
        IReadOnlyDictionary<int, AtomId> codepointHashes,
        IIdentityHashing hashing,
        string outputDir)
    {
        var sequences = new List<(IReadOnlyList<int> seq, AtomId hash, string typeTag, string? description)>();
        foreach (var e in emojiEntries)
        {
            if (e.Sequence is null || e.Sequence.Count < 2)
            {
                continue;
            }
            var children = new List<AtomId>(e.Sequence.Count);
            var counts   = new List<int>(e.Sequence.Count);
            var skip = false;
            foreach (var cp in e.Sequence)
            {
                if (!codepointHashes.TryGetValue(cp, out var hash))
                {
                    skip = true;
                    break;
                }
                children.Add(hash);
                counts.Add(1);
            }
            if (skip)
            {
                continue;
            }
            sequences.Add((e.Sequence, hashing.CompositionId(children, counts), e.TypeTag, e.Description));
        }

        EmitHeader(sequences.Count, outputDir);
        EmitSource(sequences, outputDir);
    }

    private static void EmitHeader(int count, string outputDir)
    {
        var path = Path.Combine(outputDir, "emoji_sequences.h");
        using var w = CHeaderWriter.OpenWriter(path);
        w.Write(CHeaderWriter.LicenseBanner);
        w.WriteLine("#ifndef LAPLACE_EMOJI_SEQUENCES_H");
        w.WriteLine("#define LAPLACE_EMOJI_SEQUENCES_H");
        w.WriteLine();
        w.WriteLine("#include <stdint.h>");
        w.WriteLine();
        w.Write("#define LAPLACE_EMOJI_SEQUENCES_COUNT ");
        w.WriteLine(count.ToString(CultureInfo.InvariantCulture));
        w.WriteLine();
        w.WriteLine("typedef struct {");
        w.WriteLine("    uint8_t  hash[32];      /* composition entity_hash for this sequence */");
        w.WriteLine("    uint16_t sequence_offset; /* offset into LAPLACE_EMOJI_CODEPOINTS */");
        w.WriteLine("    uint8_t  sequence_length;");
        w.WriteLine("    uint8_t  type_tag;      /* 0=Basic_Emoji 1=Emoji_Keycap_Sequence");
        w.WriteLine("                             * 2=RGI_Emoji_Modifier_Sequence");
        w.WriteLine("                             * 3=RGI_Emoji_Flag_Sequence");
        w.WriteLine("                             * 4=RGI_Emoji_Tag_Sequence");
        w.WriteLine("                             * 5=RGI_Emoji_ZWJ_Sequence");
        w.WriteLine("                             * 255=Other */");
        w.WriteLine("} laplace_emoji_sequence_entry_t;");
        w.WriteLine();
        w.WriteLine("extern const laplace_emoji_sequence_entry_t");
        w.WriteLine("    LAPLACE_EMOJI_SEQUENCES[LAPLACE_EMOJI_SEQUENCES_COUNT];");
        w.WriteLine("extern const int32_t LAPLACE_EMOJI_CODEPOINTS[];");
        w.WriteLine();
        w.WriteLine("#endif /* LAPLACE_EMOJI_SEQUENCES_H */");
    }

    private static void EmitSource(
        List<(IReadOnlyList<int> seq, AtomId hash, string typeTag, string? description)> sequences,
        string outputDir)
    {
        var path = Path.Combine(outputDir, "emoji_sequences.c");
        using var w = CHeaderWriter.OpenWriter(path);
        w.Write(CHeaderWriter.LicenseBanner);
        w.WriteLine("#include \"laplace_pg/emoji_sequences.h\"");
        w.WriteLine();

        // Concatenate all codepoint sequences into a flat array; per-entry
        // offset indexes into it.
        var flat = new List<int>();
        var offsets = new List<int>(sequences.Count);
        foreach (var (seq, _, _, _) in sequences)
        {
            offsets.Add(flat.Count);
            foreach (var cp in seq) { flat.Add(cp); }
        }

        w.WriteLine("const int32_t LAPLACE_EMOJI_CODEPOINTS[] = {");
        const int perLine = 8;
        for (int i = 0; i < flat.Count; ++i)
        {
            if (i % perLine == 0) { w.Write("    "); }
            w.Write(flat[i].ToString(CultureInfo.InvariantCulture));
            w.Write(',');
            if ((i + 1) % perLine == 0) { w.WriteLine(); }
        }
        if (flat.Count % perLine != 0) { w.WriteLine(); }
        w.WriteLine("};");
        w.WriteLine();

        w.WriteLine("const laplace_emoji_sequence_entry_t");
        w.WriteLine("    LAPLACE_EMOJI_SEQUENCES[LAPLACE_EMOJI_SEQUENCES_COUNT] = {");
        for (int i = 0; i < sequences.Count; ++i)
        {
            var (seq, hash, typeTag, _) = sequences[i];
            w.Write("    {");
            w.Write(CHeaderWriter.FormatHashInit(hash.AsSpan()));
            w.Write(',');
            w.Write(offsets[i].ToString(CultureInfo.InvariantCulture));
            w.Write(',');
            w.Write(seq.Count.ToString(CultureInfo.InvariantCulture));
            w.Write(',');
            w.Write(EncodeTypeTag(typeTag).ToString(CultureInfo.InvariantCulture));
            w.WriteLine("},");
        }
        w.WriteLine("};");
    }

    private static byte EncodeTypeTag(string typeTag) => typeTag switch
    {
        "Basic_Emoji"                  => 0,
        "Emoji_Keycap_Sequence"        => 1,
        "RGI_Emoji_Modifier_Sequence"  => 2,
        "RGI_Emoji_Flag_Sequence"      => 3,
        "RGI_Emoji_Tag_Sequence"       => 4,
        "RGI_Emoji_ZWJ_Sequence"       => 5,
        _                              => 255,
    };
}
