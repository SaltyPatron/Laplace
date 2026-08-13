using System.Collections.Concurrent;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.UD;

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

        // Language is a property of the TEXT, asserted ONCE at the sentence root — a
        // treebank says the SENTENCE is language L, not that each wordform intrinsically
        // is (a wordform's language is a use-property; `chat` is French AND English).
        // Per-token Lang= from MISC (genuine code-switching the source marks) is still
        // emitted in the token loop below. See docs/specs/16 §1a — this replaces 5.3M
        // per-word HAS_LANGUAGE rows with one per sentence.
        // Hoisted: the sentence content root anchors the language attestation AND
        // is the contextId on every dependency arc below (#1057). With a null
        // context, arcs joined type-level wordforms globally — the nsubj arc from
        // "The cat sat" was the same consensus row as every cat→sat pair in every
        // corpus, no arc was bound to any sentence, and French/Spanish homographs
        // merged. Context = sentence root keeps consensus folding by
        // (subject, type, object) exactly as before while the attestation layer
        // retains per-sentence testimony (attestation identity includes context),
        // and language scoping rides the sentence's own HAS_LANGUAGE.
        Hash128? sentenceRoot = s.TextUtf8 is { Length: > 0 } ? ctx.RootFor(s.TextUtf8) : null;
        if (sentenceRoot is { } sentenceRootId)
            b.AddAttestation(NativeAttestation.Categorical(
                sentenceRootId, "HAS_LANGUAGE", langId, sourceId, null, SourceTrust.AcademicCurated));

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

            Hash128? uposId = null;
            if (!string.IsNullOrEmpty(tok.Upos) && tok.Upos != "_")
                uposId = PosReference.Attest(b, form, tok.Upos!, PosReference.PosTagset.Upos,
                    sourceId, null, SourceTrust.AcademicCurated, canonicalNames);

            if (!string.IsNullOrEmpty(tok.Xpos) && tok.Xpos != "_")
            {
                Hash128 xposId = HighwayNodeEmitter.Emit(b, tok.Xpos, PosReference.PosTypeId,
                    sourceId, TC.AcademicCurated, seenEntBatch);
                b.AddAttestation(NativeAttestation.Categorical(
                    form, "HAS_XPOS", xposId, sourceId, langId, TC.AcademicCurated));
                // Link the language-specific XPOS to its universal POS on the SAME row, so the
                // ~36k per-treebank XPOS tags collapse onto the 17 UPOS hubs (XPOS IS_A UPOS:
                // "NNP" is a kind of PROPN). Morphology XPOS also encodes stays in the FEATS
                // channel (FEAT_* below) — UD's canonical morphology surface. See docs/specs/16 §5.
                if (uposId is { } up)
                    b.AddAttestation(NativeAttestation.Categorical(
                        xposId, "IS_A", up, sourceId, langId, TC.AcademicCurated));
            }

            foreach (var feat in tok.Feats)
            {
                if (!RelationTypeRegistry.ParseFeature(feat, out var fName, out var fVal)) continue;
                VocabularyNames.TrackUdFeatureValue(canonicalNames, fName, fVal);
                Hash128 valId = HighwayNodeEmitter.Emit(b, $"{fName}={fVal}", FeatureTypeId,
                    sourceId, SourceTrust.AcademicCurated, seenEntBatch);
                var featRel = RelationTypeRegistry.ResolveFeature(fName);
                RelationTypeRegistry.SeedDynamic(b, featRel, sourceId,
                    seenEntBatch, seenAttBatch, canonicalNames);
                b.AddAttestation(NativeAttestation.CategoricalResolved(
                    form, featRel.Id, valId, sourceId, null, featRel.Rank * SourceTrust.AcademicCurated));
            }

            // NOTE: sentence language is attested once at the sentence root (top of
            // EmitWitness), NOT per wordform. Per-token language survives only where the
            // source explicitly marks code-switching via MISC Lang= (handled below).

            if (!tok.FormLemmaSame && ctx.RootFor(tok.LemmaUtf8) is { } lemmaId)
                b.AddAttestation(NativeAttestation.Categorical(
                    lemmaId, "IS_LEMMA_OF", form, sourceId, SourceTrust.AcademicCurated));

            if (tok.Head > 0 && tok.Head <= s.MaxId && ctx.FormId[tok.Head] is { } headId
                && !string.IsNullOrEmpty(tok.Deprel) && tok.Deprel != "_")
            {
                RelationTypeRegistry.SeedDeprel(b, tok.Deprel, sourceId, seenEntBatch, seenAttBatch, canonicalNames);
                var dep = RelationTypeRegistry.ResolveDeprel(tok.Deprel);
                b.AddAttestation(NativeAttestation.CategoricalResolved(
                    form, dep.Id, headId, sourceId, sentenceRoot,
                    dep.Rank * SourceTrust.AcademicCurated));
            }
            else if (tok.Head == 0 && sentenceRoot is { } sentRoot
                     && !string.IsNullOrEmpty(tok.Deprel) && tok.Deprel != "_")
            {
                // HEAD=0 marks the sentence head (deprel 'root'): bind the heading
                // word to the SENTENCE itself. Previously dropped entirely —
                // ~2.31M sentence-head markers lost across the treebanks, and
                // "which word heads this sentence" was unanswerable (#1057).
                RelationTypeRegistry.SeedDeprel(b, tok.Deprel, sourceId, seenEntBatch, seenAttBatch, canonicalNames);
                var dep = RelationTypeRegistry.ResolveDeprel(tok.Deprel);
                b.AddAttestation(NativeAttestation.CategoricalResolved(
                    form, dep.Id, sentRoot, sourceId, sentRoot,
                    dep.Rank * SourceTrust.AcademicCurated));
            }

            if (tok.Deps.Length > 0 && tok.Deps != "_")
            {
                foreach (var edge in tok.Deps.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    int colon = edge.IndexOf(':');
                    if (colon <= 0) continue;
                    string headRef = edge[..colon];
                    string erel = edge[(colon + 1)..].Trim();
                    if (erel.Length == 0) continue;
                    int esub = erel.IndexOf(':');
                    string ebase = esub > 0 ? erel[..esub] : erel;
                    if (headRef == "0")
                    {
                        // Enhanced-graph root: same law as the basic HEAD=0 arc —
                        // bind to the sentence, don't drop (#1057).
                        if (sentenceRoot is { } esr)
                        {
                            RelationTypeRegistry.SeedEnhancedDeprel(b, ebase, sourceId, seenEntBatch, seenAttBatch, canonicalNames);
                            var eroot = RelationTypeRegistry.ResolveEnhancedDeprel(ebase);
                            b.AddAttestation(NativeAttestation.CategoricalResolved(
                                form, eroot.Id, esr, sourceId, esr,
                                eroot.Rank * SourceTrust.AcademicCurated));
                        }
                        continue;
                    }
                    if (!ctx.RefToForm.TryGetValue(headRef, out var eHead)) continue;
                    RelationTypeRegistry.SeedEnhancedDeprel(b, ebase, sourceId, seenEntBatch, seenAttBatch, canonicalNames);
                    var edep = RelationTypeRegistry.ResolveEnhancedDeprel(ebase);
                    b.AddAttestation(NativeAttestation.CategoricalResolved(
                        form, edep.Id, eHead, sourceId, sentenceRoot,
                        edep.Rank * SourceTrust.AcademicCurated));
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
                        // Never emit form HAS_DEFINITION form — when the gloss
                        // roots to the same id as the token, that is a self-loop,
                        // not a definition (#496). Numeric/misc tokens hit this.
                        if (ctx.RootFor(gBytes) is { } gid && gid != form)
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
                        AddUnique(System.Text.Encoding.UTF8.GetBytes(val), sink, seen);
                }
            }
        }
        foreach (var mwt in s.Mwts)
            AddUnique(mwt.FormUtf8, sink, seen);
    }

    private static void AddUnique(byte[] bytes, List<byte[]> sink, HashSet<Hash128> seen)
    {
        if (bytes.Length == 0) return;
        if (seen.Add(Hash128.Blake3(bytes)))
            sink.Add(bytes);
    }
}
