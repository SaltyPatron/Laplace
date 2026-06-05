using System.Runtime.CompilerServices;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Wiktionary;

/// <summary>
/// Emits Wiktionary (wiktextract JSONL) as content + attestations.
///
/// Input is the pre-extracted JSONL (raw-wiktextract-data.jsonl, all languages, or the
/// kaikki English snapshot) — NOT the raw MediaWiki XML. Per record: the headword, every
/// sense gloss + example, IPA pronunciations, translations, sense relations
/// (syn/ant/hyper/hypo/mero/holo/derived/related), inflected forms, and etymology prose are
/// all decomposed as content (ContentEmitter) so they converge with every other source.
/// Attestations: HAS_LANGUAGE, HAS_POS, DEFINES (gloss), HAS_EXAMPLE, TRANSCRIBES_AS (IPA),
/// IS_TRANSLATION_OF, IS_SYNONYM_OF / IS_ANTONYM_OF / HAS_HYPERNYM / HAS_HYPONYM / HAS_PART /
/// IS_PART_OF / DERIVATIONALLY_RELATED / RELATED_TO, HAS_VARIANT_OF, HAS_ETYMOLOGY.
///
/// Single-pass: each record emits its referenced content inline, so every attestation's FK is
/// satisfied within the same batch (writer orders entities before attestations).
/// </summary>
public sealed class WiktionaryDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/WiktionaryDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCuratedWithUserInput/v1");

    private static readonly Hash128 LanguageTypeId = Hash128.OfCanonical("substrate/type/Language/v1");

    // Sense-relation property → kind NAME only. Rank / symmetry / direction-flip
    // resolve through KindRegistry (the single source of truth) at attest time.
    private static readonly Dictionary<string, string> RelMap = new(StringComparer.Ordinal)
    {
        ["synonyms"]  = "IS_SYNONYM_OF",
        ["antonyms"]  = "IS_ANTONYM_OF",
        ["hypernyms"] = "HAS_HYPERNYM",
        ["hyponyms"]  = "HAS_HYPONYM",
        ["meronyms"]  = "HAS_PART",
        ["holonyms"]  = "IS_PART_OF",
        ["derived"]   = "DERIVATIONALLY_RELATED",
        ["related"]   = "RELATED_TO",
    };

    public Hash128 SourceId     => Source;
    public string  SourceName   => "WiktionaryDecomposer";
    public int     LayerOrder   => 2;   // needs only unicode(0)+iso(1) — independent of wordnet/omw
    public Hash128 TrustClassId => TrustClass;

    /* pos string → THE canonical POS value (PosReference); unmapped strings →
     * namespaced probationary value + logged miss — never silent, never guessed. */
    private static Hash128 PosId(string p) => PosReference.Resolve(p, PosReference.PosTagset.Wiktionary);

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        // Rank/trust live in the REGISTRY at attest time — AddKind(name) only.
        boot.AddKind("HAS_POS");
        boot.AddKind("HAS_DEFINITION");
        boot.AddKind("HAS_EXAMPLE");
        boot.AddKind("HAS_ETYMOLOGY");
        boot.AddKind("HAS_HYPERNYM");
        boot.AddKind("HAS_HYPONYM");
        boot.AddKind("IS_PART_OF");
        boot.AddKind("IS_SYNONYM_OF");
        boot.AddKind("IS_ANTONYM_OF");
        boot.AddKind("DERIVATIONALLY_RELATED");
        boot.AddKind("RELATED_TO");
        boot.AddKind("IS_COORDINATE_TERM_WITH");
        boot.AddKind("HAS_USAGE_REGISTER");
        boot.AddKind("HAS_DOMAIN_TOPIC");
        boot.AddKind("ETYMOLOGICALLY_DERIVED_FROM");
        await context.Writer.ApplyAsync(boot.Build(), ct);

        // THE canonical POS inventory (PosReference) — canonical value FKs;
        // probationary values self-contain per record at emit time.
        var posSeed = new SubstrateChangeBuilder(
            Source, "bootstrap/pos-canonical", null,
            entityCapacity: PosReference.Canonical.Length + 1,
            physicalityCapacity: 0, attestationCapacity: 0);
        PosReference.SeedCanonical(posSeed, Source);
        await context.Writer.ApplyAsync(posSeed.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string? file = ResolveInput(context.EcosystemPath);
        if (file is null) yield break;
        int batch = options.BatchSize > 1 ? options.BatchSize : 1024;

        var b = NewBuilder("wiktionary/batch-0", batch);
        int n = 0, bn = 0;

        await foreach (var line in File.ReadLinesAsync(file, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0 || line[0] != '{') continue;

            bool emitted;
            try { emitted = EmitRecord(b, line); }
            catch (JsonException) { continue; } // tolerate malformed lines

            if (emitted && ++n >= batch)
            {
                if (!options.DryRun) yield return b.Build();
                b = NewBuilder($"wiktionary/batch-{++bn}", batch); n = 0; await Task.Yield();
            }
        }
        if (n > 0 && !options.DryRun) yield return b.Build();
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The usage-register vocabulary worth witnessing from sense tags
    /// (wiktextract mixes registers with grammar tags — only registers land in
    /// HAS_USAGE_REGISTER; grammar lives in the FEAT_*/UPOS layers).</summary>
    private static readonly HashSet<string> RegisterTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "archaic", "obsolete", "dated", "slang", "colloquial", "informal", "formal",
        "vulgar", "offensive", "derogatory", "humorous", "euphemistic", "dialectal",
        "regional", "literary", "poetic", "technical", "rare", "nonstandard",
        "historical", "figurative",
    };

    private static bool EmitRecord(SubstrateChangeBuilder b, string line)
    {
        using var doc = JsonDocument.Parse(line);
        var rec = doc.RootElement;

        string? word = Str(rec, "word");
        if (string.IsNullOrEmpty(word)) return false;
        var wordId = ContentEmitter.Emit(b, word!, Source);
        if (wordId is null) return false;
        Hash128 w = wordId.Value;

        // Language
        string? langCode = Str(rec, "lang_code") ?? Str(rec, "lang");
        if (!string.IsNullOrEmpty(langCode))
        {
            Hash128 langId = LanguageReference.Resolve(langCode!);
            b.AddEntity(new EntityRow(langId, (byte)MetaTier.Meta, LanguageTypeId, Source));
            b.AddAttestation(KindRegistry.Attest(
                w, "HAS_LANGUAGE", langId, Source, SourceTrust.AcademicCuratedUserInput));
        }

        // POS
        string? pos = Str(rec, "pos");
        Hash128? posCtx = null;
        if (!string.IsNullOrEmpty(pos))
        {
            Hash128 posId = PosId(pos!);
            posCtx = posId;
            b.AddEntity(new EntityRow(posId, (byte)MetaTier.Meta, PosReference.PosTypeId, Source));
            b.AddAttestation(KindRegistry.Attest(
                w, "HAS_POS", posId, Source, SourceTrust.AcademicCuratedUserInput));
        }

        // Senses → glosses (HAS_DEFINITION, context=pos) + examples + the
        // PER-SENSE relations the 2026-06-05 audit found dropped entirely —
        // the sense-specificity is the point of a dictionary. The sense's POS
        // rides as context on the relation (the sense disambiguator available
        // at this tier; wordform-level identity is the subject as ever).
        if (rec.TryGetProperty("senses", out var senses) && senses.ValueKind == JsonValueKind.Array)
        {
            foreach (var sense in senses.EnumerateArray())
            {
                if (sense.TryGetProperty("glosses", out var gl) && gl.ValueKind == JsonValueKind.Array)
                    foreach (var g in gl.EnumerateArray())
                        AttestText(b, w, "HAS_DEFINITION", g.GetString(), posCtx);

                if (sense.TryGetProperty("examples", out var ex) && ex.ValueKind == JsonValueKind.Array)
                    foreach (var e in ex.EnumerateArray())
                    {
                        string? txt = e.ValueKind == JsonValueKind.String ? e.GetString() : Str(e, "text");
                        AttestText(b, w, "HAS_EXAMPLE", txt, null);
                    }

                // Per-sense lexical relations (synonyms/antonyms/hypernyms/…
                // + coordinate_terms — co-hyponyms, their own symmetric arena).
                foreach (var (prop, kindName) in RelMap)
                    if (sense.TryGetProperty(prop, out var sarr) && sarr.ValueKind == JsonValueKind.Array)
                        foreach (var el in sarr.EnumerateArray())
                            AttestText(b, w, kindName, Str(el, "word"), posCtx);
                if (sense.TryGetProperty("coordinate_terms", out var coord) && coord.ValueKind == JsonValueKind.Array)
                    foreach (var el in coord.EnumerateArray())
                        AttestText(b, w, "IS_COORDINATE_TERM_WITH", Str(el, "word"), posCtx);

                // Sense categories → the SHARED domain arena (converges with
                // WordNet lexname domains); register tags (slang/archaic/…) →
                // HAS_USAGE_REGISTER with the register wordform as value.
                if (sense.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
                    foreach (var cEl in cats.EnumerateArray())
                    {
                        string? cat = cEl.ValueKind == JsonValueKind.String ? cEl.GetString() : Str(cEl, "name");
                        AttestText(b, w, "HAS_DOMAIN_TOPIC", cat, posCtx);
                    }
                if (sense.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                    foreach (var tEl in tags.EnumerateArray())
                        if (tEl.ValueKind == JsonValueKind.String && RegisterTags.Contains(tEl.GetString()!))
                            AttestText(b, w, "HAS_USAGE_REGISTER", tEl.GetString(), posCtx);
            }
        }

        // IPA pronunciations — the dialect tag (US/UK/RP/…) rides as context
        // on the transcription (which variety says it this way), as content.
        if (rec.TryGetProperty("sounds", out var sounds) && sounds.ValueKind == JsonValueKind.Array)
            foreach (var snd in sounds.EnumerateArray())
            {
                string? ipa = Str(snd, "ipa");
                if (string.IsNullOrWhiteSpace(ipa)) continue;
                Hash128? dialectCtx = null;
                if (snd.TryGetProperty("tags", out var stags) && stags.ValueKind == JsonValueKind.Array)
                    foreach (var tg in stags.EnumerateArray())
                        if (tg.ValueKind == JsonValueKind.String)
                        {
                            dialectCtx = ContentEmitter.Emit(b, tg.GetString()!, Source);
                            break;   // first tag = the variety label
                        }
                AttestText(b, w, "TRANSCRIBES_AS", ipa, dialectCtx);
            }

        // Translations
        if (rec.TryGetProperty("translations", out var tr) && tr.ValueKind == JsonValueKind.Array)
            foreach (var t in tr.EnumerateArray())
                AttestText(b, w, "IS_TRANSLATION_OF", Str(t, "word"), null);

        // Root-level relations (synonyms / antonyms / hypernyms / … + coordinate terms)
        foreach (var (prop, kindName) in RelMap)
            if (rec.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                    AttestText(b, w, kindName, Str(el, "word"), null);
        if (rec.TryGetProperty("coordinate_terms", out var rootCoord) && rootCoord.ValueKind == JsonValueKind.Array)
            foreach (var el in rootCoord.EnumerateArray())
                AttestText(b, w, "IS_COORDINATE_TERM_WITH", Str(el, "word"), null);

        // Inflected forms → variants
        if (rec.TryGetProperty("forms", out var forms) && forms.ValueKind == JsonValueKind.Array)
            foreach (var f in forms.EnumerateArray())
                AttestText(b, w, "HAS_VARIANT_OF", Str(f, "form"), null);

        // Etymology prose
        AttestText(b, w, "HAS_ETYMOLOGY", Str(rec, "etymology_text"), null);

        // Structured etymology templates (minimal lawful slice): borrowed /
        // derived / inherited carry a source-language term → the cognate
        // wordform, in the existing etymological arenas.
        if (rec.TryGetProperty("etymology_templates", out var etys) && etys.ValueKind == JsonValueKind.Array)
            foreach (var et in etys.EnumerateArray())
            {
                string? name = Str(et, "name");
                if (name is not ("bor" or "borrowed" or "der" or "derived" or "inh" or "inherited")) continue;
                if (!et.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object) continue;
                // wiktextract template args: "2" = source language, "3" = the term.
                string? term = args.TryGetProperty("3", out var a3) && a3.ValueKind == JsonValueKind.String
                    ? a3.GetString() : null;
                if (string.IsNullOrWhiteSpace(term) || term == "-") continue;
                AttestText(b, w, "ETYMOLOGICALLY_DERIVED_FROM", term, null);
            }

        return true;
    }

    /// <summary>Emit <paramref name="text"/> as content and attest
    /// (subject)→(kind)→(content), rank/symmetry/direction resolved through
    /// <see cref="KindRegistry"/> — the single source of truth.</summary>
    private static void AttestText(
        SubstrateChangeBuilder b, Hash128 subject, string kindName, string? text,
        Hash128? context)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var id = ContentEmitter.Emit(b, text!, Source);
        if (id is null) return;
        b.AddAttestation(KindRegistry.Attest(
            subject, kindName, id.Value, Source, SourceTrust.AcademicCuratedUserInput,
            contextId: context));
    }

    private static string? Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string? ResolveInput(string dir)
    {
        foreach (var name in new[] { "raw-wiktextract-data.jsonl", "kaikki.org-dictionary-English.jsonl" })
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        // Fall back to any *.jsonl in the directory.
        if (Directory.Exists(dir))
            foreach (var p in Directory.EnumerateFiles(dir, "*.jsonl")) return p;
        return null;
    }

    private static SubstrateChangeBuilder NewBuilder(string unit, int batch) =>
        new(Source, unit, null,
            entityCapacity:      batch * 30,
            physicalityCapacity: batch * 30,
            attestationCapacity: batch * 20);
}
