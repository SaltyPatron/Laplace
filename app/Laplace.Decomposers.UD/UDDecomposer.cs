using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.UD;

public sealed class UDDecomposer : IDecomposer, IIngestInventoryProvider
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/UDDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    private static readonly Hash128 XposTypeId     = EntityTypeRegistry.UdXpos;
    private static readonly Hash128 FeatureTypeId  = EntityTypeRegistry.UdFeature;
    private static readonly Hash128 LanguageTypeId = EntityTypeRegistry.Language;

    private static readonly string[] UposTags =
        ["ADJ","ADP","ADV","AUX","CCONJ","DET","INTJ","NOUN","NUM",
         "PART","PRON","PROPN","PUNCT","SCONJ","SYM","VERB","X"];

    public Hash128 SourceId     => Source;
    public string  SourceName   => "UDDecomposer";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    private static readonly ConcurrentDictionary<(string Lang, string Tag), Hash128> _xposIdMemo =
        new();
    private static readonly ConcurrentDictionary<(string Name, string Value), Hash128> _featValueIdMemo =
        new();

    private static Hash128 UposId(string t) => PosReference.Resolve(t, PosReference.PosTagset.Upos);
    private static Hash128 XposId(string lang, string t) =>
        _xposIdMemo.GetOrAdd((lang, t), static k => Hash128.OfCanonical($"xpos:{k.Lang}:{k.Tag}"));
    private static Hash128 FeatValueId(string name, string value) =>
        _featValueIdMemo.GetOrAdd((name, value), static k => Hash128.OfCanonical($"featval:{k.Name}:{k.Value}"));

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("UD_XPOS");
        boot.AddRelationType("HAS_DEFINITION");
        boot.AddRelationType("TRANSCRIBES_AS");
        boot.AddRelationType("ENHANCED_DEPENDS_ON");
        boot.AddType("UD_Feature");
        await context.Writer.ApplyAsync(boot.Build(), ct);

        var upos = new SubstrateChangeBuilder(
            Source, "bootstrap/ud-upos", null,
            entityCapacity: PosReference.Canonical.Length + 1, physicalityCapacity: 0, attestationCapacity: 0);
        PosReference.SeedCanonical(upos, Source);
        await context.Writer.ApplyAsync(upos.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string treebanksDir = Path.Combine(context.EcosystemPath, "ud-treebanks-v2.17");
        if (!Directory.Exists(treebanksDir)) yield break;
        int batchSentences = options.BatchSize > 1 ? options.BatchSize : 256;

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
                    EmitSentence(b, sentence, langId, langCode, seenEntBatch, seenAttRun);

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
                        EmitSentence(b, sentence, langId, langCode, seenEntBatch, seenAttRun);
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
                                     HashSet<Hash128> seenEntBatch, ConcurrentIdSet seenAttRun)
    {
        b.AddEntity(new EntityRow(langId, EntityTier.Vocabulary, LanguageTypeId, Source));

        if (!string.IsNullOrEmpty(s.Text)) ContentEmitter.Emit(b, s.Text!, Source);

        var formId = new Hash128?[s.MaxId + 1];
        var refToForm = new Dictionary<string, Hash128>(s.Tokens.Count, StringComparer.Ordinal);
        foreach (var tok in s.Tokens)
        {
            var fid = ContentEmitter.Emit(b, tok.Form, Source);
            if (tok.Id >= 0) formId[tok.Id] = fid;
            if (fid is { } f) refToForm[tok.Ref] = f;
            if (tok.Lemma != tok.Form) ContentEmitter.Emit(b, tok.Lemma, Source);
        }

        foreach (var tok in s.Tokens)
        {
            if (!refToForm.TryGetValue(tok.Ref, out var form)) continue;

            if (!string.IsNullOrEmpty(tok.Upos) && tok.Upos != "_")
                b.AddAttestation(RelationTypeRegistry.Attest(
                    form, "HAS_UPOS", UposId(tok.Upos), Source, SourceTrust.AcademicCurated));

            if (!string.IsNullOrEmpty(tok.Xpos) && tok.Xpos != "_")
            {
                b.AddEntity(new EntityRow(XposId(langCode, tok.Xpos), EntityTier.Vocabulary, XposTypeId, Source));
                b.AddAttestation(RelationTypeRegistry.Attest(
                    form, "HAS_XPOS", XposId(langCode, tok.Xpos), Source, SourceTrust.AcademicCurated));
            }

            foreach (var feat in tok.Feats)
            {
                if (!RelationTypeRegistry.ParseFeature(feat, out var fName, out var fVal)) continue;
                Hash128 valId = FeatValueId(fName, fVal);
                b.AddEntity(new EntityRow(valId, EntityTier.Vocabulary, FeatureTypeId, Source));
                RelationTypeRegistry.SeedDynamic(b, RelationTypeRegistry.ResolveFeature(fName), Source, seenEntBatch, seenAttRun);
                b.AddAttestation(RelationTypeRegistry.AttestFeature(
                    form, fName, valId, Source, SourceTrust.AcademicCurated));
            }

            b.AddAttestation(RelationTypeRegistry.Attest(
                form, "HAS_LANGUAGE", langId, Source, SourceTrust.AcademicCurated));

            if (tok.Lemma != tok.Form)
            {
                var lemmaId = ContentEmitter.RootId(tok.Lemma);
                if (lemmaId is not null)
                    b.AddAttestation(RelationTypeRegistry.Attest(
                        lemmaId.Value, "IS_LEMMA_OF", form, Source, SourceTrust.AcademicCurated));
            }

            if (tok.Head > 0 && tok.Head <= s.MaxId && formId[tok.Head] is { } headId
                && !string.IsNullOrEmpty(tok.Deprel) && tok.Deprel != "_")
            {
                RelationTypeRegistry.SeedDeprel(b, tok.Deprel, Source, seenEntBatch, seenAttRun);
                b.AddAttestation(RelationTypeRegistry.AttestDeprel(
                    form, tok.Deprel, headId, Source, SourceTrust.AcademicCurated));
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
                    RelationTypeRegistry.SeedEnhancedDeprel(b, erel, Source, seenEntBatch, seenAttRun);
                    b.AddAttestation(RelationTypeRegistry.AttestEnhancedDeprel(
                        form, erel, eHead, Source, SourceTrust.AcademicCurated));
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
                        var g = ContentEmitter.Emit(b, val, Source);
                        if (g is { } gid)
                            b.AddAttestation(RelationTypeRegistry.Attest(
                                form, "HAS_DEFINITION", gid, Source, SourceTrust.AcademicCurated));
                    }
                    else if (key.Equals("Translit", StringComparison.OrdinalIgnoreCase))
                    {
                        var t = ContentEmitter.Emit(b, val, Source);
                        if (t is { } tid)
                            b.AddAttestation(RelationTypeRegistry.Attest(
                                form, "TRANSCRIBES_AS", tid, Source, SourceTrust.AcademicCurated));
                    }
                }
            }
        }

        foreach (var mwt in s.Mwts)
        {
            var surfaceId = ContentEmitter.Emit(b, mwt.Form, Source);
            if (surfaceId is null) continue;
            for (int id = mwt.Start; id <= mwt.End && id <= s.MaxId; id++)
                if (formId[id] is { } partId)
                    b.AddAttestation(RelationTypeRegistry.Attest(
                        surfaceId.Value, "HAS_PART", partId, Source, SourceTrust.AcademicCurated));
        }
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
        if (all.Count > 50)
            throw new InvalidOperationException(
                $"UD ingest found {all.Count} treebank files with no language filter active. "
                + "Set LAPLACE_INGEST_LANGS=en (or LAPLACE_UD_LANGS) or pass --langs en to scope to en_* dialects.");
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
        string? text = null;
        int maxId = 0;

        await foreach (var lineMem in StreamingUtf8LineReader.ReadLinesAsync(path, ct))
        {
            ReadOnlySpan<byte> line = lineMem.Span;
            if (line.IsEmpty)
            {
                if (tokens.Count > 0)
                    yield return new UdSentence(text, tokens.ToList(), mwts.ToList(), maxId);
                tokens.Clear(); mwts.Clear(); text = null; maxId = 0;
                continue;
            }
            if (line[0] == (byte)'#')
            {
                int eq = line.IndexOf((byte)'=');
                if (eq > 0 && line[..eq].Trim((byte)' ').SequenceEqual("# text"u8))
                    text = System.Text.Encoding.UTF8.GetString(line[(eq + 1)..]).Trim();
                continue;
            }

            if (!TsvSpan.TryField(line, 0, out var id0Span)) continue;
            string id0 = System.Text.Encoding.UTF8.GetString(id0Span);

            if (id0.Contains('-'))
            {
                int dash = id0.IndexOf('-');
                if (int.TryParse(id0[..dash], out int st) && int.TryParse(id0[(dash + 1)..], out int en))
                    mwts.Add(new UdMwt(st, en, TsvSpan.TryField(line, 1, out var mwtForm)
                        ? System.Text.Encoding.UTF8.GetString(mwtForm).Trim() : ""));
                continue;
            }
            bool isEmptyNode = id0.Contains('.');
            int id = 0;
            if (!isEmptyNode && !int.TryParse(id0, out id)) continue;

            if (!TsvSpan.TryField(line, 1, out var formSpan)) continue;
            string form = System.Text.Encoding.UTF8.GetString(formSpan).Trim();
            if (form.Length == 0 || form == "_") continue;
            string lemma = TsvSpan.TryField(line, 2, out var lemmaSpan)
                ? System.Text.Encoding.UTF8.GetString(lemmaSpan).Trim() : form;
            if (lemma.Length == 0 || lemma == "_") lemma = form;
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
            tokens.Add(new UdToken(isEmptyNode ? -1 : id, id0, form, lemma, upos, xpos, feats, head, deprel, deps, misc));
        }
        if (tokens.Count > 0)
            yield return new UdSentence(text, tokens.ToList(), mwts.ToList(), maxId);
    }

    private sealed record UdSentence(string? Text, List<UdToken> Tokens, List<UdMwt> Mwts, int MaxId);

    private readonly record struct UdToken(
        int Id, string Ref, string Form, string Lemma, string Upos, string Xpos, string[] Feats,
        int Head, string Deprel, string Deps, string Misc);

    private readonly record struct UdMwt(int Start, int End, string Form);
}
