using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Wiktionary;

internal static class WiktionaryWitness
{
    private static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;

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

    private static readonly HashSet<string> RegisterTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "archaic", "obsolete", "dated", "slang", "colloquial", "informal", "formal",
        "vulgar", "offensive", "derogatory", "humorous", "euphemistic", "dialectal",
        "regional", "literary", "poetic", "technical", "rare", "nonstandard",
        "historical", "figurative",
    };

    public static bool TryWalkRecord(
        SubstrateChangeBuilder b, ReadOnlyMemory<byte> line, DecomposerOptions options)
    {
        try
        {
            var reader = new Utf8JsonReader(line.Span, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;
            var rec = JsonElement.ParseValue(ref reader);
            return WalkRecord(b, rec, options);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool WalkRecord(SubstrateChangeBuilder b, JsonElement rec, DecomposerOptions options)
    {
        string? word = Str(rec, "word");
        if (string.IsNullOrEmpty(word)) return false;

        string? langCode = Str(rec, "lang_code") ?? Str(rec, "lang");
        if (options.Languages?.MatchesRaw(langCode) == false) return false;

        var wordId = ContentEmitter.Emit(b, word!, WiktionaryDecomposer.Source);
        if (wordId is null) return false;
        Hash128 w = wordId.Value;

        if (!string.IsNullOrEmpty(langCode))
        {
            Hash128 langId = LanguageReference.Resolve(langCode!);
            b.AddEntity(new EntityRow(langId, EntityTier.Vocabulary, LanguageTypeId, WiktionaryDecomposer.Source));
            b.AddAttestation(NativeAttestation.Categorical(
                w, "HAS_LANGUAGE", langId, WiktionaryDecomposer.Source, SourceTrust.AcademicCuratedUserInput));
        }

        string? pos = Str(rec, "pos");
        Hash128? posCtx = null;
        if (!string.IsNullOrEmpty(pos))
        {
            Hash128 posId = NativeAttestation.ResolvePos(pos!, NativeAttestation.PosTagset.Wiktionary);
            posCtx = posId;
            b.AddEntity(new EntityRow(posId, EntityTier.Vocabulary, PosReference.PosTypeId, WiktionaryDecomposer.Source));
            b.AddAttestation(NativeAttestation.PosWiktionary(
                w, pos!, WiktionaryDecomposer.Source, posCtx, SourceTrust.AcademicCuratedUserInput));
        }

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

                foreach (var (prop, typeName) in RelMap)
                    if (sense.TryGetProperty(prop, out var sarr) && sarr.ValueKind == JsonValueKind.Array)
                        foreach (var el in sarr.EnumerateArray())
                            AttestText(b, w, typeName, Str(el, "word"), posCtx);
                if (sense.TryGetProperty("coordinate_terms", out var coord) && coord.ValueKind == JsonValueKind.Array)
                    foreach (var el in coord.EnumerateArray())
                        AttestText(b, w, "IS_COORDINATE_TERM_WITH", Str(el, "word"), posCtx);

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
                            // context_id is an entity reference — witness it, never RootId it
                            dialectCtx = ContentEmitter.Emit(b, tg.GetString()!, WiktionaryDecomposer.Source);
                            break;
                        }
                AttestText(b, w, "TRANSCRIBES_AS", ipa, dialectCtx);
            }

        if (options.EmitCrossLanguageLinks
            && rec.TryGetProperty("translations", out var tr) && tr.ValueKind == JsonValueKind.Array)
            foreach (var t in tr.EnumerateArray())
                AttestText(b, w, "IS_TRANSLATION_OF", Str(t, "word"), null);

        foreach (var (prop, typeName) in RelMap)
            if (rec.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                    AttestText(b, w, typeName, Str(el, "word"), null);
        if (rec.TryGetProperty("coordinate_terms", out var rootCoord) && rootCoord.ValueKind == JsonValueKind.Array)
            foreach (var el in rootCoord.EnumerateArray())
                AttestText(b, w, "IS_COORDINATE_TERM_WITH", Str(el, "word"), null);

        if (rec.TryGetProperty("forms", out var forms) && forms.ValueKind == JsonValueKind.Array)
            foreach (var f in forms.EnumerateArray())
                AttestText(b, w, "HAS_VARIANT_OF", Str(f, "form"), null);

        AttestText(b, w, "HAS_ETYMOLOGY", Str(rec, "etymology_text"), null);

        if (rec.TryGetProperty("etymology_templates", out var etys) && etys.ValueKind == JsonValueKind.Array)
            foreach (var et in etys.EnumerateArray())
            {
                string? name = Str(et, "name");
                if (name is not ("bor" or "borrowed" or "der" or "derived" or "inh" or "inherited")) continue;
                if (!et.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object) continue;
                string? term = args.TryGetProperty("3", out var a3) && a3.ValueKind == JsonValueKind.String
                    ? a3.GetString() : null;
                if (string.IsNullOrWhiteSpace(term) || term == "-") continue;
                AttestText(b, w, "ETYMOLOGICALLY_DERIVED_FROM", term, null);
            }

        return true;
    }

    // The tier-witness law: an attestation object must be WITNESSED content, never a
    // bare RootId — id-without-witness is a ghost reference (no entity, no trajectory,
    // no tier; invisible to content_index/generation). Emit deposits the content tree
    // at its natural tier through the builder's coalesced stage (cheap since the
    // per-witness round-trip cost died), then the claim attests to real content.
    private static void AttestText(
        SubstrateChangeBuilder b, Hash128 subject, string typeName, string? text,
        Hash128? context)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var id = ContentEmitter.Emit(b, text!, WiktionaryDecomposer.Source);
        if (id is null) return;
        b.AddAttestation(NativeAttestation.Categorical(
            subject, typeName, id.Value, WiktionaryDecomposer.Source, SourceTrust.AcademicCuratedUserInput,
            contextId: context));
    }

    private static string? Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
