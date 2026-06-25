using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.UD;

public sealed class UDDecomposer : IDecomposer, IIngestInventoryProvider{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/UDDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    
    
    
    
    // Multi-worker production emits independent per-file batches that the runner commits via one
    // Unordered N-consumer lane (forward refs are legal). Relation-type entity + IS_A seeding is
    // re-emitted per batch (content-addressed + ON CONFLICT dedups), so there is no run-wide shared
    // mutable seen-set and therefore no cross-worker seeding race.

    private static readonly Hash128 FeatureTypeId  = EntityTypeRegistry.UdFeature;
    private static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;

    public Hash128 SourceId     => Source;
    public string  SourceName   => "UDDecomposer";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    
    
    
    private readonly ConcurrentDictionary<string, byte> _canonicalNames = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> CanonicalNamesForReadback => new List<string>(_canonicalNames.Keys);

    private static readonly ConcurrentDictionary<(string Name, string Value), Hash128> _featValueIdMemo =
        new();

    private static Hash128 FeatValueId(string name, string value) =>
        _featValueIdMemo.GetOrAdd((name, value), static k =>
            VocabularyNames.UdFeatureValueId(k.Name, k.Value));

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddRelationType("HAS_DEFINITION");
        boot.AddRelationType("TRANSCRIBES_AS");
        boot.AddRelationType("ENHANCED_DEPENDS_ON");
        boot.AddRelationType("HAS_XPOS");
        boot.AddRelationType("HAS_LANGUAGE");
        boot.AddType("UD_Feature");
        await context.Writer.ApplyAsync(boot.Build(), ct);
        foreach (var n in boot.CanonicalNames)
            _canonicalNames.TryAdd(n, 0);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string treebanksDir = Path.Combine(context.EcosystemPath, "ud-treebanks-v2.17");
        if (!Directory.Exists(treebanksDir)) yield break;
        // Each UD sentence expands to ~140 rows (grapheme/word tiers + POS/deprel/feature relations),
        // so the global LAPLACE_INGEST_BATCH (65536, sized for 1-row-per-unit sources) would make a
        // batch of ~9M rows and a builder that pre-allocates *40/*60 of that -> ~9GB across 16 workers.
        // Cap sentences/batch so each batch is ~70k rows and the in-flight builders stay bounded.
        int batchSentences = Math.Clamp(options.BatchSize > 1 ? options.BatchSize : 512, 64, 512);

        // Each producer composes into its OWN per-batch stage and dedup is per-batch (per-stage
        // witness) now that the process-global content bank is deleted -- so concurrent file
        // producers share no mutable state and parallelize safely. Stream treebank files to a
        // threadpool, bounded by the per-batch channel.
        int workers = IngestParallelism.ResolveFileWorkers(coreHeadroom: 4);

        var files = ListTreebankFiles(treebanksDir, options);
        if (files.Count == 0) yield break;

        // Compose-time tier-containment dedup: when a reader is available, buffer each batch, probe
        // its distinct content roots/node-ids via the existing entities_exist_bitmap, and stage only
        // novel subtrees (MerkleDedup.TrunkShortcircuit in the native content emit). reader == null
        // keeps the original one-pass streaming behavior byte-for-byte.
        ISubstrateReader? reader = context.Reader;

