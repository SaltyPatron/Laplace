using System.Runtime.CompilerServices;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.Wiktionary;

public sealed class WiktionaryDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/WiktionaryDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCuratedWithUserInput/v1");

    private static readonly Hash128 LanguageTypeId = Hash128.OfCanonical("substrate/type/Language/v1");

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
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    private static Hash128 PosId(string p) => PosReference.Resolve(p, PosReference.PosTagset.Wiktionary);

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddRelationType("HAS_POS");
        boot.AddRelationType("HAS_DEFINITION");
        boot.AddRelationType("HAS_EXAMPLE");
        boot.AddRelationType("HAS_ETYMOLOGY");
        boot.AddRelationType("HAS_HYPERNYM");
        boot.AddRelationType("HAS_HYPONYM");
        boot.AddRelationType("IS_PART_OF");
        boot.AddRelationType("IS_SYNONYM_OF");
        boot.AddRelationType("IS_ANTONYM_OF");
        boot.AddRelationType("DERIVATIONALLY_RELATED");
        boot.AddRelationType("RELATED_TO");
        boot.AddRelationType("IS_COORDINATE_TERM_WITH");
        boot.AddRelationType("HAS_USAGE_REGISTER");
        boot.AddRelationType("HAS_DOMAIN_TOPIC");
        boot.AddRelationType("ETYMOLOGICALLY_DERIVED_FROM");
        await context.Writer.ApplyAsync(boot.Build(), ct);

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
        string? file = ResolveInput(context.EcosystemPath, options.Languages);
        if (file is null) yield break;
        int batch = options.BatchSize > 1 ? options.BatchSize : 1024;

        var b = NewBuilder("wiktionary/batch-0", batch);
        int n = 0, bn = 0;

        await foreach (var line in File.ReadLinesAsync(file, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (line.Length == 0 || line[0] != '{') continue;

            bool emitted;
            try { emitted = EmitRecord(b, line, options); }
            catch (JsonException) { continue; }

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

    private static readonly HashSet<string> RegisterTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "archaic", "obsolete", "dated", "slang", "colloquial", "informal", "formal",
        "vulgar", "offensive", "derogatory", "humorous", "euphemistic", "dialectal",
        "regional", "literary", "poetic", "technical", "rare", "nonstandard",
        "historical", "figurative",
    };

    private static bool EmitRecord(SubstrateChangeBuilder b, string line, DecomposerOptions options)
    {
        using var doc = JsonDocument.Parse(line);
        var rec = doc.RootElement;

        string? word = Str(rec, "word");
        if (string.IsNullOrEmpty(word)) return false;

        string? langCode = Str(rec, "lang_code") ?? Str(rec, "lang");
        if (options.Languages?.MatchesRaw(langCode) == false) return false;

        var wordId = ContentEmitter.Emit(b, word!, Source);
        if (wordId is null) return false;
        Hash128 w = wordId.Value;

        if (!string.IsNullOrEmpty(langCode))
        {
            Hash128 langId = LanguageReference.Resolve(langCode!);
            b.AddEntity(new EntityRow(langId, EntityTier.Vocabulary, LanguageTypeId, Source));
            b.AddAttestation(RelationTypeRegistry.Attest(
                w, "HAS_LANGUAGE", langId, Source, SourceTrust.AcademicCuratedUserInput));
        }

        string? pos = Str(rec, "pos");
        Hash128? posCtx = null;
        if (!string.IsNullOrEmpty(pos))
        {
            Hash128 posId = PosId(pos!);
            posCtx = posId;
            b.AddEntity(new EntityRow(posId, EntityTier.Vocabulary, PosReference.PosTypeId, Source));
            b.AddAttestation(RelationTypeRegistry.Attest(
                w, "HAS_POS", posId, Source, SourceTrust.AcademicCuratedUserInput));
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
                            dialectCtx = ContentEmitter.Emit(b, tg.GetString()!, Source);
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

    private static void AttestText(
        SubstrateChangeBuilder b, Hash128 subject, string typeName, string? text,
        Hash128? context)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var id = ContentEmitter.Emit(b, text!, Source);
        if (id is null) return;
        b.AddAttestation(RelationTypeRegistry.Attest(
            subject, typeName, id.Value, Source, SourceTrust.AcademicCuratedUserInput,
            contextId: context));
    }

    private static string? Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string? ResolveInput(string dir, LanguageFilter? langs)
    {
        if (langs?.IsActive == true)
        {
            string eng = Path.Combine(dir, "kaikki.org-dictionary-English.jsonl");
            if (File.Exists(eng)) return eng;
        }
        foreach (var name in new[] { "raw-wiktextract-data.jsonl", "kaikki.org-dictionary-English.jsonl" })
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
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
