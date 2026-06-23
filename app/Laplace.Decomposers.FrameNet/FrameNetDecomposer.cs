using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

[assembly: InternalsVisibleTo("Laplace.Decomposers.FrameNet.Tests")]

namespace Laplace.Decomposers.FrameNet;

public sealed class FrameNetDecomposer : IDecomposer{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/FrameNetDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    private static readonly Hash128 FrameTypeId    = EntityTypeRegistry.FrameNetFrame;
    private static readonly Hash128 FeTypeId       = EntityTypeRegistry.FrameNetFe;
    private static readonly Hash128 CorenessTypeId = EntityTypeRegistry.FrameNetCoreness;

    // SCHEMA, not content: the four FrameNet coreness levels (Core / Peripheral /
    // Extra-Thematic / Core-Unexpressed) are a small, fixed, closed enum used only as the
    // contextId qualifier on HAS_FRAME_ELEMENT edges. They are app/meta Vocabulary with no
    // geometry by design, so Hash128.OfCanonical (not ContentEmitter) is correct here.
    private static Hash128 CorenessId(string coreType) =>
        Hash128.OfCanonical($"framenet/coreness/{coreType}");

    private static readonly ConcurrentDictionary<string, byte> _vocabularyNames = new(StringComparer.Ordinal);
    internal static ConcurrentDictionary<string, byte> VocabularyNames => _vocabularyNames;

    private static readonly string[] CorenessValues =
        ["Core", "Peripheral", "Extra-Thematic", "Core-Unexpressed"];

    private static readonly Dictionary<string, string> RelationTypes = new(StringComparer.Ordinal)
    {
        ["Inherits from"]   = "INHERITS_FROM",
        ["Uses"]            = "FRAME_USES",
        ["Perspective on"]  = "PERSPECTIVE_ON",
        ["Subframe of"]     = "HAS_SUBEVENT",
        ["Is Causative of"] = "CAUSATIVE_OF",
        ["Is Inchoative of"] = "INCHOATIVE_OF",
        ["Precedes"]        = "PRECEDES",
        ["See also"]        = "ALSO_SEE",
    };

    private const string Ns = "http://framenet.icsi.berkeley.edu";

    public Hash128 SourceId     => Source;
    public string  SourceName   => "FrameNetDecomposer";
    public int     LayerOrder   => 3;
    public Hash128 TrustClassId => TrustClass;

    
    
    
    
    
    
    // Each frame/LU/fulltext batch is a self-contained builder; the only out-of-batch (and
    // out-of-source) references are name-anchor edges (TargetFrame, FE Requires/Excludes,
    // EVOKES_FRAME) that resolve by content-addressed id wherever the referent lands. The
    // per-batch referential EXISTS pre-check is gone, so forward/cross-batch anchors are legal
    // and N workers can commit concurrently.

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("FrameNet_Frame");
        boot.AddType("FrameNet_FE");
        boot.AddType("FrameNet_LU");
        boot.AddType("FrameNet_Coreness");

        boot.AddRelationType("EVOKES_FRAME");
        boot.AddRelationType("HAS_FRAME_ELEMENT");
        boot.AddRelationType("REQUIRES");
        boot.AddRelationType("EXCLUDES");
        boot.AddRelationType("HAS_VALENCE_PATTERN");
        boot.AddRelationType("HAS_DEFINITION");
        boot.AddRelationType("HAS_POS");
        boot.AddRelationType("HAS_EXAMPLE");
        boot.AddRelationType("FRAME_USES");
        boot.AddRelationType("PERSPECTIVE_ON");
        boot.AddRelationType("INHERITS_FROM");
        boot.AddRelationType("CAUSATIVE_OF");
        boot.AddRelationType("INCHOATIVE_OF");
        boot.AddRelationType("PRECEDES");
        boot.AddRelationType("ALSO_SEE");
        boot.AddRelationType("IS_A");
        boot.AddRelationType("HAS_SUBEVENT");
        boot.AddRelationType("RELATED_TO");

        await context.Writer.ApplyAsync(boot.Build(), ct);
        foreach (var n in boot.CanonicalNames)
            _vocabularyNames.TryAdd(n, 0);