        if (workers <= 1 || files.Count == 1)
        {
            if (reader is null)
            {
                var b = NewBuilder("ud/batch-0", batchSentences);
                var seenEntBatch = new HashSet<Hash128>();
                var seenAttBatch = new ConcurrentIdSet();
                int sentCount = 0, batchNum = 0;
                foreach (string conllu in files)
                {
                    ct.ThrowIfCancellationRequested();
                    string langCode = ExtractLangCode(Path.GetFileName(conllu));
                    Hash128 langId = LanguageReference.Resolve(langCode);

                    await foreach (var sentence in ParseSentencesAsync(conllu, ct))
                    {
                        ct.ThrowIfCancellationRequested();
                        EmitSentence(b, sentence, langId, langCode, seenEntBatch, seenAttBatch, _canonicalNames);

                        if (++sentCount >= batchSentences)
                        {
                            if (!options.DryRun) yield return b.SetInputUnitsConsumed(sentCount).Build();
                            b = NewBuilder($"ud/batch-{++batchNum}", batchSentences);
                            seenEntBatch.Clear();
                            seenAttBatch = new ConcurrentIdSet();
                            sentCount = 0;
                            await Task.Yield();
                        }
                    }
                    if (!options.DryRun)
                        yield return PeriodBoundary(Path.GetFileNameWithoutExtension(conllu));
                }
                if (sentCount > 0 && !options.DryRun) yield return b.SetInputUnitsConsumed(sentCount).Build();
                yield break;
            }

            var buffer = new List<(UdSentence, Hash128, string)>(batchSentences);
            int bn = 0;
            foreach (string conllu in files)
            {
                ct.ThrowIfCancellationRequested();
                string langCode = ExtractLangCode(Path.GetFileName(conllu));
                Hash128 langId = LanguageReference.Resolve(langCode);

                await foreach (var sentence in ParseSentencesAsync(conllu, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    buffer.Add((sentence, langId, langCode));
                    if (buffer.Count >= batchSentences)
                    {
                        var change = await BuildBatchAsync(buffer, $"ud/batch-{bn++}", batchSentences, reader, ct);
                        buffer.Clear();
                        if (change is not null && !options.DryRun) yield return change;
                    }
                }
                if (!options.DryRun)
                    yield return PeriodBoundary(Path.GetFileNameWithoutExtension(conllu));
            }
            if (buffer.Count > 0)
            {
                var change = await BuildBatchAsync(buffer, $"ud/batch-{bn++}", batchSentences, reader, ct);
                buffer.Clear();
                if (change is not null && !options.DryRun) yield return change;
            }
            yield break;
        }

        var fileQueue = new System.Collections.Concurrent.ConcurrentQueue<string>(files);
        var channel = System.Threading.Channels.Channel.CreateBounded<SubstrateChange>(
            new System.Threading.Channels.BoundedChannelOptions(workers * 4)
            {
                SingleWriter = false,
                SingleReader = true,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait,
            });

        var producers = new Task[workers];
        for (int wi = 0; wi < workers; wi++)
        {
            int worker = wi;
            producers[wi] = Task.Run(async () =>
            {
                while (fileQueue.TryDequeue(out var conllu))
                {
                    ct.ThrowIfCancellationRequested();
                    string langCode = ExtractLangCode(Path.GetFileName(conllu));
                    Hash128 langId = LanguageReference.Resolve(langCode);
                    string stem = Path.GetFileNameWithoutExtension(conllu);

                    if (reader is null)
                    {
                        var b = NewBuilder($"ud/w{worker}/{stem}/0", batchSentences);
                        var seenEntBatch = new HashSet<Hash128>();
                        var seenAttBatch = new ConcurrentIdSet();
                        int sentCount = 0, batchNum = 0;
                        await foreach (var sentence in ParseSentencesAsync(conllu, ct))
                        {
                            EmitSentence(b, sentence, langId, langCode, seenEntBatch, seenAttBatch, _canonicalNames);
                            if (++sentCount >= batchSentences)
                            {
                                if (!options.DryRun) await channel.Writer.WriteAsync(b.SetInputUnitsConsumed(sentCount).Build(), ct);
                                b = NewBuilder($"ud/w{worker}/{stem}/{++batchNum}", batchSentences);
                                seenEntBatch.Clear();
                                seenAttBatch = new ConcurrentIdSet();
                                sentCount = 0;
                            }
                        }
                        if (sentCount > 0 && !options.DryRun)
                            await channel.Writer.WriteAsync(b.SetInputUnitsConsumed(sentCount).Build(), ct);
                    }
                    else
                    {
                        var buffer = new List<(UdSentence, Hash128, string)>(batchSentences);
                        int batchNum = 0;
                        await foreach (var sentence in ParseSentencesAsync(conllu, ct))
                        {
                            buffer.Add((sentence, langId, langCode));
                            if (buffer.Count >= batchSentences)
                            {
                                var change = await BuildBatchAsync(
                                    buffer, $"ud/w{worker}/{stem}/{batchNum++}", batchSentences, reader, ct);
                                buffer.Clear();
                                if (change is not null && !options.DryRun)
                                    await channel.Writer.WriteAsync(change, ct);
                            }
                        }
                        if (buffer.Count > 0)
                        {
                            var change = await BuildBatchAsync(
                                buffer, $"ud/w{worker}/{stem}/{batchNum++}", batchSentences, reader, ct);
                            buffer.Clear();
                            if (change is not null && !options.DryRun)
                                await channel.Writer.WriteAsync(change, ct);
                        }
                    }
                    if (!options.DryRun)
                        await channel.Writer.WriteAsync(PeriodBoundary(stem), ct);
                }
            }, ct);
        }

        _ = Task.WhenAll(producers).ContinueWith(
            t => channel.Writer.TryComplete(t.Exception),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (var change in channel.Reader.ReadAllAsync(ct))
            yield return change;
        await Task.WhenAll(producers);
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        string treebanksDir = Path.Combine(context.EcosystemPath, "ud-treebanks-v2.17");
        if (!Directory.Exists(treebanksDir))
            return Task.FromResult<IngestInventory?>(null);
        var files = ListTreebankFiles(treebanksDir, options)
            .Select(p =>
            {
                string id = Path.GetFileNameWithoutExtension(p);
                return new IngestFileSpec(id, p, EtlInventory.CountConlluSentences(p));
            })
            .ToList();
        long total = 0;
        foreach (var f in files) total += f.InputUnits;
        return Task.FromResult<IngestInventory?>(new IngestInventory("sentences", total, files));
    }

    public async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var inv = await DescribeInputAsync(context, DecomposerOptions.Default, ct);
        return inv?.TotalInputUnits;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static SubstrateChangeBuilder NewBuilder(string unit, int batchSentences) =>
        new(Source, unit, null,
            entityCapacity:      batchSentences * 40,
            physicalityCapacity: batchSentences * 40,
            attestationCapacity: batchSentences * 60);

    private static SubstrateChange PeriodBoundary(string stem) =>
        new SubstrateChangeBuilder(Source, $"period-boundary/{stem}", null,
            entityCapacity: 0, physicalityCapacity: 0, attestationCapacity: 0).Build();

    private static void EmitSentence(SubstrateChangeBuilder b, UdSentence s, Hash128 langId, string langCode,
                                     HashSet<Hash128> seenEntBatch, ConcurrentIdSet seenAttBatch,
                                     ConcurrentDictionary<string, byte> canonicalNames,
                                     ContentBatch? cb = null)
    {
        b.AddEntity(new EntityRow(langId, EntityTier.Vocabulary, LanguageTypeId, Source));
        VocabularyNames.TrackLanguage(canonicalNames, langCode);

        if (s.TextUtf8 is { Length: > 0 })
            EmitUtf8(b, s.TextUtf8, Source, cb);

        var formId = new Hash128?[s.MaxId + 1];
        var refToForm = new Dictionary<string, Hash128>(s.Tokens.Count, StringComparer.Ordinal);
        foreach (var tok in s.Tokens)
        {
            var fid = EmitUtf8(b, tok.FormUtf8, Source, cb);
            if (tok.Id >= 0) formId[tok.Id] = fid;
            if (fid is { } f) refToForm[tok.Ref] = f;
            if (!tok.FormLemmaSame)
                EmitUtf8(b, tok.LemmaUtf8, Source, cb);
        }

        foreach (var tok in s.Tokens)
        {
            if (!refToForm.TryGetValue(tok.Ref, out var form)) continue;

            if (!string.IsNullOrEmpty(tok.Upos) && tok.Upos != "_")
                PosReference.Attest(b, form, tok.Upos!, PosReference.PosTagset.Upos,
                    Source, null, SourceTrust.AcademicCurated, canonicalNames);

            // XPOS: treebank-specific POS tag (Penn Treebank etc.), distinct from universal UPOS.
            // Namespaced so the tag "NN" doesn't collide with the word "NN"; emitted under HAS_XPOS,
            // scoped to the language as context. (Previously parsed but never emitted.)
            if (!string.IsNullOrEmpty(tok.Xpos) && tok.Xpos != "_")
            {
                string xposName = $"substrate/pos/xpos/{tok.Xpos}/v1";
                Hash128 xposId = Hash128.OfCanonical(xposName);
                canonicalNames.TryAdd(xposName, 0);
                // Substrate-native name (once per batch) so the XPOS tag is legible/walkable, not just a
                // canonical_names code-table entry.
                VocabularyAnchor.Emit(b, xposId, PosReference.PosTypeId, tok.Xpos, Source,
                    TC.AcademicCurated, seenEntBatch);
                b.AddAttestation(NativeAttestation.Categorical(
                    form, "HAS_XPOS", xposId, Source, langId, TC.AcademicCurated));
            }

            foreach (var feat in tok.Feats)
            {
                if (!RelationTypeRegistry.ParseFeature(feat, out var fName, out var fVal)) continue;
                VocabularyNames.TrackUdFeatureValue(canonicalNames, fName, fVal);
                Hash128 valId = FeatValueId(fName, fVal);
                // Substrate-native name (once per batch) so the feature value (e.g. "Number=Sing") is
                // legible/walkable instead of a path-hash island named only by the code-table.
                VocabularyAnchor.Emit(b, valId, FeatureTypeId, $"{fName}={fVal}", Source,
                    SourceTrust.AcademicCurated, seenEntBatch);
                RelationTypeRegistry.SeedDynamic(b, RelationTypeRegistry.ResolveFeature(fName), Source, seenEntBatch, seenAttBatch, canonicalNames);
                var featRel = RelationTypeRegistry.ResolveFeature(fName);
                b.AddAttestation(NativeAttestation.CategoricalResolved(
                    form, featRel.Id, valId, Source, null, featRel.Rank * SourceTrust.AcademicCurated));
            }

            b.AddAttestation(NativeAttestation.Categorical(
                form, "HAS_LANGUAGE", langId, Source, null, SourceTrust.AcademicCurated));

            if (!tok.FormLemmaSame)
            {
                var lemmaId = ContentWitnessBatch.RootId(tok.LemmaUtf8);
                if (lemmaId is not null)
                    b.AddAttestation(NativeAttestation.Categorical(
                        lemmaId.Value, "IS_LEMMA_OF", form, Source, SourceTrust.AcademicCurated));
            }

            if (tok.Head > 0 && tok.Head <= s.MaxId && formId[tok.Head] is { } headId
                && !string.IsNullOrEmpty(tok.Deprel) && tok.Deprel != "_")
            {
                RelationTypeRegistry.SeedDeprel(b, tok.Deprel, Source, seenEntBatch, seenAttBatch, canonicalNames);
                var dep = RelationTypeRegistry.ResolveDeprel(tok.Deprel);
                b.AddAttestation(NativeAttestation.CategoricalResolved(
                    form, dep.Id, headId, Source, null, dep.Rank * SourceTrust.AcademicCurated));
            }

            if (tok.Deps.Length > 0 && tok.Deps != "_")
            {
                foreach (var edge in tok.Deps.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    int colon = edge.IndexOf(':');
                    if (colon <= 0) continue;
                    string headRef = edge[..colon];
                    string erel = edge[(colon + 1)..].Trim();
                    if (erel.Length == 0 || headRef == "0") continue;
                    if (!refToForm.TryGetValue(headRef, out var eHead)) continue;
                    RelationTypeRegistry.SeedEnhancedDeprel(b, erel, Source, seenEntBatch, seenAttBatch, canonicalNames);
                    var edep = RelationTypeRegistry.ResolveEnhancedDeprel(erel);
                    b.AddAttestation(NativeAttestation.CategoricalResolved(
                        form, edep.Id, eHead, Source, null, edep.Rank * SourceTrust.AcademicCurated));
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
                        var g = EmitUtf8(b, System.Text.Encoding.UTF8.GetBytes(val), Source, cb);
                        if (g is { } gid)
                            b.AddAttestation(NativeAttestation.Categorical(
                                form, "HAS_DEFINITION", gid, Source, SourceTrust.AcademicCurated));
                    }
                    else if (key.Equals("Translit", StringComparison.OrdinalIgnoreCase))
                    {
                        var t = EmitUtf8(b, System.Text.Encoding.UTF8.GetBytes(val), Source, cb);
                        if (t is { } tid)
                            b.AddAttestation(NativeAttestation.Categorical(
                                form, "TRANSCRIBES_AS", tid, Source, SourceTrust.AcademicCurated));
                    }
                    else if (key.Equals("Lang", StringComparison.OrdinalIgnoreCase))
                    {
                        // Token-level MISC Lang= marks code-switching (a foreign-language token inside an
                        // otherwise monolingual sentence) — distinct from the file-level HAS_LANGUAGE.
                        Hash128 miscLangId = LanguageReference.Resolve(val);
                        b.AddAttestation(NativeAttestation.Categorical(
                            form, "HAS_LANGUAGE", miscLangId, Source, SourceTrust.AcademicCurated));
                    }
                }
            }
        }

        foreach (var mwt in s.Mwts)
        {
            var surfaceId = EmitUtf8(b, mwt.FormUtf8, Source, cb);
            if (surfaceId is null) continue;
            for (int id = mwt.Start; id <= mwt.End && id <= s.MaxId; id++)
                if (formId[id] is { } partId)
                    b.AddAttestation(NativeAttestation.Categorical(
                        surfaceId.Value, "HAS_PART", partId, Source, SourceTrust.AcademicCurated));
        }
    }

