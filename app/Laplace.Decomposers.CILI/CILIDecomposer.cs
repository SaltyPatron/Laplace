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
    private readonly object _namesLock = new();
    public IReadOnlyCollection<string> CanonicalNamesForReadback => _names;

    private void RecordName(string name)
    {
        lock (_namesLock) _names.Add(name);
    }

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("WordNet_Synset");
        boot.AddRelationType("IS_TYPED_AS");
        boot.AddRelationType("HAS_DEFINITION");
        boot.AddRelationType("HAS_SYNSET_KEY");
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string root = context.EcosystemPath;
        if (options.DryRun) yield break;

        // O(tier) skip-present via the SHARED two-phase containment (the same mechanism the grammar
        // sources use — not a CILI-specific dedup): each builder probes its content tree node ids and
        // stages only NOVEL subtrees, so the graphemes/words shared across 117k definitions are
        // committed ONCE, not re-COPYed and re-anti-joined every record.
        var reader = context.Reader;

        // P-CORE-PINNED PARALLEL COMPOSE. CILI's per-record work (content-id derivation = BLAKE3
        // over the ILI/definition canonical bytes + attestation assembly) is a pure function of the
        // record, so it fans out across P-core-pinned workers instead of running serial on one
        // thread. Each worker owns its own builder over a disjoint slice; ids are content-addressed
        // so duplicate ids across workers fold in the server-side merge. (Worker count alone would
        // let the OS scatter onto E-cores; the helper pins each worker thread.)
        int workers = IngestParallelism.ResolveFileWorkers(coreHeadroom: 1);

        // ---- Pass 1: ili.ttl — concept nodes + authoritative English definitions ----
        // <iN> a <Concept> ; skos:definition "..."@en ; dc:source pwn30:OFFSET-POS .
        string ttl = Path.Combine(root, "ili.ttl");
        if (File.Exists(ttl))
        {
            var changes = PCoreParallelCompose.RunAsync(
                ParseIliTtlAsync(ttl, ct),
                workers, BatchSize,
                () => NewBuilder("cili/concepts", 0).EnableDeferredContent(reader),
                (b, rec) =>
                {
                    var (ili, def) = rec;
                    if (ContentEmitter.Emit(b, ili, Source) is not { } id) return;
                    b.AddAttestation(NativeAttestation.Categorical(
                        id, "IS_TYPED_AS", SynsetTypeId, Source, TC.AcademicCurated));
                    if (def is { Length: > 0 } && ContentEmitter.Emit(b, def, Source) is { } dId)
                        b.AddAttestation(NativeAttestation.Categorical(
                            id, "HAS_DEFINITION", dId, Source, TC.AcademicCurated, EngLang));
                },
                ct);
            await foreach (var change in changes.WithCancellation(ct))
                yield return change;
        }

        // ---- Pass 2: ili-map-*.tab — cross-wordnet-version synset offset maps ----
        // Each line: iN \t OFFSET-POS  (pwn15/16/17/171/20/21/30/31, recursive).
        // The map is an ATTESTATION on the shared ILI concept, not a new entity per row: the
        // version-specific synset key (OFFSET-POS) is CONTENT (its own Merkle DAG / tiers / geometry),
        // and the WordNet VERSION is the attestation's CONTEXT (provenance) — neither is ever baked
        // into an id. This keeps the cross-version mapping first-class and provenance-bearing while
        // converging on the same content-addressed ILI concept the other wordnets resolve to (no
        // CILI-private alias entity, no dead nameless row).
        var tabs = Directory.EnumerateFiles(root, "ili-map-*.tab", SearchOption.AllDirectories)
                            .OrderBy(p => p, StringComparer.Ordinal);
        foreach (var tab in tabs)
        {
            ct.ThrowIfCancellationRequested();
            string version = VersionLabel(tab);
            var changes = PCoreParallelCompose.RunAsync(
                ParseIliMapAsync(tab, ct),
                workers, BatchSize,
                () => NewBuilder($"cili/map/{version}", 0).EnableDeferredContent(reader),
                (mb, rec) =>
                {
                    var (ili, offsetPos) = rec;
                    if (ContentEmitter.Emit(mb, ili, Source) is not { } id) return;
                    if (ContentEmitter.Emit(mb, offsetPos, Source) is not { } keyId) return;
                    var verCtx = ContentEmitter.Emit(mb, version, Source) ?? id;
                    mb.AddAttestation(NativeAttestation.Categorical(
                        id, "HAS_SYNSET_KEY", keyId, Source, TC.AcademicCurated, verCtx));
                },
                ct);
            await foreach (var change in changes.WithCancellation(ct))
                yield return change;
        }
    }

    private static async IAsyncEnumerable<(string Ili, string OffsetPos)> ParseIliMapAsync(
        string tab, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var line in File.ReadLinesAsync(tab, ct))
        {
            int sep = line.IndexOf('\t');
            if (sep <= 0) continue;
            string ili = line[..sep].Trim();
            string offsetPos = line[(sep + 1)..].Trim();
            if (ili.Length == 0 || offsetPos.Length == 0 || ili[0] != 'i') continue;
            yield return (ili, offsetPos);
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
