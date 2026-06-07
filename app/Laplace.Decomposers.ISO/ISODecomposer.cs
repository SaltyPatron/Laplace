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

    private static readonly Hash128 LanguageTypeId =
        Hash128.OfCanonical("substrate/type/Language/v1");
    private static readonly Hash128 Iso639CodeTypeId =
        Hash128.OfCanonical("substrate/type/ISO639Code/v1");
    private static readonly Hash128 KindIsLanguageCode =
        Hash128.OfCanonical("substrate/kind/IS_LANGUAGE_CODE/v1");
    private static readonly Hash128 KindHasIso6391Code =
        Hash128.OfCanonical("substrate/kind/HAS_ISO639_1_CODE/v1");
    private static readonly Hash128 KindUsesScript =
        Hash128.OfCanonical("substrate/kind/USES_SCRIPT/v1");
    private static readonly Hash128 KindMemberOfMacrolanguage =
        Hash128.OfCanonical("substrate/kind/MEMBER_OF_MACROLANGUAGE/v1");
    private static readonly Hash128 UcdClassifierTypeId =
        Hash128.OfCanonical("substrate/type/UcdClassifier/v1");

    public Hash128 SourceId    => Source;
    public string  SourceName  => "ISO639Decomposer";
    public int     LayerOrder  => 1;
    public Hash128 TrustClassId => TrustClass;

    private readonly HashSet<string> _codeNames = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> CanonicalNamesForReadback => _codeNames;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("Language");
        boot.AddType("ISO639Code");
        boot.AddRelationType("IS_LANGUAGE_CODE");
        boot.AddRelationType("HAS_ISO639_1_CODE");
        boot.AddRelationType("USES_SCRIPT");
        boot.AddRelationType("MEMBER_OF_MACROLANGUAGE");
        boot.AddRelationType("HAS_ISO639_2_CODE");
        boot.AddRelationType("HAS_LANGUAGE_SCOPE");
        boot.AddRelationType("HAS_LANGUAGE_TYPE");
        boot.AddRelationType("HAS_VARIANT_OF");
        boot.AddRelationType("HAS_DEFINITION");
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string dataPath = Path.Combine(context.EcosystemPath, "iso-639-3.tab");

        var b = new SubstrateChangeBuilder(
            Source, "iso639-3/all", null,
            entityCapacity: 24_000, physicalityCapacity: 0, attestationCapacity: 48_000);

        await foreach (var rec in ParseAsync(dataPath, ct))
        {
            var langId = LanguageEntityId.FromIso639_3(rec.Id);
            b.AddEntity(langId, (byte)MetaTier.Meta, LanguageTypeId, Source);
            b.AddAttestation(AttestationFactory.Create(
                langId, KindIsLanguageCode, null, Source, null,
                RelationTypeRank.Partitive, SourceTrust.StandardsDerived));

            if (rec.Part1.Length > 0)
            {
                var iso1Name = $"iso639-1:{rec.Part1}";
                _codeNames.Add(iso1Name);
                var iso1Id = Hash128.OfCanonical(iso1Name);
                b.AddEntity(iso1Id, (byte)MetaTier.Meta, Iso639CodeTypeId, Source);
                b.AddAttestation(AttestationFactory.Create(
                    langId, KindHasIso6391Code, iso1Id, Source, null,
                    RelationTypeRank.Partitive, SourceTrust.StandardsDerived));
            }

            foreach (var p2 in new[] { rec.Part2b, rec.Part2t }.Distinct())
            {
                if (p2.Length == 0) continue;
                var iso2Name = $"iso639-2:{p2}";
                _codeNames.Add(iso2Name);
                var iso2Id = Hash128.OfCanonical(iso2Name);
                b.AddEntity(iso2Id, (byte)MetaTier.Meta, Iso639CodeTypeId, Source);
                b.AddAttestation(RelationTypeRegistry.Attest(
                    langId, "HAS_ISO639_2_CODE", iso2Id, Source, SourceTrust.StandardsDerived));
            }
            if (rec.Scope.Length > 0)
            {
                var scopeId = Hash128.OfCanonical($"substrate/iso639/scope/{rec.Scope}/v1");
                b.AddEntity(scopeId, (byte)MetaTier.Meta, Iso639CodeTypeId, Source);
                b.AddAttestation(RelationTypeRegistry.Attest(
                    langId, "HAS_LANGUAGE_SCOPE", scopeId, Source, SourceTrust.StandardsDerived));
            }
            if (rec.Type.Length > 0)
            {
                var typeId = Hash128.OfCanonical($"substrate/iso639/type/{rec.Type}/v1");
                b.AddEntity(typeId, (byte)MetaTier.Meta, Iso639CodeTypeId, Source);
                b.AddAttestation(RelationTypeRegistry.Attest(
                    langId, "HAS_LANGUAGE_TYPE", typeId, Source, SourceTrust.StandardsDerived));
            }
        }

        var undId = LanguageEntityId.FromIso639_3("und");

        foreach (var (indiv, macro) in LanguageGraph.Macrolanguages(context.EcosystemPath))
        {
            var indivId = LanguageEntityId.FromIso639_3(indiv);
            var macroId = LanguageEntityId.FromIso639_3(macro);
            b.AddEntity(indivId, (byte)MetaTier.Meta, LanguageTypeId, Source);
            b.AddEntity(macroId, (byte)MetaTier.Meta, LanguageTypeId, Source);
            b.AddAttestation(AttestationFactory.Create(
                indivId, KindMemberOfMacrolanguage, macroId, Source, null,
                RelationTypeRank.Taxonomic, SourceTrust.StandardsDerived));
        }

        string unidata = Path.GetFullPath(
            Path.Combine(context.EcosystemPath, "..", "Unicode", "Public", "UNIDATA"));
        var scriptName = LanguageGraph.LoadScriptCodeToUcdName(unidata);
        foreach (var (subtag, scriptCodes) in LanguageGraph.LanguageScripts(context.EcosystemPath))
        {
            var langId = LanguageReference.Resolve(subtag);
            if (langId.Equals(undId)) continue;
            b.AddEntity(langId, (byte)MetaTier.Meta, LanguageTypeId, Source);
            foreach (var code in scriptCodes)
            {
                if (!scriptName.TryGetValue(code, out var name)) continue;
                var scriptId = LanguageGraph.ScriptEntityId(name);
                b.AddEntity(scriptId, (byte)MetaTier.Meta, UcdClassifierTypeId, Source);
                b.AddAttestation(AttestationFactory.Create(
                    langId, KindUsesScript, scriptId, Source, null,
                    RelationTypeRank.StandardsStructural, SourceTrust.StandardsDerived));
            }
        }

        string retPath = Path.Combine(context.EcosystemPath, "iso-639-3_Retirements.tab");
        if (File.Exists(retPath))
        {
            bool hdr = false;
            foreach (var line in File.ReadLines(retPath))
            {
                if (!hdr) { hdr = true; continue; }
                var c = line.Split('\t');
                if (c.Length < 4) continue;
                string retired = c[0].Trim(), changeTo = c[3].Trim();
                if (retired.Length != 3 || changeTo.Length != 3) continue;
                var retId = LanguageEntityId.FromIso639_3(retired);
                var sucId = LanguageEntityId.FromIso639_3(changeTo);
                b.AddEntity(retId, (byte)MetaTier.Meta, LanguageTypeId, Source);
                b.AddEntity(sucId, (byte)MetaTier.Meta, LanguageTypeId, Source);
                b.AddAttestation(RelationTypeRegistry.Attest(
                    retId, "HAS_VARIANT_OF", sucId, Source, SourceTrust.StandardsDerived));
            }
        }

        if (!options.DryRun)
            yield return b.Build();
        await Task.Yield();

        string namePath = Path.Combine(context.EcosystemPath, "iso-639-3_Name_Index.tab");
        if (File.Exists(namePath))
        {
            var nb = new SubstrateChangeBuilder(Source, "iso639/names-0", null,
                entityCapacity: 4096, physicalityCapacity: 4096, attestationCapacity: 4096);
            int n = 0, bn = 0;
            bool hdr = false;
            foreach (var line in File.ReadLines(namePath))
            {
                ct.ThrowIfCancellationRequested();
                if (!hdr) { hdr = true; continue; }
                var c = line.Split('\t');
                if (c.Length < 2) continue;
                string id = c[0].Trim(), printName = c[1].Trim();
                if (id.Length != 3 || printName.Length == 0) continue;
                var lid = LanguageEntityId.FromIso639_3(id);
                nb.AddEntity(lid, (byte)MetaTier.Meta, LanguageTypeId, Source);
                var nameId = ContentEmitter.Emit(nb, printName, Source);
                if (nameId is { } nid)
                    nb.AddAttestation(RelationTypeRegistry.Attest(
                        lid, "HAS_DEFINITION", nid, Source, SourceTrust.StandardsDerived));
                if (++n >= 2048)
                {
                    if (!options.DryRun) yield return nb.Build();
                    nb = new SubstrateChangeBuilder(Source, $"iso639/names-{++bn}", null,
                        entityCapacity: 4096, physicalityCapacity: 4096, attestationCapacity: 4096);
                    n = 0;
                    await Task.Yield();
                }
            }
            if (n > 0 && !options.DryRun) yield return nb.Build();
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

            string id     = parts[0].Trim();
            string part2b = parts[1].Trim();
            string part2t = parts[2].Trim();
            string part1  = parts[3].Trim();
            string scope  = parts[4].Trim();
            string type   = parts[5].Trim();
            string refName = parts[6].Trim();
            if (id.Length != 3) continue;

            yield return new IsoRecord(id, part2b, part2t, part1, scope, type, refName);
        }
    }

    private readonly record struct IsoRecord(
        string Id, string Part2b, string Part2t, string Part1,
        string Scope, string Type, string RefName);
}