    /// <summary>
    /// Emit one content text. With no <see cref="ContentBatch"/> (reader unavailable) this is the
    /// original one-pass native content-witness add. Under a batch it routes through the two-phase
    /// tier-containment path: in the collect pass it builds (once) the content tier tree and returns
    /// its root id without staging; in the emit pass it stages only the novel subtrees (the per-tree
    /// existing-bitmap drives MerkleDedup.TrunkShortcircuit inside the native content emit).
    /// </summary>
    private static Hash128? EmitUtf8(
        SubstrateChangeBuilder b, ReadOnlySpan<byte> utf8, Hash128 sourceId, ContentBatch? cb)
    {
        if (utf8.IsEmpty) return null;
        if (cb is null)
            return ContentWitnessBatch.TryAppendToBuilder(b, utf8, sourceId, out var id) ? id : null;

        var key = Hash128.Blake3(utf8);
        if (cb.Collecting)
        {
            if (!cb.Map.TryGetValue(key, out var entry))
            {
                entry = (ContentWitnessBatch.BuildTree(utf8), null);
                cb.Map[key] = entry;
            }
            if (entry.Tree is null) return null;
            return entry.Tree.GetNode(entry.Tree.NaturalUnitIndex()).Id;
        }

        if (!cb.Map.TryGetValue(key, out var e) || e.Tree is null) return null;
        return ContentWitnessBatch.TryEmitTree(
            b, e.Tree, sourceId, e.Bitmap ?? ReadOnlySpan<byte>.Empty, out var rid) ? rid : null;
    }

