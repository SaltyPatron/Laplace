using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.ISO;

public sealed class ISODecomposer : IDecomposer
{


    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/ISO639Decomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StandardsDerived/v1");

    private static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;
    private static readonly Hash128 Iso639CodeTypeId = EntityTypeRegistry.Iso639Code;
    private static readonly Hash128 RelTypeIsLanguageCode =
        RelationTypeRegistry.RelationTypeId("IS_LANGUAGE_CODE");
    private static readonly Hash128 RelTypeHasIso6391Code =
        RelationTypeRegistry.RelationTypeId("HAS_ISO639_1_CODE");
    private static readonly Hash128 RelTypeUsesScript =
        RelationTypeRegistry.RelationTypeId("USES_SCRIPT");
    private static readonly Hash128 RelTypeMemberOfMacrolanguage =
        RelationTypeRegistry.RelationTypeId("MEMBER_OF_MACROLANGUAGE");
    private static readonly Hash128 UcdClassifierTypeId = EntityTypeRegistry.UcdClassifier;
    private static readonly Hash128 LanguageVariantTypeId = EntityTypeRegistry.LanguageVariant;

    public Hash128 SourceId => Source;
    public string SourceName => "ISO639Decomposer";
    public int LayerOrder => 1;
    public Hash128 TrustClassId => TrustClass;

