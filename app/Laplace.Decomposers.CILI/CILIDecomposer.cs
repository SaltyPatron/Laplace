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

    private const int DefaultBatchSize = 2048;

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

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            typeNodeNames: ["WordNet_Synset"],
            relationNodeNames: ["IS_TYPED_AS", "HAS_DEFINITION", "HAS_NAME_ALIAS", "HAS_SYNSET_KEY"],
            ct: ct);

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
        int batchSize = options.BatchSize > 1 ? options.BatchSize : DefaultBatchSize;

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
                workers, batchSize,
                () => NewBuilder("cili/concepts", 0, batchSize).EnableDeferredContent(reader),
                (b, rec) =>
                {
                    var (ili, def) = rec;
                    if (ContentEmitter.Emit(b, ili, Source) is not { } id) return;
                    b.AddAttestation(NativeAttestation.Categorical(
                        id, "IS_TYPED_AS", SynsetTypeId, Source, TC.AcademicCurated));
                    if (def is { Length: > 0 } && ContentEmitter.Emit(b, def, Source) is { } dId)
                    {
                        // The ILI id (i92375) stays the content-address anchor for convergence, but
                        // surface the English definition as the display name so the concept never
                        // renders as the opaque "i92375".
                        b.AddAttestation(NativeAttestation.Categorical(
                            id, "HAS_NAME_ALIAS", dId, Source, TC.AcademicCurated, EngLang));
                        b.AddAttestation(NativeAttestation.Categorical(
                            id, "HAS_DEFINITION", dId, Source, TC.AcademicCurated, EngLang));
                    }
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
                ParseIliMapAsync(tab, version, ct),
                workers, batchSize,
                () => NewBuilder($"cili/map/{version}", 0, batchSize).EnableDeferredContent(reader),
                EmitMapRow,
                ct);
            await foreach (var change in changes.WithCancellation(ct))
                yield return change;
        }

        // ---- Pass 3: ili-map-*.ttl — turtle-form cross-version maps ----
        // The .tab pass above only catches the .tab files (pwn15..pwn31). The CILI distribution ALSO
        // ships the maps in turtle: ili-map-odwn13.ttl (Open Dutch WordNet — the ONLY source for the
        // odwn13 offsets, never present as a .tab) plus ili-map.ttl / ili-map-wn30.ttl / ili-map-wn31.ttl.
        // Each data line is `<iN> owl:sameAs PREFIX:OFFSET-POS . # gloss` (or the `ili:iN ...` form). The
        // synset-key content emitted is the bare OFFSET-POS (so it converges with the .tab pass and the
        // wordnets), and the wordnet PREFIX (pwn30/pwn31/odwn13/...) is the attestation's version context
        // — provenance, never baked into an id. Dropping these silently lost the entire odwn13 mapping.
        var ttlMaps = Directory.EnumerateFiles(root, "ili-map-*.ttl", SearchOption.AllDirectories)
                               .OrderBy(p => p, StringComparer.Ordinal);
        foreach (var ttlMap in ttlMaps)
        {
            ct.ThrowIfCancellationRequested();
            string version = VersionLabel(ttlMap);
            var changes = PCoreParallelCompose.RunAsync(
                ParseIliMapTtlAsync(ttlMap, version, ct),
                workers, batchSize,
                () => NewBuilder($"cili/map/{version}", 0, batchSize).EnableDeferredContent(reader),
                EmitMapRow,
                ct);
            await foreach (var change in changes.WithCancellation(ct))
                yield return change;
        }
    }

    /// <summary>
    /// Stages one HAS_SYNSET_KEY attestation: the version-specific synset key (OFFSET-POS) is CONTENT
    /// and the wordnet version is the attestation CONTEXT. Shared by the .tab and .ttl map passes so
    /// both forms converge on the same content-addressed ILI concept and synset-key entities.
    /// </summary>
    private static void EmitMapRow(SubstrateChangeBuilder mb, (string Ili, string OffsetPos, string Version) rec)
    {
        var (ili, offsetPos, version) = rec;
        if (ContentEmitter.Emit(mb, ili, Source) is not { } id) return;
        if (ContentEmitter.Emit(mb, offsetPos, Source) is not { } keyId) return;
        var verCtx = ContentEmitter.Emit(mb, version, Source) ?? id;
        mb.AddAttestation(NativeAttestation.Categorical(
            id, "HAS_SYNSET_KEY", keyId, Source, TC.AcademicCurated, verCtx));
    }

    private static async IAsyncEnumerable<(string Ili, string OffsetPos, string Version)> ParseIliMapAsync(
        string tab, string version, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var line in File.ReadLinesAsync(tab, ct))
        {
            int sep = line.IndexOf('\t');
            if (sep <= 0) continue;
            string ili = line[..sep].Trim();
            // Take ONLY the second column. Some ili-map rows carry a trailing 3rd field; the previous
            // `line[(sep+1)..]` swept it into the synset key (e.g. "01927847-n\t1"), corrupting the
            // content so those keys never converged with the bare "01927847-n" the wordnets emit.
            string rest = line[(sep + 1)..];
            int sep2 = rest.IndexOf('\t');
            string offsetPos = (sep2 >= 0 ? rest[..sep2] : rest).Trim();
            if (ili.Length == 0 || offsetPos.Length == 0 || ili[0] != 'i') continue;
            yield return (ili, offsetPos, version);
        }
    }

    /// <summary>
    /// Parses a turtle-form ILI map line: `&lt;iN&gt; owl:sameAs PREFIX:OFFSET-POS . # gloss`, or the
    /// `ili:iN owl:sameAs PREFIX:OFFSET-POS .` form. Whitespace between the three terms is a tab OR
    /// spaces (the distribution mixes both). Yields the bare ILI ("iN") and the bare synset key
    /// (OFFSET-POS, prefix stripped) so the emission converges with the .tab pass and the wordnets.
    /// </summary>
    private static async IAsyncEnumerable<(string Ili, string OffsetPos, string Version)> ParseIliMapTtlAsync(
        string path, string version, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var raw in File.ReadLinesAsync(path, ct))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '@' || line[0] == '#') continue;

            int sameAs = line.IndexOf("owl:sameAs", StringComparison.Ordinal);
            if (sameAs < 0) continue;

            string subject = line[..sameAs].Trim();
            string ili = NormalizeIli(subject);
            if (ili.Length == 0) continue;

            // object term: from after "owl:sameAs" up to the statement-terminating '.' (or a trailing
            // " # gloss" comment, whichever comes first). It is `PREFIX:OFFSET-POS`.
            string rest = line[(sameAs + "owl:sameAs".Length)..].Trim();
            int hash = rest.IndexOf('#');
            if (hash >= 0) rest = rest[..hash];
            int dot = rest.LastIndexOf('.');
            if (dot >= 0) rest = rest[..dot];
            string objTerm = rest.Trim();
            if (objTerm.Length == 0) continue;

            int prefixColon = objTerm.IndexOf(':');
            string offsetPos = prefixColon >= 0 ? objTerm[(prefixColon + 1)..].Trim() : objTerm;
            if (offsetPos.Length == 0) continue;

            yield return (ili, offsetPos, version);
        }
    }

    /// <summary>Normalizes an ILI subject term (`&lt;i1&gt;` or `ili:i1`) to the bare `i1`.</summary>
    private static string NormalizeIli(string term)
    {
        string s = term;
        if (s.StartsWith("ili:", StringComparison.Ordinal)) s = s["ili:".Length..];
        if (s.Length >= 2 && s[0] == '<' && s[^1] == '>') s = s[1..^1];
        s = s.Trim();
        return s.Length > 0 && s[0] == 'i' && s.Length > 1 && char.IsDigit(s[1]) ? s : string.Empty;
    }

    private static SubstrateChangeBuilder NewBuilder(string label, int bn, int batchSize) =>
        new(Source, $"{label}-{bn}", null,
            entityCapacity: batchSize * 4, physicalityCapacity: batchSize * 4,
            attestationCapacity: batchSize * 4);

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