    /// <summary>
    /// Per-batch content tier-tree cache for the two-phase containment path: collect builds one tree
    /// per distinct content text (deduped by Blake3 of the canonical bytes), the probe fills each
    /// tree's existing-bitmap from <c>entities_exist_bitmap</c>, and emit stages only novel subtrees.
    /// Trees are native handles, freed on <see cref="Dispose"/>.
    /// </summary>
    private sealed class ContentBatch : IDisposable
    {
        public bool Collecting;
        public readonly Dictionary<Hash128, (TierTree? Tree, byte[]? Bitmap)> Map = new();

        public void Dispose()
        {
            foreach (var e in Map.Values) e.Tree?.Dispose();
            Map.Clear();
        }
    }

    
    
    
    
    private async Task<SubstrateChange?> BuildBatchAsync(
        List<(UdSentence S, Hash128 LangId, string LangCode)> batch, string unit,
        int batchSentences, ISubstrateReader reader, CancellationToken ct)
    {
        if (batch.Count == 0) return null;
        using var cb = new ContentBatch { Collecting = true };

        // Pass A — collect: build each distinct content text's tier tree once (no staging). The
        // collect builder is purely managed (collect-mode EmitUtf8 never touches the native content
        // stage), so it allocates no intent stage and is simply dropped.
        var collectBuilder = NewBuilder(unit + "/collect", batchSentences);
        var collectEnt = new HashSet<Hash128>();
        var collectAtt = new ConcurrentIdSet();
        foreach (var (s, langId, langCode) in batch)
            EmitSentence(collectBuilder, s, langId, langCode, collectEnt, collectAtt, _canonicalNames, cb);

        await ProbeContentRootsAsync(cb, reader, ct);

        // Pass B — emit: stage only novel subtrees (present trunks skipped) into the real builder.
        cb.Collecting = false;
        var b = NewBuilder(unit, batchSentences);
        var seenEntBatch = new HashSet<Hash128>();
        var seenAttBatch = new ConcurrentIdSet();
        foreach (var (s, langId, langCode) in batch)
            EmitSentence(b, s, langId, langCode, seenEntBatch, seenAttBatch, _canonicalNames, cb);

        return b.SetInputUnitsConsumed(batch.Count).Build();
    }

    
    
    
    private static async Task ProbeContentRootsAsync(
        ContentBatch cb, ISubstrateReader reader, CancellationToken ct)
    {
        var keys = new List<Hash128>(cb.Map.Count);
        var perTree = new List<Hash128[]>(cb.Map.Count);
        int total = 0;
        foreach (var kv in cb.Map)
        {
            if (kv.Value.Tree is null) continue;
            var ids = kv.Value.Tree.NodeIds();
            if (ids.Length == 0) continue;
            keys.Add(kv.Key);
            perTree.Add(ids);
            total += ids.Length;
        }
        if (total == 0) return;

        var candidates = new Hash128[total];
        int off = 0;
        foreach (var ids in perTree)
        {
            Array.Copy(ids, 0, candidates, off, ids.Length);
            off += ids.Length;
        }

        byte[] combined = await reader.EntitiesExistBitmapAsync(candidates, ct);
        long combinedBits = (long)combined.Length * 8;

        int g = 0;
        for (int i = 0; i < keys.Count; i++)
        {
            int n = perTree[i].Length;
            var bm = new byte[(n + 7) / 8];
            for (int j = 0; j < n; j++)
            {
                int gi = g + j;
                if (gi < combinedBits && (combined[gi >> 3] & (1 << (gi & 7))) != 0)
                    bm[j >> 3] |= (byte)(1 << (j & 7));
            }
            var e = cb.Map[keys[i]];
            cb.Map[keys[i]] = (e.Tree, bm);
            g += n;
        }
    }

