using System.Collections.Concurrent;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.UD;

/// <summary>Content roots + witness maps produced by <see cref="UdIngestHandler"/> drain.</summary>
public sealed class UdSentenceEmitContext
{
    private static readonly Hash128 FeatureTypeId = EntityTypeRegistry.UdFeature;
    private static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;

    internal readonly Dictionary<Hash128, Hash128> RootByCanonical = new();
    internal Hash128?[] FormId = [];
    internal Dictionary<string, Hash128> RefToForm = new(StringComparer.Ordinal);

    internal void RegisterRoot(ReadOnlySpan<byte> canonical, Hash128 rootId)
    {
        if (canonical.IsEmpty || rootId == default) return;
        RootByCanonical[Hash128.Blake3(canonical)] = rootId;
    }

    internal Hash128? RootFor(ReadOnlySpan<byte> canonical)
    {
        if (canonical.IsEmpty) return null;
        return RootByCanonical.TryGetValue(Hash128.Blake3(canonical), out var id) ? id : null;
    }

    public static void EmitWitness(
        SubstrateChangeBuilder b,
        UdSentence s,
        Hash128 langId,
        string langCode,
        HashSet<Hash128> seenEntBatch,
        ConcurrentIdSet seenAttBatch,
        ConcurrentDictionary<string, byte> canonicalNames,
        UdSentenceEmitContext ctx,
        Hash128 sourceId)
    {
        b.AddEntity(new EntityRow(langId, EntityTier.Word, LanguageTypeId, sourceId));
        VocabularyNames.TrackLanguage(canonicalNames, langCode);

        ctx.FormId = new Hash128?[s.MaxId + 1];
        ctx.RefToForm.Clear();
        foreach (var tok in s.Tokens)
        {
            if (ctx.RootFor(tok.FormUtf8) is { } fid)
            {
                if (tok.Id >= 0) ctx.FormId[tok.Id] = fid;
                ctx.RefToForm[tok.Ref] = fid;
            }
        }

        foreach (var tok in s.Tokens)
        {
            if (!ctx.RefToForm.TryGetValue(tok.Ref, out var form)) continue;

            if (!string.IsNullOrEmpty(tok.Upos) && tok.Upos != "_")
                PosReference.Attest(b, form, tok.Upos!, PosReference.PosTagset.Upos,
                    sourceId, null, SourceTrust.AcademicCurated, canonicalNames);

            if (!string.IsNullOrEmpty(tok.Xpos) && tok.Xpos != "_")
            {
                // Content-addressed: xpos entity id = blake3(utf8(tok.Xpos)); HAS_NAME_ALIAS handles legibility.
                Hash128 xposId = HighwayNodeEmitter.Emit(b, tok.Xpos, PosReference.PosTypeId,
                    sourceId, TC.AcademicCurated, seenEntBatch);
                b.AddAttestation(NativeAttestation.Categorical(
                    form, "HAS_XPOS", xposId, sourceId, langId, TC.AcademicCurated));
            }

            foreach (var feat in tok.Feats)
            {
                if (!RelationTypeRegistry.ParseFeature(feat, out var fName, out var fVal)) continue;
                VocabularyNames.TrackUdFeatureValue(canonicalNames, fName, fVal);
                // Content-addressed: feature value entity id = blake3(utf8("{name}={val}")).
                Hash128 valId = HighwayNodeEmitter.Emit(b, $"{fName}={fVal}", FeatureTypeId,
                    sourceId, SourceTrust.AcademicCurated, seenEntBatch);
                RelationTypeRegistry.SeedDynamic(b, RelationTypeRegistry.ResolveFeature(fName), sourceId,
                    seenEntBatch, seenAttBatch, canonicalNames);
                var featRel = RelationTypeRegistry.ResolveFeature(fName);
                b.AddAttestation(NativeAttestation.CategoricalResolved(
                    form, featRel.Id, valId, sourceId, null, featRel.Rank * SourceTrust.AcademicCurated));
            }

            b.AddAttestation(NativeAttestation.Categorical(
                form, "HAS_LANGUAGE", langId, sourceId, null, SourceTrust.AcademicCurated));

            if (!tok.FormLemmaSame && ctx.RootFor(tok.LemmaUtf8) is { } lemmaId)
                b.AddAttestation(NativeAttestation.Categorical(
                    lemmaId, "IS_LEMMA_OF", form, sourceId, SourceTrust.AcademicCurated));

            if (tok.Head > 0 && tok.Head <= s.MaxId && ctx.FormId[tok.Head] is { } headId
                && !string.IsNullOrEmpty(tok.Deprel) && tok.Deprel != "_")
            {
                RelationTypeRegistry.SeedDeprel(b, tok.Deprel, sourceId, seenEntBatch, seenAttBatch, canonicalNames);
                var dep = RelationTypeRegistry.ResolveDeprel(tok.Deprel);
                b.AddAttestation(NativeAttestation.CategoricalResolved(
                    form, dep.Id, headId, sourceId, null, dep.Rank * SourceTrust.AcademicCurated));
            }

            if (tok.Deps.Length > 0 && tok.Deps != "_")
            {
                foreach (var edge in tok.Deps.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    int colon = edge.IndexOf(':');
                    if (colon <= 0) continue;
                    string headRef = edge[..colon];
                    string erel = edge[(colon + 1)..].Trim();
                    if (erel.Length == 0 || headRef == "0") continue;
                    if (!ctx.RefToForm.TryGetValue(headRef, out var eHead)) continue;
                    int esub = erel.IndexOf(':');
                    string ebase = esub > 0 ? erel[..esub] : erel;
                    RelationTypeRegistry.SeedEnhancedDeprel(b, ebase, sourceId, seenEntBatch, seenAttBatch, canonicalNames);
                    var edep = RelationTypeRegistry.ResolveEnhancedDeprel(ebase);
                    b.AddAttestation(NativeAttestation.CategoricalResolved(
                        form, edep.Id, eHead, sourceId, null, edep.Rank * SourceTrust.AcademicCurated));
                }
            }

            if (tok.Misc.Length > 0 && tok.Misc != "_")
            {
                foreach (var kv in tok.Misc.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = kv[..eq];
                    string val = kv[(eq + 1)..].Trim();
                    if (val.Length == 0) continue;
                    if (key.Equals("Gloss", StringComparison.OrdinalIgnoreCase))
                    {
                        var gBytes = System.Text.Encoding.UTF8.GetBytes(val);
                        if (ctx.RootFor(gBytes) is { } gid)
                            b.AddAttestation(NativeAttestation.Categorical(
                                form, "HAS_DEFINITION", gid, sourceId, SourceTrust.AcademicCurated));
                    }
                    else if (key.Equals("Translit", StringComparison.OrdinalIgnoreCase))
                    {
                        var tBytes = System.Text.Encoding.UTF8.GetBytes(val);
                        if (ctx.RootFor(tBytes) is { } tid)
                            b.AddAttestation(NativeAttestation.Categorical(
                                form, "TRANSCRIBES_AS", tid, sourceId, SourceTrust.AcademicCurated));
                    }
                    else if (key.Equals("Lang", StringComparison.OrdinalIgnoreCase))
                    {
                        Hash128 miscLangId = LanguageReference.Resolve(val);
                        b.AddAttestation(NativeAttestation.Categorical(
                            form, "HAS_LANGUAGE", miscLangId, sourceId, SourceTrust.AcademicCurated));
                    }
                }
            }
        }

        foreach (var mwt in s.Mwts)
        {
            if (ctx.RootFor(mwt.FormUtf8) is not { } surfaceId) continue;
            for (int id = mwt.Start; id <= mwt.End && id <= s.MaxId; id++)
                if (ctx.FormId[id] is { } partId)
                    b.AddAttestation(NativeAttestation.Categorical(
                        surfaceId, "HAS_PART", partId, sourceId, SourceTrust.AcademicCurated));
        }
    }

    internal static void CollectCanonicals(UdSentence s, List<byte[]> sink)
    {
        if (s.TextUtf8 is { Length: > 0 })
            AddUnique(s.TextUtf8, sink);
        foreach (var tok in s.Tokens)
        {
            AddUnique(tok.FormUtf8, sink);
            if (!tok.FormLemmaSame)
                AddUnique(tok.LemmaUtf8, sink);
            if (tok.Misc.Length > 0 && tok.Misc != "_")
            {
                foreach (var kv in tok.Misc.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = kv[..eq];
                    string val = kv[(eq + 1)..].Trim();
                    if (val.Length == 0) continue;
                    if (key.Equals("Gloss", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("Translit", StringComparison.OrdinalIgnoreCase))
                        AddUnique(System.Text.Encoding.UTF8.GetBytes(val), sink);
                }
            }
        }
        foreach (var mwt in s.Mwts)
            AddUnique(mwt.FormUtf8, sink);
    }

    private static void AddUnique(byte[] bytes, List<byte[]> sink)
    {
        if (bytes.Length == 0) return;
        var key = Hash128.Blake3(bytes);
        foreach (var existing in sink)
        {
            if (Hash128.Blake3(existing) == key) return;
        }
        sink.Add(bytes);
    }
}
