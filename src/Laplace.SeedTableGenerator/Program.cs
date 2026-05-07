namespace Laplace.SeedTableGenerator;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using Laplace.Core;
using Laplace.Decomposers.Ucd;

/// <summary>
/// laplace-seed-gen — the foundational-seed C-table generator.
///
/// Reads canonical Unicode sources (ucd.all.flat.xml + UCA allkeys.txt +
/// emoji sequences + ISO 639-3 .tab files), computes per-codepoint substrate
/// identity (BLAKE3 entity hash + super-Fibonacci position + Hilbert index +
/// derived prime flags), and emits compiled C tables for the substrate's
/// in-process acceleration layer plus a TSV for the DB shadow rows.
///
/// Phase 3 / Track E completion. Closes the foundational seed acceleration
/// piece that decomposers (Track F) read from for microsecond-per-codepoint
/// lookups instead of round-tripping the database.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return 0;
        }

        return args[0] switch
        {
            "scan"     => RunScan(args),
            "generate" => RunGenerate(args),
            _          => Unknown(args[0]),
        };
    }

    private static int RunScan(string[] args)
    {
        var ucdXmlPath = ArgValue(args, "--ucd-xml")
                      ?? "D:/Models/UCD/Public/UCD/latest/ucdxml/ucd.all.flat.xml";
        var limit = int.Parse(
            ArgValue(args, "--limit") ?? "16",
            CultureInfo.InvariantCulture);

        Console.WriteLine($"Scanning {ucdXmlPath} (first {limit} codepoints)...");

        var hashing  = new IdentityHashing();
        var superFib = new SuperFibonacci();
        var hilbert  = new HilbertCurve();
        var builder  = new CodepointEntryBuilder(hashing, superFib, hilbert);

        // For the scan command we slurp just `limit` codepoint records and
        // pretend that's the full seed for super-Fibonacci placement so the
        // pipeline can be exercised end-to-end without parsing 219 MB.
        var records = new List<UcdCodepointRecord>(limit);
        foreach (var rec in UcdXmlParser.Parse(ucdXmlPath))
        {
            records.Add(rec);
            if (records.Count >= limit) { break; }
        }

        var ordering = CanonicalOrdering.Sort(
            records,
            ucaPrimaryFor:    static _ => 0,
            unihanRadicalFor: static _ => 0);

        var entries = builder.Build(ordering);
        foreach (var e in entries)
        {
            Console.WriteLine(
                $"  cp=U+{e.Codepoint:X4} hash={e.EntityHash} sc={e.Script,-4} gc={e.GeneralCategory,-3} " +
                $"hilbert=0x{e.HilbertIndex:X16} primes=0x{e.PrimeFlags:X16}");
        }
        return 0;
    }

    private static int RunGenerate(string[] args)
    {
        var ucdXmlPath = ArgValue(args, "--ucd-xml")
                      ?? "D:/Models/UCD/Public/UCD/latest/ucdxml/ucd.all.flat.xml";
        var iso639Dir  = ArgValue(args, "--iso639")
                      ?? "D:/Models/ISO639";
        var outputDir = ArgValue(args, "--output")
                      ?? Path.Combine(
                          AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                          "ext", "laplace_pg", "generated");
        outputDir = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(outputDir);

        Console.WriteLine($"Generating to {outputDir}");
        Console.WriteLine($"  UCD XML: {ucdXmlPath}");
        Console.WriteLine($"  ISO 639: {iso639Dir}");

        var hashing  = new IdentityHashing();
        var superFib = new SuperFibonacci();
        var hilbert  = new HilbertCurve();
        var builder  = new CodepointEntryBuilder(hashing, superFib, hilbert);

        Console.WriteLine("Parsing UCD XML (streaming)...");
        var records = new List<UcdCodepointRecord>(capacity: 200_000);
        var t0 = DateTime.UtcNow;
        foreach (var rec in UcdXmlParser.Parse(ucdXmlPath))
        {
            records.Add(rec);
        }
        Console.WriteLine($"  parsed {records.Count} char records in {(DateTime.UtcNow - t0).TotalSeconds:F1}s");

        Console.WriteLine("Building canonical ordering...");
        var ordering = CanonicalOrdering.Sort(
            records,
            ucaPrimaryFor:    static _ => 0,
            unihanRadicalFor: static _ => 0);
        Console.WriteLine($"  ordered {ordering.Count} codepoints");

        Console.WriteLine("Computing entries (BLAKE3 + super-Fibonacci + Hilbert + flags)...");
        t0 = DateTime.UtcNow;
        var entries = builder.Build(ordering);
        Console.WriteLine($"  built {entries.Count} entries in {(DateTime.UtcNow - t0).TotalSeconds:F1}s");

        Console.WriteLine("Building codepoint→hash lookup...");
        var codepointHashes = new Dictionary<int, Laplace.Core.Abstractions.AtomId>(entries.Count);
        foreach (var e in entries)
        {
            codepointHashes[e.Codepoint] = e.EntityHash;
        }

        Console.WriteLine("Emitting codepoint_table.{h,c}...");
        CodepointTableEmitter.Emit(entries, outputDir);

        Console.WriteLine("Emitting seed_db_rows.tsv...");
        SeedDbRowsEmitter.Emit(entries, outputDir);

        Console.WriteLine("Emitting codepoint_names.{h,c}...");
        NamePoolEmitter.Emit(entries, outputDir);

        Console.WriteLine("Emitting codepoint_decompositions.{h,c}...");
        DecompositionTableEmitter.Emit(records, outputDir);

        Console.WriteLine("Emitting registries (script, block, age, gc, bidi)...");
        RegistryEmitter.Emit("script", entries.Select(e => e.Script), codepointHashes, hashing, outputDir);
        RegistryEmitter.Emit("block",  entries.Select(e => e.Block),  codepointHashes, hashing, outputDir);
        RegistryEmitter.Emit("age",    entries.Select(e => e.Age),    codepointHashes, hashing, outputDir);
        RegistryEmitter.Emit("gc",     entries.Select(e => e.GeneralCategory), codepointHashes, hashing, outputDir);
        RegistryEmitter.Emit("bidi",   entries.Select(e => e.BidiClass), codepointHashes, hashing, outputDir);

        var ucaPath = ArgValue(args, "--uca")
                   ?? "D:/Models/UCD/Public/UCD/latest/uca/allkeys.txt";
        if (File.Exists(ucaPath))
        {
            Console.WriteLine($"Parsing UCA allkeys.txt ({ucaPath})...");
            var ucaEntries = new List<UcaEntry>();
            foreach (var ue in UcaAllKeysParser.Parse(ucaPath)) { ucaEntries.Add(ue); }
            Console.WriteLine($"  parsed {ucaEntries.Count} UCA entries");
            Console.WriteLine("Emitting uca_weights.{h,c}...");
            UcaWeightsEmitter.Emit(ucaEntries, outputDir);
        }
        else
        {
            Console.WriteLine($"  UCA allkeys.txt not found at {ucaPath}; skipping");
        }

        var emojiDir = ArgValue(args, "--emoji")
                    ?? "D:/Models/UCD/Public/UCD/latest/emoji";
        if (Directory.Exists(emojiDir))
        {
            Console.WriteLine($"Parsing emoji sequences ({emojiDir})...");
            var emojiEntries = new List<EmojiSequenceEntry>();
            foreach (var ee in EmojiSequencesParser.ParseAll(emojiDir)) { emojiEntries.Add(ee); }
            Console.WriteLine($"  parsed {emojiEntries.Count} emoji sequence rows");
            Console.WriteLine("Emitting emoji_sequences.{h,c}...");
            EmojiSequencesEmitter.Emit(emojiEntries, codepointHashes, hashing, outputDir);
        }
        else
        {
            Console.WriteLine($"  emoji directory not found at {emojiDir}; skipping");
        }

        Console.WriteLine("Parsing ISO 639-3...");
        var iso639Languages = new List<Decomposers.Iso639.Iso639LanguageRecord>();
        var iso639TabPath = Path.Combine(iso639Dir, "iso-639-3.tab");
        if (File.Exists(iso639TabPath))
        {
            foreach (var lang in Decomposers.Iso639.Iso639TabParser.ParseLanguages(iso639TabPath))
            {
                iso639Languages.Add(lang);
            }
            Console.WriteLine($"  parsed {iso639Languages.Count} languages");
            Console.WriteLine("Emitting iso639_languages.{h,c}...");
            Iso639LanguagesEmitter.Emit(iso639Languages, codepointHashes, hashing, outputDir);
        }
        else
        {
            Console.WriteLine($"  ISO 639-3 file not found at {iso639TabPath}; skipping");
        }

        Console.WriteLine("Done.");
        return 0;
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"unknown subcommand: {verb}");
        PrintHelp();
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("laplace-seed-gen — foundational-seed C-table generator");
        Console.WriteLine();
        Console.WriteLine("usage:");
        Console.WriteLine("  laplace-seed-gen scan [--ucd-xml PATH] [--limit N]");
        Console.WriteLine("    Smoke-test the parser + canonical ordering + native");
        Console.WriteLine("    pipeline by hashing + placing the first N codepoints.");
        Console.WriteLine();
        Console.WriteLine("  laplace-seed-gen generate [--ucd-xml PATH] [--output DIR]");
        Console.WriteLine("    Full generation: emits codepoint_table.{h,c} + seed_db_rows.tsv.");
        Console.WriteLine("    (Auxiliary tables — names, decompositions, registries, UCA,");
        Console.WriteLine("    emoji, ISO 639 — land in follow-up commits.)");
    }

    private static string? ArgValue(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; ++i)
        {
            if (args[i] == flag) { return args[i + 1]; }
        }
        return null;
    }
}