    private static byte[] CopyUtf8Field(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty) return Array.Empty<byte>();
        return span.ToArray();
    }

    private static LanguageFilter? EffectiveLanguages(DecomposerOptions options) =>
        options.Languages is { IsActive: true } ? options.Languages
        : LanguageFilter.ForSource("UDDecomposer");

    private static List<string> ListTreebankFiles(string treebanksDir, DecomposerOptions options)
    {
        var all = Directory.EnumerateFiles(treebanksDir, "*.conllu", SearchOption.AllDirectories).ToList();
        var langs = EffectiveLanguages(options);
        if (langs is { IsActive: true })
            return all.Where(p => langs.MatchesUdTreebankFile(Path.GetFileName(p))).ToList();
        // No filter = ingest every treebank (all languages). Treebanks are just data; full
        // multilingual ingest is the intended omniglottal path, not an accident to guard against.
        Console.Error.WriteLine($"UD: no language filter — ingesting all {all.Count} treebank files (multilingual).");
        return all;
    }

    private static string ExtractLangCode(string fileName)
    {
        int under = fileName.IndexOf('_');
        return under > 0 ? fileName[..under] : "und";
    }

    private static async IAsyncEnumerable<UdSentence> ParseSentencesAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var tokens = new List<UdToken>(48);
        var mwts = new List<UdMwt>(4);
        byte[]? textUtf8 = null;
        int maxId = 0;

        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            ReadOnlySpan<byte> line = lineMem.Span;
            if (line.IsEmpty)
            {
                if (tokens.Count > 0)
                    yield return new UdSentence(textUtf8, tokens.ToList(), mwts.ToList(), maxId);
                tokens.Clear(); mwts.Clear(); textUtf8 = null; maxId = 0;
                continue;
            }
            if (line[0] == (byte)'#')
            {
                int eq = line.IndexOf((byte)'=');
                if (eq > 0 && line[..eq].Trim((byte)' ').SequenceEqual("# text"u8))
                {
                    var raw = line[(eq + 1)..].Trim((byte)' ');
                    textUtf8 = raw.IsEmpty ? null : CopyUtf8Field(raw);
                }
                continue;
            }

            if (!TsvSpan.TryField(line, 0, out var id0Span)) continue;
            string id0 = System.Text.Encoding.UTF8.GetString(id0Span);

            if (id0.Contains('-'))
            {
                int dash = id0.IndexOf('-');
                if (int.TryParse(id0[..dash], out int st) && int.TryParse(id0[(dash + 1)..], out int en)
                    && TsvSpan.TryField(line, 1, out var mwtForm))
                    mwts.Add(new UdMwt(st, en, CopyUtf8Field(mwtForm.Trim((byte)' '))));
                continue;
            }
            bool isEmptyNode = id0.Contains('.');
            int id = 0;
            if (!isEmptyNode && !int.TryParse(id0, out id)) continue;

            if (!TsvSpan.TryField(line, 1, out var formSpan)) continue;
            formSpan = formSpan.Trim((byte)' ');
            if (formSpan.IsEmpty || formSpan.SequenceEqual("_"u8)) continue;
            var formUtf8 = CopyUtf8Field(formSpan);
            ReadOnlySpan<byte> lemmaSpan = TsvSpan.TryField(line, 2, out var ls) ? ls.Trim((byte)' ') : formSpan;
            if (lemmaSpan.IsEmpty || lemmaSpan.SequenceEqual("_"u8)) lemmaSpan = formSpan;
            var lemmaUtf8 = lemmaSpan.SequenceEqual(formSpan) ? formUtf8 : CopyUtf8Field(lemmaSpan);
            bool formLemmaSame = ReferenceEquals(formUtf8, lemmaUtf8)
                                 || formUtf8.AsSpan().SequenceEqual(lemmaUtf8);
            string upos = TsvSpan.TryField(line, 3, out var uposSpan)
                ? System.Text.Encoding.UTF8.GetString(uposSpan).Trim() : "";
            string xpos = TsvSpan.TryField(line, 4, out var xposSpan)
                ? System.Text.Encoding.UTF8.GetString(xposSpan).Trim() : "";
            string[] feats = TsvSpan.TryField(line, 5, out var featSpan) && !featSpan.SequenceEqual("_"u8)
                ? System.Text.Encoding.UTF8.GetString(featSpan).Split('|', StringSplitOptions.RemoveEmptyEntries)
                : System.Array.Empty<string>();
            int head = TsvSpan.TryField(line, 6, out var headSpan)
                && int.TryParse(System.Text.Encoding.UTF8.GetString(headSpan), out int h) ? h : 0;
            string deprel = TsvSpan.TryField(line, 7, out var depSpan)
                ? System.Text.Encoding.UTF8.GetString(depSpan).Trim() : "";
            string deps = TsvSpan.TryField(line, 8, out var depsSpan)
                ? System.Text.Encoding.UTF8.GetString(depsSpan).Trim() : "_";
            string misc = TsvSpan.TryField(line, 9, out var miscSpan)
                ? System.Text.Encoding.UTF8.GetString(miscSpan).Trim() : "_";

            if (!isEmptyNode && id > maxId) maxId = id;
            tokens.Add(new UdToken(isEmptyNode ? -1 : id, id0, formUtf8, lemmaUtf8, formLemmaSame,
                upos, xpos, feats, head, deprel, deps, misc));
        }
        if (tokens.Count > 0)
            yield return new UdSentence(textUtf8, tokens.ToList(), mwts.ToList(), maxId);
    }

    private sealed record UdSentence(byte[]? TextUtf8, List<UdToken> Tokens, List<UdMwt> Mwts, int MaxId);

    private readonly record struct UdToken(
        int Id, string Ref, byte[] FormUtf8, byte[] LemmaUtf8, bool FormLemmaSame,
        string Upos, string Xpos, string[] Feats,
        int Head, string Deprel, string Deps, string Misc);

    private readonly record struct UdMwt(int Start, int End, byte[] FormUtf8);
}
