using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.UD;

public sealed class UDDecomposer : IDecomposer, IIngestInventoryProvider, IIngestCommitPolicy
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/UDDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    
    
    
    
    public IngestCommitParallelism CommitParallelism => IngestCommitParallelism.StrictSerial;

    private static readonly Hash128 FeatureTypeId  = EntityTypeRegistry.UdFeature;
    private static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;

    private static readonly string[] UposTags =
        ["ADJ","ADP","ADV","AUX","CCONJ","DET","INTJ","NOUN","NUM",
         "PART","PRON","PROPN","PUNCT","SCONJ","SYM","VERB","X"];

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
        int batchSentences = options.BatchSize > 1 ? options.BatchSize : 4096;

        int workers = int.TryParse(
            Environment.GetEnvironmentVariable("LAPLACE_DECOMPOSE_WORKERS"), out var w) && w > 0
            ? w : Math.Clamp(Environment.ProcessorCount - 4, 1, 16);

        var files = ListTreebankFiles(treebanksDir, options);
        if (files.Count == 0) yield break;

        var seenAttRun = new ConcurrentIdSet();

        if (workers <= 1 || files.Count == 1)
        {
            var b = NewBuilder("ud/batch-0", batchSentences);
            var seenEntBatch = new HashSet<Hash128>();
            int sentCount = 0, batchNum = 0;
            foreach (string conllu in files)
            {
                ct.ThrowIfCancellationRequested();
                string langCode = ExtractLangCode(Path.GetFileName(conllu));
                Hash128 langId = LanguageReference.Resolve(langCode);

                await foreach (var sentence in ParseSentencesAsync(conllu, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    EmitSentence(b, sentence, langId, langCode, seenEntBatch, seenAttRun, _canonicalNames);

                    if (++sentCount >= batchSentences)
                    {
                        if (!options.DryRun) yield return b.SetInputUnitsConsumed(sentCount).Build();
                        b = NewBuilder($"ud/batch-{++batchNum}", batchSentences);
                        seenEntBatch.Clear();
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

                    var b = NewBuilder($"ud/w{worker}/{stem}/0", batchSentences);
                    var seenEntBatch = new HashSet<Hash128>();
                    int sentCount = 0, batchNum = 0;
                    await foreach (var sentence in ParseSentencesAsync(conllu, ct))
                    {
                        EmitSentence(b, sentence, langId, langCode, seenEntBatch, seenAttRun, _canonicalNames);
                        if (++sentCount >= batchSentences)
                        {
                            if (!options.DryRun) await channel.Writer.WriteAsync(b.SetInputUnitsConsumed(sentCount).Build(), ct);
                            b = NewBuilder($"ud/w{worker}/{stem}/{++batchNum}", batchSentences);
                            seenEntBatch.Clear();
                            sentCount = 0;
                        }
                    }
                    if (sentCount > 0 && !options.DryRun)
                        await channel.Writer.WriteAsync(b.SetInputUnitsConsumed(sentCount).Build(), ct);
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
                                     HashSet<Hash128> seenEntBatch, ConcurrentIdSet seenAttRun,
                                     ConcurrentDictionary<string, byte> canonicalNames)
    {
        b.AddEntity(new EntityRow(langId, EntityTier.Vocabulary, LanguageTypeId, Source));
        VocabularyNames.TrackLanguage(canonicalNames, langCode);

        if (s.TextUtf8 is { Length: > 0 })
            EmitUtf8(b, s.TextUtf8, Source);

        var formId = new Hash128?[s.MaxId + 1];
        var refToForm = new Dictionary<string, Hash128>(s.Tokens.Count, StringComparer.Ordinal);
        foreach (var tok in s.Tokens)
        {
            var fid = EmitUtf8(b, tok.FormUtf8, Source);
            if (tok.Id >= 0) formId[tok.Id] = fid;
            if (fid is { } f) refToForm[tok.Ref] = f;
            if (!tok.FormLemmaSame)
                EmitUtf8(b, tok.LemmaUtf8, Source);
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
                b.AddEntity(new EntityRow(xposId, EntityTier.Vocabulary, PosReference.PosTypeId, Source));
                b.AddAttestation(NativeAttestation.Categorical(
                    form, "HAS_XPOS", xposId, Source, langId, TC.AcademicCurated));
            }

            foreach (var feat in tok.Feats)
            {
                if (!RelationTypeRegistry.ParseFeature(feat, out var fName, out var fVal)) continue;
                VocabularyNames.TrackUdFeatureValue(canonicalNames, fName, fVal);
                Hash128 valId = FeatValueId(fName, fVal);
                b.AddEntity(new EntityRow(valId, EntityTier.Vocabulary, FeatureTypeId, Source));
                RelationTypeRegistry.SeedDynamic(b, RelationTypeRegistry.ResolveFeature(fName), Source, seenEntBatch, seenAttRun, canonicalNames);
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
                RelationTypeRegistry.SeedDeprel(b, tok.Deprel, Source, seenEntBatch, seenAttRun, canonicalNames);
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
                    RelationTypeRegistry.SeedEnhancedDeprel(b, erel, Source, seenEntBatch, seenAttRun, canonicalNames);
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
                        var g = ContentWitnessBatch.Emit(b, val, Source);
                        if (g is { } gid)
                            b.AddAttestation(NativeAttestation.Categorical(
                                form, "HAS_DEFINITION", gid, Source, SourceTrust.AcademicCurated));
                    }
                    else if (key.Equals("Translit", StringComparison.OrdinalIgnoreCase))
                    {
                        var t = ContentWitnessBatch.Emit(b, val, Source);
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
            var surfaceId = EmitUtf8(b, mwt.FormUtf8, Source);
            if (surfaceId is null) continue;
            for (int id = mwt.Start; id <= mwt.End && id <= s.MaxId; id++)
                if (formId[id] is { } partId)
                    b.AddAttestation(NativeAttestation.Categorical(
                        surfaceId.Value, "HAS_PART", partId, Source, SourceTrust.AcademicCurated));
        }
    }

    private static Hash128? EmitUtf8(SubstrateChangeBuilder b, ReadOnlySpan<byte> utf8, Hash128 sourceId) =>
        ContentWitnessBatch.TryAppendToBuilder(b, utf8, sourceId, out var id) ? id : null;

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