        var seed = new SubstrateChangeBuilder(
            Source, "bootstrap/framenet-vocab", null,
            entityCapacity: CorenessValues.Length + 1,
            physicalityCapacity: 0, attestationCapacity: 0);
        seed.AddEntity(new EntityRow(CorenessTypeId, EntityTier.Vocabulary,
            BootstrapIntentBuilder.TypeMetaTypeId, Source));
        foreach (var c in CorenessValues)
            seed.AddEntity(new EntityRow(CorenessId(c), EntityTier.Vocabulary, CorenessTypeId, Source));
        await context.Writer.ApplyAsync(seed.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string frameDir    = Path.Combine(context.EcosystemPath, "frame");
        string fulltextDir = Path.Combine(context.EcosystemPath, "fulltext");
        int batch = options.BatchSize > 1 ? options.BatchSize : 4096;
        var reader = context.Reader;

        await foreach (var change in StreamFramesAsync(frameDir, batch, reader, ct))
        { if (!options.DryRun) yield return change; await Task.Yield(); }

        string luDir = Path.Combine(context.EcosystemPath, "lu");
        await foreach (var change in FrameNetLuIngest.StreamLuAsync(luDir, batch, Source, reader, ct))
        { if (!options.DryRun) yield return change; await Task.Yield(); }

        await foreach (var change in StreamFulltextAsync(fulltextDir, batch, reader, ct))
        { if (!options.DryRun) yield return change; await Task.Yield(); }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        long frames = 0, lus = 0;
        string frameDir = Path.Combine(context.EcosystemPath, "frame");
        string luDir    = Path.Combine(context.EcosystemPath, "lu");
        if (Directory.Exists(frameDir)) frames = Directory.EnumerateFiles(frameDir, "*.xml").LongCount();
        if (Directory.Exists(luDir))    lus    = Directory.EnumerateFiles(luDir, "lu*.xml").LongCount();
        return Task.FromResult<long?>(frames + lus);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    
    
    public IReadOnlyCollection<string> CanonicalNamesForReadback
    {
        get
        {
            foreach (var c in CorenessValues)
                _vocabularyNames.TryAdd($"framenet/coreness/{c}", 0);
            return _vocabularyNames.Keys.ToList();
        }
    }

    private static async IAsyncEnumerable<SubstrateChange> StreamFramesAsync(
        string frameDir, int batch, ISubstrateReader? reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(frameDir)) yield break;
        var b = NewBuilder("framenet/frame-0", batch, reader);
        int count = 0, batchNum = 0;

        foreach (var path in Directory.EnumerateFiles(frameDir, "*.xml").OrderBy(p => p, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            Frame? frame = ParseFrame(path);
            if (frame is null) continue;

            EmitFrameEntities(b, frame);
            EmitFrameAttestations(b, frame);

            if (++count >= batch)
            {
                yield return await b.BuildAsync(ct);
                b = NewBuilder($"framenet/frame-{++batchNum}", batch, reader);
                count = 0;
                await Task.Yield();
            }
        }
        if (count > 0) yield return await b.BuildAsync(ct);
    }

    private static void EmitFrameEntities(SubstrateChangeBuilder b, Frame frame)
    {
        
        
        CategoryAnchor.Emit(b, frame.Name, FrameTypeId, Source, SourceTrust.AcademicCurated);
        if (frame.Definition.Length > 0) ContentEmitter.Emit(b, frame.Definition, Source);
        foreach (var ex in frame.Examples) ContentEmitter.Emit(b, ex, Source);

        foreach (var fe in frame.Elements)
        {
            CategoryAnchor.Emit(b, fe.Name, FeTypeId, Source, SourceTrust.AcademicCurated);
            if (fe.Definition.Length > 0) ContentEmitter.Emit(b, fe.Definition, Source);
        }


        foreach (var lu in frame.LexUnits)
            ContentEmitter.Emit(b, lu.Lemma, Source);

        // Relation targets are frames OWNED by their own frame files; their entity rows land there
        // under the identical content-addressed id. We only need the id to anchor the attestation
        // (CategoryAnchor.Id below), not an entity row here — pre-emitting one was purely to satisfy
        // the deleted referential EXISTS pre-check, so it is removed.
    }

    
    
    
    private static void EmitFrameAttestations(SubstrateChangeBuilder b, Frame frame)
    {
        Hash128? frameAnchor = CategoryAnchor.Id(frame.Name);
        if (frameAnchor is null) return;
        Hash128 frameId = frameAnchor.Value;

        if (frame.Definition.Length > 0)
        {
            var defId = ContentEmitter.RootId(frame.Definition);
            if (defId is not null)
                b.AddAttestation(NativeAttestation.Categorical(
                    frameId, "HAS_DEFINITION", defId.Value, Source, SourceTrust.AcademicCurated));
        }
        foreach (var ex in frame.Examples)
        {
            var exId = ContentEmitter.RootId(ex);
            if (exId is not null)
                b.AddAttestation(NativeAttestation.Categorical(
                    frameId, "HAS_EXAMPLE", exId.Value, Source, SourceTrust.AcademicCurated));
        }

        foreach (var fe in frame.Elements)
        {
            var feNameId = CategoryAnchor.Id(fe.Name);
            if (feNameId is null) continue;
            Hash128? coreCtx = CorenessValues.Contains(fe.CoreType) ? CorenessId(fe.CoreType) : null;
            b.AddAttestation(NativeAttestation.Categorical(
                frameId, "HAS_FRAME_ELEMENT", feNameId.Value, Source, SourceTrust.AcademicCurated,
                contextId: coreCtx));

            if (fe.Definition.Length > 0)
            {
                var feDefId = ContentEmitter.RootId(fe.Definition);
                if (feDefId is not null)
                    b.AddAttestation(NativeAttestation.Categorical(
                        feNameId.Value, "HAS_DEFINITION", feDefId.Value,
                        Source, SourceTrust.AcademicCurated));
            }

            // FE-to-FE constraints, anchored the same way as HAS_FRAME_ELEMENT (CategoryAnchor by FE name).
            foreach (var reqName in fe.Requires)
                if (CategoryAnchor.Id(reqName) is { } reqId)
                    b.AddAttestation(NativeAttestation.Categorical(
                        feNameId.Value, "REQUIRES", reqId, Source, SourceTrust.AcademicCurated));
            foreach (var exName in fe.Excludes)
                if (CategoryAnchor.Id(exName) is { } exId)
                    b.AddAttestation(NativeAttestation.Categorical(
                        feNameId.Value, "EXCLUDES", exId, Source, SourceTrust.AcademicCurated));
        }

        foreach (var lu in frame.LexUnits)
        {
            var lemmaId = ContentEmitter.RootId(lu.Lemma);
            if (lemmaId is null) continue;

            PosReference.Attest(b, lemmaId.Value, lu.Pos, PosReference.PosTagset.FrameNet,
                Source, null, SourceTrust.AcademicCurated, _vocabularyNames);
            b.AddAttestation(NativeAttestation.Categorical(
                lemmaId.Value, "EVOKES_FRAME", frameId, Source, SourceTrust.AcademicCurated));
        }

        foreach (var rel in frame.Relations)
        {
            if (!RelationTypes.TryGetValue(rel.Type, out var typeName)) continue;
            Hash128? tgt = CategoryAnchor.Id(rel.TargetFrame);
            if (tgt is null) continue;
            // "X Subframe of Y" means X is a sub-event of the larger frame Y, so the HAS_SUBEVENT
            // edge runs Y -> X (subject HAS_SUBEVENT object). Every other FrameNet relation keeps
            // this frame as the subject.
            if (rel.Type == "Subframe of")
                b.AddAttestation(NativeAttestation.Categorical(
                    tgt.Value, typeName, frameId, Source, SourceTrust.AcademicCurated));
            else
                b.AddAttestation(NativeAttestation.Categorical(
                    frameId, typeName, tgt.Value, Source, SourceTrust.AcademicCurated));
        }
    }

    private static async IAsyncEnumerable<SubstrateChange> StreamFulltextAsync(
        string fulltextDir, int batch, ISubstrateReader? reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(fulltextDir)) yield break;
        var b = NewBuilder("framenet/fulltext-0", batch, reader);
        int count = 0, batchNum = 0;

        foreach (var path in Directory.EnumerateFiles(fulltextDir, "*.xml").OrderBy(p => p, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            await foreach (var ann in ParseFulltextAsync(path, ct))
            {
                var sentId = ContentEmitter.Emit(b, ann.Sentence, Source);
                var targetId = ContentEmitter.Emit(b, ann.TargetText, Source);
                var frameId = CategoryAnchor.Emit(b, ann.FrameName, FrameTypeId, Source, SourceTrust.AcademicCurated);
                if (sentId is not null && targetId is not null && frameId is not null)
                {
                    b.AddAttestation(NativeAttestation.Categorical(
                        targetId.Value, "EVOKES_FRAME", frameId.Value,
                        Source, SourceTrust.AcademicCurated, contextId: sentId.Value));
                }

                if (++count >= batch)
                {
                    yield return await b.BuildAsync(ct);
                    b = NewBuilder($"framenet/fulltext-{++batchNum}", batch, reader);
                    count = 0;
                    await Task.Yield();
                }
            }
        }
        if (count > 0) yield return await b.BuildAsync(ct);
    }

    // Every content emission (frame/FE/LU names, definitions, examples, fulltext sentences) routes
    // through the SHARED two-phase containment (EnableDeferredContent) — the same mechanism the
    // grammar sources and CILI use — so the graphemes/words shared across thousands of frames are
    // committed ONCE, not re-COPYed per frame. The builder MUST be drained with BuildAsync (not
    // Build) so the deferred-content probe-and-flush actually runs.
    private static SubstrateChangeBuilder NewBuilder(string unit, int batch, ISubstrateReader? reader) =>
        new SubstrateChangeBuilder(Source, unit, null,
            entityCapacity:      batch * 32,
            physicalityCapacity: batch * 32,
            attestationCapacity: batch * 32)
            .EnableDeferredContent(reader);


    internal static Frame? ParseFrame(string path)
    {
        XDocument doc;
        try { doc = XDocument.Load(path); }
        catch (XmlException) { return null; }
        return ParseFrame(doc);
    }

    internal static Frame? ParseFrame(XDocument doc)
    {
        XNamespace ns = Ns;
        var root = doc.Root;
        if (root is null || root.Name.LocalName != "frame") return null;
        string? name = (string?)root.Attribute("name");
        if (string.IsNullOrEmpty(name)) return null;

        var (frameDef, frameExamples) = ParseDefRoot((string?)root.Element(ns + "definition") ?? "");

        var elements = new List<FrameElement>();
        foreach (var fe in root.Elements(ns + "FE"))
        {
            string? feName = (string?)fe.Attribute("name");
            if (string.IsNullOrEmpty(feName)) continue;
            string coreType = (string?)fe.Attribute("coreType") ?? "";
            var (feDef, _) = ParseDefRoot((string?)fe.Element(ns + "definition") ?? "");
            // FE-to-FE structural constraints: <requiresFE>/<excludesFE> name a sibling FE in this frame.
            var requires = new List<string>();
            foreach (var rq in fe.Elements(ns + "requiresFE"))
                if ((string?)rq.Attribute("name") is { Length: > 0 } rn) requires.Add(rn);
            var excludes = new List<string>();
            foreach (var ex in fe.Elements(ns + "excludesFE"))
                if ((string?)ex.Attribute("name") is { Length: > 0 } en) excludes.Add(en);
            elements.Add(new FrameElement(feName, coreType, feDef, requires, excludes));
        }

        var lus = new List<LexUnit>();
        foreach (var lu in root.Elements(ns + "lexUnit"))
        {
            string? luName = (string?)lu.Attribute("name");
            string? pos    = (string?)lu.Attribute("POS");
            if (string.IsNullOrEmpty(luName) || string.IsNullOrEmpty(pos)) continue;
            if (!int.TryParse((string?)lu.Attribute("ID"), out int id)) continue;
            string lemma = LemmaOf(luName);
            if (lemma.Length == 0) continue;
            lus.Add(new LexUnit(id, lemma, pos));
        }

        var relations = new List<FrameRel>();
        foreach (var fr in root.Elements(ns + "frameRelation"))
        {
            string type = (string?)fr.Attribute("type") ?? "";
            if (!RelationTypes.ContainsKey(type)) continue;
            foreach (var rf in fr.Elements(ns + "relatedFrame"))
            {
                string target = ((string?)rf)?.Trim() ?? "";
                if (target.Length > 0) relations.Add(new FrameRel(type, target));
            }
        }

        return new Frame(name, frameDef, frameExamples, elements, lus, relations);
    }

    private static string LemmaOf(string luName)
    {
        int dot = luName.LastIndexOf('.');
        return (dot > 0 ? luName[..dot] : luName).Trim();
    }

    internal static async IAsyncEnumerable<FulltextAnno> ParseFulltextAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var settings = new XmlReaderSettings { Async = true, IgnoreWhitespace = false };
        using var reader = XmlReader.Create(path, settings);

        string sentence = "";
        string? frameName = null;
        int targetStart = -1, targetEnd = -1;
        bool inTargetLayer = false;

        while (await reader.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "sentence":
                        sentence = "";
                        break;
                    case "text":
                        sentence = await reader.ReadElementContentAsStringAsync();
                        break;
                    case "annotationSet":
                        frameName = reader.GetAttribute("frameName");
                        targetStart = targetEnd = -1;
                        inTargetLayer = false;
                        break;
                    case "layer":
                        inTargetLayer = reader.GetAttribute("name") == "Target";
                        break;
                    case "label":
                        if (inTargetLayer && reader.GetAttribute("name") == "Target")
                        {
                            if (targetStart < 0)
                            {
                                int.TryParse(reader.GetAttribute("start"), out targetStart);
                                if (!int.TryParse(reader.GetAttribute("end"), out targetEnd)) targetEnd = -1;
                            }
                        }
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "annotationSet")
            {
                if (!string.IsNullOrEmpty(frameName) && !string.IsNullOrEmpty(sentence)
                    && targetStart >= 0 && targetEnd >= targetStart && targetEnd < sentence.Length)
                {
                    string target = sentence.Substring(targetStart, targetEnd - targetStart + 1).Trim();
                    if (target.Length > 0)
                        yield return new FulltextAnno(sentence, target, frameName!);
                }
                frameName = null;
                targetStart = targetEnd = -1;
                inTargetLayer = false;
            }
        }
    }

    internal static (string Def, List<string> Examples) ParseDefRoot(string raw)
    {
        var examples = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return ("", examples);

        string wrapped = raw.Contains('<') ? raw : $"<def-root>{System.Security.SecurityElement.Escape(raw)}</def-root>";
        XElement el;
        try
        {
            el = XElement.Parse(wrapped, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return (StripTags(raw).Trim(), examples);
        }

        var defBody = new StringBuilder();
        CollectText(el, defBody, examples, insideExample: false);
        return (CollapseWs(defBody.ToString()), examples);
    }

    private static void CollectText(XElement el, StringBuilder def, List<string> examples, bool insideExample)
    {
        foreach (var node in el.Nodes())
        {
            if (node is XText t)
            {
                (insideExample ? null : def)?.Append(t.Value);
            }
            else if (node is XElement child)
            {
                if (child.Name.LocalName == "ex")
                {
                    string ex = CollapseWs(InnerText(child));
                    if (ex.Length > 0) examples.Add(ex);
                }
                else
                {
                    CollectText(child, def, examples, insideExample);
                }
            }
        }
    }

    private static string InnerText(XElement el)
    {
        var sb = new StringBuilder();
        foreach (var n in el.DescendantNodes())
            if (n is XText t) sb.Append(t.Value);
        return sb.ToString();
    }

    private static string StripTags(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool inTag = false;
        foreach (char c in s)
        {
            if (c == '<') inTag = true;
            else if (c == '>') inTag = false;
            else if (!inTag) sb.Append(c);
        }
        return sb.ToString();
    }

    private static string CollapseWs(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool ws = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) { ws = true; continue; }
            if (ws && sb.Length > 0) sb.Append(' ');
            ws = false;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    internal sealed record Frame(
        string Name, string Definition, List<string> Examples,
        List<FrameElement> Elements, List<LexUnit> LexUnits, List<FrameRel> Relations);

    internal sealed record FrameElement(
        string Name, string CoreType, string Definition,
        List<string> Requires, List<string> Excludes);

    internal sealed record LexUnit(int Id, string Lemma, string Pos);

    internal sealed record FrameRel(string Type, string TargetFrame);

    internal sealed record FulltextAnno(string Sentence, string TargetText, string FrameName);
}
