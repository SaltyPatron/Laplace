using System.Globalization;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.TrustClass;

namespace Laplace.Decomposers.WordNet;

/// <summary>
/// Emits Princeton WordNet 3.0 synsets, lemmas, and semantic relations into
/// the substrate. Uses two passes over data.{noun,verb,adj,adv} so all
/// synset entities exist before any attestation references them as object_id.
///
/// Pass 1: synset entities + lemma entities (no attestations).
/// Pass 2: IS_SYNONYM_OF, IS_HYPERNYM_OF, IS_ANTONYM_OF, HAS_MERONYM,
///         HAS_HOLONYM, HAS_POS attestations.
/// </summary>
public sealed class WordNetDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/WordNetDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/StandardsDerived/v1");

    // Entity type IDs
    private static readonly Hash128 SynsetTypeId =
        Hash128.OfCanonical("substrate/type/WordNet_Synset/v1");
    private static readonly Hash128 LemmaTypeId =
        Hash128.OfCanonical("substrate/type/WordNet_Lemma/v1");
    private static readonly Hash128 PosTypeId =
        Hash128.OfCanonical("substrate/type/WordNet_POS/v1");

    // Attestation kind IDs (registered in InitializeAsync)
    internal static readonly Hash128 KindIsSynonymOf =
        Hash128.OfCanonical("substrate/kind/IS_SYNONYM_OF/v1");
    internal static readonly Hash128 KindIsHypernymOf =
        Hash128.OfCanonical("substrate/kind/IS_HYPERNYM_OF/v1");
    internal static readonly Hash128 KindIsAntonymOf =
        Hash128.OfCanonical("substrate/kind/IS_ANTONYM_OF/v1");
    internal static readonly Hash128 KindHasMeronym =
        Hash128.OfCanonical("substrate/kind/HAS_MERONYM/v1");
    internal static readonly Hash128 KindHasHolonym =
        Hash128.OfCanonical("substrate/kind/HAS_HOLONYM/v1");
    internal static readonly Hash128 KindHasPos =
        Hash128.OfCanonical("substrate/kind/HAS_POS/v1");

    // POS entities seeded in InitializeAsync
    private static readonly Hash128 PosNounId  = Hash128.OfCanonical("wordnet/pos/n/v1");
    private static readonly Hash128 PosVerbId  = Hash128.OfCanonical("wordnet/pos/v/v1");
    private static readonly Hash128 PosAdjId   = Hash128.OfCanonical("wordnet/pos/a/v1");
    private static readonly Hash128 PosAdvId   = Hash128.OfCanonical("wordnet/pos/r/v1");

    // Total synsets across all 4 POS files
    private const long EstimatedSynsets = 117_700L;

    public Hash128 SourceId    => Source;
    public string  SourceName  => "WordNetDecomposer";
    public int     LayerOrder  => 2;
    public Hash128 TrustClassId => TrustClass;

    private static readonly string[] PosFiles = ["data.noun", "data.verb", "data.adj", "data.adv"];

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("WordNet_Synset");
        boot.AddType("WordNet_Lemma");
        boot.AddType("WordNet_POS");
        boot.AddKind("IS_SYNONYM_OF",  KindValueTier.T4, TC.StandardsDerivedTier2);
        boot.AddKind("IS_HYPERNYM_OF", KindValueTier.T3, TC.StandardsDerivedTier2);
        boot.AddKind("IS_ANTONYM_OF",  KindValueTier.T4, TC.StandardsDerivedTier2);
        boot.AddKind("HAS_MERONYM",    KindValueTier.T5, TC.StandardsDerivedTier2);
        boot.AddKind("HAS_HOLONYM",    KindValueTier.T5, TC.StandardsDerivedTier2);
        boot.AddKind("HAS_POS",        KindValueTier.T4, TC.StandardsDerivedTier2);
        await context.Writer.ApplyAsync(boot.Build(), ct);

        // Seed the 4 POS entities
        var pos = new SubstrateChangeBuilder(
            Source, "bootstrap/wordnet-pos", null,
            entityCapacity: 4, physicalityCapacity: 0, attestationCapacity: 0);
        pos.AddEntity(new EntityRow(PosNounId, 0, PosTypeId, Source));
        pos.AddEntity(new EntityRow(PosVerbId, 0, PosTypeId, Source));
        pos.AddEntity(new EntityRow(PosAdjId,  0, PosTypeId, Source));
        pos.AddEntity(new EntityRow(PosAdvId,  0, PosTypeId, Source));
        await context.Writer.ApplyAsync(pos.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string dictDir = Path.Combine(context.EcosystemPath, "WordNet-3.0", "dict");
        int batch = options.BatchSize > 1 ? options.BatchSize : 2048;

        // Pass 1: entities only — synsets and lemmas
        await foreach (var change in StreamBatchedAsync(
            dictDir, batch, entitiesOnly: true, ct))
        {
            yield return change;
            await Task.Yield();
        }

        // Pass 2: attestations only — all synsets exist now
        await foreach (var change in StreamBatchedAsync(
            dictDir, batch, entitiesOnly: false, ct))
        {
            yield return change;
            await Task.Yield();
        }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(EstimatedSynsets);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async IAsyncEnumerable<SubstrateChange> StreamBatchedAsync(
        string dictDir, int batch, bool entitiesOnly,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string suffix = entitiesOnly ? "/entities" : "/attestations";
        var b = new SubstrateChangeBuilder(
            Source, $"wordnet/batch-0{suffix}", null,
            entityCapacity:      entitiesOnly ? batch * 3 : 0,
            physicalityCapacity: 0,
            attestationCapacity: entitiesOnly ? 0 : batch * 6);
        int count = 0;
        int batchNum = 0;

        foreach (var posFile in PosFiles)
        {
            string filePath = Path.Combine(dictDir, posFile);
            if (!File.Exists(filePath)) continue;
            char pos = PosFromDataFile(posFile);

            await foreach (var syn in ParseFileAsync(filePath, pos, ct))
            {
                ct.ThrowIfCancellationRequested();

                if (entitiesOnly)
                    EmitEntities(b, syn);
                else
                    EmitAttestations(b, syn);

                count++;
                if (count >= batch)
                {
                    yield return RebuildWithName(b, batchNum, suffix);
                    batchNum++;
                    b = new SubstrateChangeBuilder(
                        Source, $"wordnet/batch-{batchNum}{suffix}", null,
                        entityCapacity:      entitiesOnly ? batch * 3 : 0,
                        physicalityCapacity: 0,
                        attestationCapacity: entitiesOnly ? 0 : batch * 6);
                    count = 0;
                }
            }
        }

        if (count > 0)
            yield return RebuildWithName(b, batchNum, suffix);
    }

    private static SubstrateChange RebuildWithName(
        SubstrateChangeBuilder b, int batchNum, string suffix)
    {
        // The builder already has the right name; just call Build()
        _ = (batchNum, suffix); // suppress unused
        return b.Build();
    }

    private static void EmitEntities(SubstrateChangeBuilder b, WnSynset syn)
    {
        b.AddEntity(syn.SynsetId, /*tier*/ 3, SynsetTypeId, Source);
        foreach (var lemma in syn.Lemmas)
            b.AddEntity(LemmaEntityId(lemma), /*tier*/ 2, LemmaTypeId, Source);
    }

    private static void EmitAttestations(SubstrateChangeBuilder b, WnSynset syn)
    {
        Hash128 posId = PosEntityId(syn.Pos);

        foreach (var lemma in syn.Lemmas)
        {
            Hash128 lemmaId = LemmaEntityId(lemma);
            // IS_SYNONYM_OF: lemma → synset
            b.AddAttestation(AttestationFactory.Create(
                lemmaId, KindIsSynonymOf, syn.SynsetId, Source, null,
                KindValueTier.T4, TC.StandardsDerivedTier2));
            // HAS_POS: lemma → POS entity
            b.AddAttestation(AttestationFactory.Create(
                lemmaId, KindHasPos, posId, Source, null,
                KindValueTier.T4, TC.StandardsDerivedTier2));
        }

        foreach (var ptr in syn.Pointers)
        {
            Hash128 tgtId = SourceEntityIdConventions.WordNetSynset(ptr.TargetOffset, ptr.TargetPos);
            switch (ptr.Symbol)
            {
                case "@": // hypernym
                    b.AddAttestation(AttestationFactory.Create(
                        syn.SynsetId, KindIsHypernymOf, tgtId, Source, null,
                        KindValueTier.T3, TC.StandardsDerivedTier2));
                    break;
                case "!": // antonym
                    b.AddAttestation(AttestationFactory.Create(
                        syn.SynsetId, KindIsAntonymOf, tgtId, Source, null,
                        KindValueTier.T4, TC.StandardsDerivedTier2));
                    break;
                case "%m": case "%s": case "%p": // meronyms
                    b.AddAttestation(AttestationFactory.Create(
                        syn.SynsetId, KindHasMeronym, tgtId, Source, null,
                        KindValueTier.T5, TC.StandardsDerivedTier2));
                    break;
                case "#m": case "#s": case "#p": // holonyms
                    b.AddAttestation(AttestationFactory.Create(
                        syn.SynsetId, KindHasHolonym, tgtId, Source, null,
                        KindValueTier.T5, TC.StandardsDerivedTier2));
                    break;
            }
        }
    }

    private static Hash128 LemmaEntityId(string lemma)
        => Hash128.OfCanonical($"word:{lemma.Replace('_', ' ').ToLowerInvariant()}");

    private static Hash128 PosEntityId(char pos) => pos switch
    {
        'n' => PosNounId,
        'v' => PosVerbId,
        'a' or 's' => PosAdjId,
        'r' => PosAdvId,
        _ => PosNounId,
    };

    /// <summary>WordNet data-file POS tag (n/v/a/r), not field-2 ss_type (1–5).</summary>
    private static char PosFromDataFile(string posFile) => posFile switch
    {
        "data.noun" => 'n',
        "data.verb" => 'v',
        "data.adj"  => 'a',
        "data.adv"  => 'r',
        _           => 'n',
    };

    // ── parser ──────────────────────────────────────────────────────────────

    private static async IAsyncEnumerable<WnSynset> ParseFileAsync(
        string path,
        char pos,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (line.Length == 0 || line[0] == ' ') continue; // header

            // Split gloss
            int glossSep = line.IndexOf(" | ", StringComparison.Ordinal);
            string synData = glossSep >= 0 ? line[..glossSep] : line;

            var parts = synData.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 7) continue;
            int idx = 0;
            if (!long.TryParse(parts[idx++], out long offset)) continue;  // field 0: offset
            idx++;                                                          // field 1: lex_filenum (skip)
            idx++;                                                          // field 2: ss_type (skip — not POS)
            if (!int.TryParse(parts[idx++],                                 // field 3: w_cnt (hex)
                    System.Globalization.NumberStyles.HexNumber, null, out int wCnt))
                continue;

            var lemmas = new List<string>(wCnt);
            for (int w = 0; w < wCnt; w++)
            {
                if (idx >= parts.Length) break;
                string word = parts[idx++];
                idx++; // lex_id
                lemmas.Add(word);
            }

            if (idx >= parts.Length) continue;
            if (!int.TryParse(parts[idx++], out int pCnt)) continue;

            var pointers = new List<WnPointer>(pCnt);
            for (int p = 0; p < pCnt; p++)
            {
                if (idx + 3 >= parts.Length) break;
                string sym = parts[idx++];
                if (!long.TryParse(parts[idx++], out long tgtOffset)) { idx += 2; continue; }
                char tgtPos = parts[idx++][0];
                idx++; // src_tgt
                pointers.Add(new WnPointer(sym, tgtOffset, tgtPos));
            }

            Hash128 synId = SourceEntityIdConventions.WordNetSynset(offset, pos);
            yield return new WnSynset(synId, pos, lemmas, pointers);
        }
    }

    private sealed record WnSynset(
        Hash128 SynsetId,
        char Pos,
        List<string> Lemmas,
        List<WnPointer> Pointers);

    private readonly record struct WnPointer(
        string Symbol, long TargetOffset, char TargetPos);
}