    private readonly HashSet<string> _codeNames = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> CanonicalNamesForReadback => _codeNames;

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            typeNodeNames: ["Language", "ISO639Code", "LanguageVariant"],
            relationNodeNames: ["IS_LANGUAGE_CODE", "HAS_ISO639_1_CODE", "USES_SCRIPT",
                "MEMBER_OF_MACROLANGUAGE", "HAS_ISO639_2_CODE", "HAS_LANGUAGE_SCOPE",
                "HAS_LANGUAGE_TYPE", "HAS_VARIANT_OF", "HAS_DEFINITION", "HAS_NAME_ALIAS"],
            ct: ct);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string dataPath = Path.Combine(context.EcosystemPath, "iso-639-3.tab");
        var reader = context.Reader;
        int batch = options.BatchSize > 1 ? options.BatchSize : 2048;

        if (File.Exists(dataPath))
        {
            await foreach (var change in DecomposerBatch.RunAsync(
                               ParseAsync(dataPath, ct),
                               StageIsoTabRecord,
                               Source, "iso639-3", batch, reader, options, ct))
                yield return change;
        }

        await foreach (var change in DecomposerBatch.RunAsync(
                           EnumerateMacrolanguageRecords(context.EcosystemPath, ct),
                           StageMacrolanguageRecord,
                           Source, "iso639/macrolanguages", batch, reader, options, ct))
            yield return change;

        string unidata = Path.GetFullPath(
            Path.Combine(context.EcosystemPath, "..", "UCD", "Public", "UCD", "latest", "ucd"));
        var scriptName = LanguageGraph.LoadScriptCodeToUcdName(unidata);
        await foreach (var change in DecomposerBatch.RunAsync(
                           EnumerateScriptRecords(context.EcosystemPath, scriptName, ct),
                           StageScriptRecord,
                           Source, "iso639/scripts", batch, reader, options, ct))
            yield return change;

        string retPath = Path.Combine(context.EcosystemPath, "iso-639-3_Retirements.tab");
        if (File.Exists(retPath))
        {
            await foreach (var change in DecomposerBatch.RunAsync(
                               ParseRetirementsAsync(retPath, ct),
                               StageRetirementRecord,
                               Source, "iso639/retirements", batch, reader, options, ct))
                yield return change;
        }

        await foreach (var change in DecomposerBatch.RunAsync(
                           EnumerateVariantRecords(context.EcosystemPath, ct),
                           StageVariantRecord,
                           Source, "iso639/variants", batch, reader, options, ct))
            yield return change;

        string namePath = Path.Combine(context.EcosystemPath, "iso-639-3_Name_Index.tab");
        if (File.Exists(namePath))
        {
            await foreach (var change in DecomposerBatch.RunAsync(
                               ParseNameIndexAsync(namePath, ct),
                               (rec, nb) =>
                               {
                                   var lid = LanguageEntityId.FromIso639_3(rec.Id);
                                   nb.AddEntity(lid, EntityTier.Word, LanguageTypeId, Source);
                                   if (ContentEmitter.Emit(nb, rec.PrintName, Source) is { } nid)
                                       nb.AddAttestation(NativeAttestation.Categorical(
                                           lid, "HAS_DEFINITION", nid, Source, SourceTrust.StandardsDerived));
                               },
                               Source, "iso639/names", batch, reader, options, ct))
                yield return change;
        }

        IntentStage.ResetContentBank();
    }

    private void StageIsoTabRecord(IsoRecord rec, SubstrateChangeBuilder b)
    {
        var langId = LanguageEntityId.FromIso639_3(rec.Id);
        b.AddEntity(langId, EntityTier.Word, LanguageTypeId, Source);
        _codeNames.Add(VocabularyNames.LanguageIso639_3(rec.Id));
        b.AddAttestation(NativeAttestation.CategoricalResolved(
            langId, RelTypeIsLanguageCode, null, Source, null,
            RelationTypeRank.StandardsStructural * SourceTrust.StandardsDerived));

        if (rec.Part1.Length > 0)
        {
            var iso1Name = $"iso639-1:{rec.Part1}";
            _codeNames.Add(iso1Name);
            var iso1Id = Hash128.OfCanonical(iso1Name);
            b.AddEntity(iso1Id, EntityTier.Word, Iso639CodeTypeId, Source);
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                langId, RelTypeHasIso6391Code, iso1Id, Source, null,
                RelationTypeRank.StandardsStructural * SourceTrust.StandardsDerived));
        }

        foreach (var (p2, rel) in new[] { (rec.Part2b, "HAS_ISO639_2B_CODE"), (rec.Part2t, "HAS_ISO639_2T_CODE") })
        {
            if (p2.Length == 0) continue;
            var iso2Name = $"iso639-2:{p2}";
            _codeNames.Add(iso2Name);
            var iso2Id = Hash128.OfCanonical(iso2Name);
            b.AddEntity(iso2Id, EntityTier.Word, Iso639CodeTypeId, Source);
            b.AddAttestation(NativeAttestation.Categorical(
                langId, rel, iso2Id, Source, SourceTrust.StandardsDerived));
        }
        if (rec.Scope.Length > 0)
        {
            var scopeId = Hash128.OfCanonical($"substrate/iso639/scope/{rec.Scope}/v1");
            _codeNames.Add($"substrate/iso639/scope/{rec.Scope}/v1");
            b.AddEntity(scopeId, EntityTier.Word, Iso639CodeTypeId, Source);
            b.AddAttestation(NativeAttestation.Categorical(
                langId, "HAS_LANGUAGE_SCOPE", scopeId, Source, SourceTrust.StandardsDerived));
        }
        if (rec.Type.Length > 0)
        {
            var typeId = Hash128.OfCanonical($"substrate/iso639/type/{rec.Type}/v1");
            _codeNames.Add($"substrate/iso639/type/{rec.Type}/v1");
            b.AddEntity(typeId, EntityTier.Word, Iso639CodeTypeId, Source);
            b.AddAttestation(NativeAttestation.Categorical(
                langId, "HAS_LANGUAGE_TYPE", typeId, Source, SourceTrust.StandardsDerived));
        }
        if (rec.RefName.Length > 0)
        {
            var nameId = ContentEmitter.Emit(b, rec.RefName, Source);
            if (nameId is { } nid)
            {
                b.AddAttestation(NativeAttestation.Categorical(
                    langId, "HAS_NAME_ALIAS", nid, Source, SourceTrust.StandardsDerived));
                b.AddAttestation(NativeAttestation.Categorical(
                    langId, "HAS_DEFINITION", nid, Source, SourceTrust.StandardsDerived));
            }
        }
    }

    private static void StageMacrolanguageRecord((string Indiv, string Macro) rec, SubstrateChangeBuilder b)
    {
        var indivId = LanguageEntityId.FromIso639_3(rec.Indiv);
        var macroId = LanguageEntityId.FromIso639_3(rec.Macro);
        b.AddEntity(indivId, EntityTier.Word, LanguageTypeId, Source);
        b.AddEntity(macroId, EntityTier.Word, LanguageTypeId, Source);
        b.AddAttestation(NativeAttestation.CategoricalResolved(
            indivId, RelTypeMemberOfMacrolanguage, macroId, Source, null,
            RelationTypeRank.StandardsStructural * SourceTrust.StandardsDerived));
    }

    private readonly record struct ScriptRecord(string Subtag, string ScriptName);

    private void StageScriptRecord(ScriptRecord rec, SubstrateChangeBuilder b)
    {
        var langId = LanguageReference.Resolve(rec.Subtag);
        if (langId.Equals(LanguageEntityId.FromIso639_3("und"))) return;
        b.AddEntity(langId, EntityTier.Word, LanguageTypeId, Source);
        _codeNames.Add($"unicode/script/{rec.ScriptName}/v1");
        var scriptId = LanguageGraph.ScriptEntityId(rec.ScriptName);
        b.AddEntity(scriptId, EntityTier.Word, UcdClassifierTypeId, Source);
        b.AddAttestation(NativeAttestation.CategoricalResolved(
            langId, RelTypeUsesScript, scriptId, Source, null,
            RelationTypeRank.StandardsStructural * SourceTrust.StandardsDerived));
    }

    private static void StageRetirementRecord((string Retired, string Successor) rec, SubstrateChangeBuilder b)
    {
        var retId = LanguageEntityId.FromIso639_3(rec.Retired);
        var sucId = LanguageEntityId.FromIso639_3(rec.Successor);
        b.AddEntity(retId, EntityTier.Word, LanguageTypeId, Source);
        b.AddEntity(sucId, EntityTier.Word, LanguageTypeId, Source);
        b.AddAttestation(NativeAttestation.Categorical(
            retId, "SUPERSEDED_BY", sucId, Source, SourceTrust.StandardsDerived));
    }

    private void StageVariantRecord((string Subtag, string Prefix) rec, SubstrateChangeBuilder b)
    {
        var variantId = LanguageGraph.VariantEntityId(rec.Subtag);
        _codeNames.Add($"substrate/iso639/variant/{rec.Subtag.ToLowerInvariant()}/v1");
        b.AddEntity(variantId, EntityTier.Word, LanguageVariantTypeId, Source);
        var parentId = LanguageReference.Resolve(rec.Prefix);
        if (parentId.Equals(LanguageEntityId.FromIso639_3("und"))) return;
        b.AddEntity(parentId, EntityTier.Word, LanguageTypeId, Source);
        b.AddAttestation(NativeAttestation.Categorical(
            variantId, "HAS_VARIANT_OF", parentId, Source, SourceTrust.StandardsDerived));
    }

    private static async IAsyncEnumerable<(string Indiv, string Macro)> EnumerateMacrolanguageRecords(
        string ecosystemPath, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var pair in LanguageGraph.Macrolanguages(ecosystemPath))
        {
            ct.ThrowIfCancellationRequested();
            yield return pair;
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<ScriptRecord> EnumerateScriptRecords(
        string ecosystemPath, Dictionary<string, string> scriptName,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var (subtag, scriptCodes) in LanguageGraph.LanguageScripts(ecosystemPath))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var code in scriptCodes)
            {
                if (!scriptName.TryGetValue(code, out var name)) continue;
                yield return new ScriptRecord(subtag, name);
            }
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<(string Retired, string Successor)> ParseRetirementsAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        bool hdr = false;
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (!hdr) { hdr = true; continue; }
            var c = line.Split('\t');
            if (c.Length < 4) continue;
            string retired = c[0].Trim(), changeTo = c[3].Trim();
            if (retired.Length != 3 || changeTo.Length != 3) continue;
            yield return (retired, changeTo);
        }
    }

    private static async IAsyncEnumerable<(string Subtag, string Prefix)> EnumerateVariantRecords(
        string ecosystemPath, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var (subtag, prefixes) in LanguageGraph.Variants(ecosystemPath))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var prefix in prefixes)
                yield return (subtag, prefix);
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<(string Id, string PrintName)> ParseNameIndexAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        bool hdr = false;
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (!hdr) { hdr = true; continue; }
            var c = line.Split('\t');
            if (c.Length < 2) continue;
            string id = c[0].Trim(), printName = c[1].Trim();
            if (id.Length != 3 || printName.Length == 0) continue;
            yield return (id, printName);
        }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(7929L);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async IAsyncEnumerable<IsoRecord> ParseAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct)
    {
        bool headerSkipped = false;
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (!headerSkipped) { headerSkipped = true; continue; }
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split('\t');
            if (parts.Length < 7) continue;

            string id = parts[0].Trim();
            string part2b = parts[1].Trim();
            string part2t = parts[2].Trim();
            string part1 = parts[3].Trim();
            string scope = parts[4].Trim();
            string type = parts[5].Trim();
            string refName = parts[6].Trim();
            if (id.Length != 3) continue;

            yield return new IsoRecord(id, part2b, part2t, part1, scope, type, refName);
        }
    }

    private readonly record struct IsoRecord(
        string Id, string Part2b, string Part2t, string Part1,
        string Scope, string Type, string RefName);
}
