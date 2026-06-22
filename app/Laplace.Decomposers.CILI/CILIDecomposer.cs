using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.CILI;

/// <summary>
/// Ingests the Collaborative Inter-Lingual Index (CILI) as first-class substrate: every ILI
/// concept node, its authoritative English definition, and the cross-wordnet-version synset
/// offset maps. The ILI is the convergence backbone — a synset anchor used by WordNet/OMW/
/// ConceptNet/SemLink is the content-address of the ILI string (e.g. "i1"), so the concept
/// entities CILI emits here ARE the same entities those sources attach lemmas/definitions to.
/// Previously CILI was read only as a runtime offset→ILI lookup and never ingested; this makes
/// the index — concepts, definitions, and the 9 cross-version maps — first-class with provenance.
/// </summary>
public sealed class CILIDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/CILIDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    private static readonly Hash128 SynsetTypeId = EntityTypeRegistry.WordNetSynset;
    private static readonly Hash128 EngLang = LanguageEntityId.FromIso639_3("eng");

    private const int BatchSize = 2048;

    public Hash128 SourceId     => Source;
    public string  SourceName   => "CILIDecomposer";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> CanonicalNamesForReadback => _names;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("WordNet_Synset");
        boot.AddRelationType("IS_TYPED_AS");
        boot.AddRelationType("HAS_DEFINITION");
        boot.AddRelationType("HAS_NAME_ALIAS");
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string root = context.EcosystemPath;

        // ---- Pass 1: ili.ttl — concept nodes + authoritative English definitions ----
        // <iN> a <Concept> ; skos:definition "..."@en ; dc:source pwn30:OFFSET-POS .
        string ttl = Path.Combine(root, "ili.ttl");
        if (File.Exists(ttl))
        {
            var b = NewBuilder("cili/concepts", 0);
            int n = 0, bn = 0;
            await foreach (var (ili, def) in ParseIliTtlAsync(ttl, ct))
            {
                if (ContentEmitter.Emit(b, ili, Source) is not { } id) continue;
                b.AddAttestation(NativeAttestation.Categorical(
                    id, "IS_TYPED_AS", SynsetTypeId, Source, TC.AcademicCurated));
                if (def is { Length: > 0 } && ContentEmitter.Emit(b, def, Source) is { } dId)
                    b.AddAttestation(NativeAttestation.Categorical(
                        id, "HAS_DEFINITION", dId, Source, TC.AcademicCurated, EngLang));

                if (++n >= BatchSize)
                {
                    if (!options.DryRun) yield return b.Build();
                    b = NewBuilder("cili/concepts", ++bn);
                    n = 0;
                    await Task.Yield();
                }
            }
            if (n > 0 && !options.DryRun) yield return b.Build();
        }

        // ---- Pass 2: ili-map-*.tab — cross-wordnet-version synset offset maps ----
        // Each line: iN \t OFFSET-POS  (pwn15/16/17/171/20/21/30/31, recursive).
        // Records, per version, that this ILI concept is aliased by that version's synset key,
        // so the full cross-version mapping is queryable first-class substrate.
        var tabs = Directory.EnumerateFiles(root, "ili-map-*.tab", SearchOption.AllDirectories)
                            .OrderBy(p => p, StringComparer.Ordinal);
        foreach (var tab in tabs)
        {
            ct.ThrowIfCancellationRequested();
            string version = VersionLabel(tab);
            var mb = NewBuilder($"cili/map/{version}", 0);
            int mn = 0, mbn = 0;
            await foreach (var line in File.ReadLinesAsync(tab, ct))
            {
                int sep = line.IndexOf('\t');
                if (sep <= 0) continue;
                string ili = line[..sep].Trim();
                string offsetPos = line[(sep + 1)..].Trim();
                if (ili.Length == 0 || offsetPos.Length == 0 || ili[0] != 'i') continue;

                if (ContentEmitter.Emit(mb, ili, Source) is not { } id) continue;
                string aliasName = $"substrate/cili/synset-key/{version}:{offsetPos}/v1";
                var aliasId = Hash128.OfCanonical(aliasName);
                _names.Add(aliasName);
                mb.AddEntity(aliasId, EntityTier.Vocabulary, SynsetTypeId, Source);
                mb.AddAttestation(NativeAttestation.Categorical(
                    id, "HAS_NAME_ALIAS", aliasId, Source, TC.AcademicCurated));

                if (++mn >= BatchSize)
                {
                    if (!options.DryRun) yield return mb.Build();
                    mb = NewBuilder($"cili/map/{version}", ++mbn);
                    mn = 0;
                    await Task.Yield();
                }
            }
            if (mn > 0 && !options.DryRun) yield return mb.Build();
        }
    }

    private static SubstrateChangeBuilder NewBuilder(string label, int bn) =>
        new(Source, $"{label}-{bn}", null,
            entityCapacity: BatchSize * 4, physicalityCapacity: BatchSize * 4,
            attestationCapacity: BatchSize * 4);

    private static string VersionLabel(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);   // ili-map-pwn30
        const string prefix = "ili-map-";
        return name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
    }

    private static async IAsyncEnumerable<(string Ili, string? Def)> ParseIliTtlAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        string? curIli = null;
        string? curDef = null;
        await foreach (var raw in File.ReadLinesAsync(path, ct))
        {
            string t = raw.Trim();
            if (t.Length == 0) continue;

            bool isSubject = t.Length > 2 && t[0] == '<' && t[1] == 'i' && char.IsDigit(t[2]);
            if (isSubject)
            {
                if (curIli is not null) yield return (curIli, curDef);
                int gt = t.IndexOf('>');
                curIli = gt > 1 ? t[1..gt] : null;   // "i1"
                curDef = null;
                if (t.EndsWith('.'))
                {
                    if (curIli is not null) yield return (curIli, curDef);
                    curIli = null; curDef = null;
                }
                continue;
            }

            if (curIli is null) continue;
            if (t.Contains("skos:definition", StringComparison.Ordinal))
                curDef = ExtractTurtleString(t) ?? curDef;
            if (t.EndsWith('.'))
            {
                yield return (curIli, curDef);
                curIli = null; curDef = null;
            }
        }
        if (curIli is not null) yield return (curIli, curDef);
    }

    private static string? ExtractTurtleString(string line)
    {
        int first = line.IndexOf('"');
        if (first < 0) return null;
        int last = line.LastIndexOf('"');
        if (last <= first) return null;
        return line[(first + 1)..last].Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(120_000L);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
