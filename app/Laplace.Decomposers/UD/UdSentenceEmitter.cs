using System.Collections.Concurrent;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.UD;

public sealed class UdSentenceEmitContext
{
    private static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;

    private readonly Dictionary<Hash128, RootPlacement> _rootByCanonical = new();
    internal void RegisterRoot(
        ReadOnlySpan<byte> canonical, Hash128 rootId, ReadOnlySpan<double> coordXyzm = default)
    {
        if (canonical.IsEmpty || rootId == default) return;
        double[]? coord = coordXyzm.Length >= 4 ? coordXyzm[..4].ToArray() : null;
        _rootByCanonical[Hash128.Blake3(canonical)] = new RootPlacement(rootId, coord);
    }

    internal Hash128? RootFor(ReadOnlySpan<byte> canonical)
    {
        if (canonical.IsEmpty) return null;
        return _rootByCanonical.TryGetValue(Hash128.Blake3(canonical), out var root)
            ? root.Id
            : null;
    }

    internal bool TryRootCoord(ReadOnlySpan<byte> canonical, Span<double> coordXyzm)
    {
        if (coordXyzm.Length < 4)
            throw new ArgumentException("coordXyzm must hold four doubles", nameof(coordXyzm));
        if (canonical.IsEmpty
            || !_rootByCanonical.TryGetValue(Hash128.Blake3(canonical), out var root)
            || root.CoordXyzm is not { Length: >= 4 } coord)
            return false;
        coord.AsSpan(0, 4).CopyTo(coordXyzm);
        return true;
    }

    private readonly record struct RootPlacement(Hash128 Id, double[]? CoordXyzm);

    public static void EmitWitness(
        SubstrateChangeBuilder b,
        UdSentence s,
        Hash128 langId,
        string langCode,
        string fileLabel,
        HashSet<Hash128> seenEntBatch,
        ConcurrentIdSet seenSourceDeclarations,
        ConcurrentDictionary<string, byte> canonicalNames,
        UdSentenceEmitContext ctx,
        Hash128 sourceId)
    {
        b.AddEntity(new EntityRow(langId, EntityTier.Word, LanguageTypeId, sourceId));
        VocabularyNames.TrackLanguage(canonicalNames, langCode);

        Hash128? sentenceRoot = s.TextUtf8 is { Length: > 0 } ? ctx.RootFor(s.TextUtf8) : null;
        string xposScope = XposIdentityScope(langCode, fileLabel);
        Hash128 parseId = UdParseStructure.Emit(
            b, s, langId, xposScope, fileLabel, seenEntBatch,
            seenSourceDeclarations, canonicalNames, ctx, sourceId);
        b.AddAttestation(NativeAttestation.CategoricalResolved(
            sentenceRoot ?? parseId,
            UDSource.HasLanguageTypeId,
            langId,
            sourceId,
            null,
            SourceTrust.AcademicCurated));
    }

    internal static string XposIdentityScope(string langCode, string fileLabel)
    {
        // UD XPOS is treebank/tagset specific, not merely language specific.
        // FileLabel's durable form is ud/<treebank>/<split>; binding only the
        // treebank keeps train/dev/test in one tagset while preventing two
        // English treebanks from collapsing an equal surface tag such as NN.
        const string Prefix = "ud/";
        if (!fileLabel.StartsWith(Prefix, StringComparison.Ordinal)) return langCode;
        int start = Prefix.Length;
        int slash = fileLabel.IndexOf('/', start);
        if (slash <= start) return langCode;
        string treebank = fileLabel[start..slash];
        if (!treebank.StartsWith("UD_", StringComparison.Ordinal)) return langCode;
        return $"{langCode}/{treebank}";
    }

    internal static void CollectCanonicals(UdSentence s, List<byte[]> sink)
    {
        // Seen-set keyed by content hash: each candidate is hashed exactly
        // once (the old scan re-hashed every collected entry per candidate —
        // O(T²) BLAKE3 invocations per sentence).
        var seen = new HashSet<Hash128>();
        foreach (var existing in sink)
            seen.Add(Hash128.Blake3(existing));

        if (s.TextUtf8 is { Length: > 0 })
            AddUnique(s.TextUtf8, sink, seen);
        foreach (var tok in s.Tokens)
        {
            AddUnique(tok.FormUtf8, sink, seen);
            if (!tok.FormLemmaSame)
                AddUnique(tok.LemmaUtf8, sink, seen);
            CollectMiscCanonicals(tok.Misc, sink, seen);
        }
        foreach (var mwt in s.Mwts)
        {
            AddUnique(mwt.FormUtf8, sink, seen);
            CollectMiscCanonicals(mwt.Misc, sink, seen);
        }
    }

    private static void AddUnique(byte[] bytes, List<byte[]> sink, HashSet<Hash128> seen)
    {
        if (bytes.Length == 0) return;
        if (seen.Add(Hash128.Blake3(bytes)))
            sink.Add(bytes);
    }

    private static void CollectMiscCanonicals(
        string misc, List<byte[]> sink, HashSet<Hash128> seen)
    {
        if (misc.Length == 0 || misc == "_") return;
        foreach (string item in misc.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            // EVERY value, not just Gloss and Translit. UdParseStructure.ResolveMisc
            // resolves MISC values through content.RootFor, which is a lookup into the
            // set collected here -- so this whitelist was what forced every other key
            // down the ud/misc-value/{hex}/v1 slug path that filled 97.9% of
            // laplace.canonical_names (3,313,800 rows, 892 MB, 0 consensus edges).
            // A value is content; collecting it is what makes it addressable.
            int equals = item.IndexOf('=');
            if (equals <= 0) continue;
            string value = item[(equals + 1)..].Trim();
            if (value.Length > 0)
                AddUnique(System.Text.Encoding.UTF8.GetBytes(value), sink, seen);
        }
    }
}