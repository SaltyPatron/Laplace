using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.FrameNet;

public sealed class FrameNetDecomposer : IDecomposer, IIngestInventoryProvider
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/FrameNetDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    private static readonly Hash128 FrameTypeId = EntityTypeRegistry.FrameNetFrame;
    private static readonly Hash128 FeTypeId = EntityTypeRegistry.FrameNetFe;
    private static readonly Hash128 CorenessTypeId = EntityTypeRegistry.FrameNetCoreness;





    private static Hash128 CorenessId(string coreType) =>
        Hash128.OfCanonical($"framenet/coreness/{coreType}");

    private static readonly ConcurrentDictionary<string, byte> _vocabularyNames = new(StringComparer.Ordinal);
    internal static ConcurrentDictionary<string, byte> VocabularyNames => _vocabularyNames;

    private static readonly string[] CorenessValues =
        ["Core", "Peripheral", "Extra-Thematic", "Core-Unexpressed"];

    private static readonly Dictionary<string, string> RelationTypes = new(StringComparer.Ordinal)
    {
        ["Inherits from"] = "INHERITS_FROM",
        ["Uses"] = "FRAME_USES",
        ["Perspective on"] = "PERSPECTIVE_ON",
        ["Subframe of"] = "HAS_SUBEVENT",
        ["Is Causative of"] = "CAUSATIVE_OF",
        ["Is Inchoative of"] = "INCHOATIVE_OF",
        ["Precedes"] = "PRECEDES",
        ["See also"] = "ALSO_SEE",
    };

    private const string Ns = "http://framenet.icsi.berkeley.edu";

    public Hash128 SourceId => Source;
    public string SourceName => "FrameNetDecomposer";
    public int LayerOrder => 3;
    public Hash128 TrustClassId => TrustClass;













    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = await SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            typeNodeNames: ["FrameNet_Frame", "FrameNet_FE", "FrameNet_LU", "FrameNet_Coreness"],
            relationNodeNames: ["EVOKES_FRAME", "HAS_FRAME_ELEMENT", "REQUIRES", "EXCLUDES",
                "HAS_VALENCE_PATTERN", "HAS_DEFINITION", "HAS_POS", "HAS_EXAMPLE",
                "FRAME_USES", "PERSPECTIVE_ON", "INHERITS_FROM", "CAUSATIVE_OF",
                "INCHOATIVE_OF", "PRECEDES", "ALSO_SEE", "IS_A", "HAS_SUBEVENT", "RELATED_TO"],
            readbackNames: _vocabularyNames, ct: ct);

        var seed = new SubstrateChangeBuilder(
            Source, "bootstrap/framenet-vocab", null,
            entityCapacity: CorenessValues.Length + 1,
            physicalityCapacity: 0, attestationCapacity: 0);
        seed.AddEntity(new EntityRow(CorenessTypeId, EntityTier.Word,
            BootstrapIntentBuilder.TypeMetaTypeId, Source));
        foreach (var c in CorenessValues)
            seed.AddEntity(new EntityRow(CorenessId(c), EntityTier.Word, CorenessTypeId, Source));
        await context.Writer.ApplyAsync(seed.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string frameDir = Path.Combine(context.EcosystemPath, "frame");
        string luDir = Path.Combine(context.EcosystemPath, "lu");
        string fulltextDir = Path.Combine(context.EcosystemPath, "fulltext");
        int batch = options.BatchSize > 1 ? options.BatchSize : 4096;
        var uncapped = options with { MaxInputUnits = 0 };

        await foreach (var change in DecomposerBatch.RunAsync(
            ParseAllFramesAsync(frameDir, ct),
            static (frame, b) => { EmitFrameEntities(b, frame); EmitFrameAttestations(b, frame); },
            Source, "framenet/frame", batch, context.Reader, options, ct))
            yield return change;

        await foreach (var change in DecomposerBatch.RunAsync(
            FrameNetLuIngest.ParseAllLusAsync(luDir, ct),
            (lu, b) => FrameNetLuIngest.EmitLu(b, lu, Source),
            Source, "framenet/lu", batch, context.Reader, uncapped, ct))
            yield return change;

        await foreach (var change in DecomposerBatch.RunAsync(
            ParseAllFulltextAsync(fulltextDir, ct),
            static (ann, b) => ComposeFulltextAnno(ann, b),
            Source, "framenet/fulltext", batch, context.Reader, uncapped, ct))
            yield return change;
    }

    private static async IAsyncEnumerable<Frame> ParseAllFramesAsync(
        string frameDir, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(frameDir)) yield break;
        foreach (var path in Directory.EnumerateFiles(frameDir, "*.xml").OrderBy(p => p, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (ParseFrame(path) is { } frame) yield return frame;
        }
    }

    private static async IAsyncEnumerable<FulltextAnno> ParseAllFulltextAsync(
        string fulltextDir, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(fulltextDir)) yield break;
        foreach (var path in Directory.EnumerateFiles(fulltextDir, "*.xml").OrderBy(p => p, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            await foreach (var ann in ParseFulltextAsync(path, ct))
                yield return ann;
        }
    }

    private static void ComposeFulltextAnno(FulltextAnno ann, SubstrateChangeBuilder b)
    {
        var sentId = ContentEmitter.Emit(b, ann.Sentence, Source);
        var targetId = ContentEmitter.Emit(b, ann.TargetText, Source);
        var frameId = CategoryAnchor.Emit(b, ann.FrameName, FrameTypeId, Source, SourceTrust.AcademicCurated);
        if (sentId is not null && targetId is not null && frameId is not null)
            b.AddAttestation(NativeAttestation.Categorical(
                targetId.Value, "EVOKES_FRAME", frameId.Value,
                Source, SourceTrust.AcademicCurated, contextId: sentId.Value));
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        string frameDir = Path.Combine(context.EcosystemPath, "frame");
        string luDir = Path.Combine(context.EcosystemPath, "lu");
        var paths = new List<string>();
        if (Directory.Exists(frameDir))
            paths.AddRange(Directory.EnumerateFiles(frameDir, "*.xml"));
        if (Directory.Exists(luDir))
            paths.AddRange(Directory.EnumerateFiles(luDir, "lu*.xml"));
        if (paths.Count == 0) return Task.FromResult<IngestInventory?>(null);
        return Task.FromResult(IngestInventory.FromFiles("frames", paths, options.MaxInputUnits, ct));
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        long frames = 0, lus = 0;
        string frameDir = Path.Combine(context.EcosystemPath, "frame");
        string luDir = Path.Combine(context.EcosystemPath, "lu");
        if (Directory.Exists(frameDir)) frames = Directory.EnumerateFiles(frameDir, "*.xml").LongCount();
        if (Directory.Exists(luDir)) lus = Directory.EnumerateFiles(luDir, "lu*.xml").LongCount();
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



            if (rel.Type == "Subframe of")
                b.AddAttestation(NativeAttestation.Categorical(
                    tgt.Value, typeName, frameId, Source, SourceTrust.AcademicCurated));
            else
                b.AddAttestation(NativeAttestation.Categorical(
                    frameId, typeName, tgt.Value, Source, SourceTrust.AcademicCurated));
        }
    }



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
            string? pos = (string?)lu.Attribute("POS");
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
