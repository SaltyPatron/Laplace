using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.FrameNet;

/// <summary>
/// FrameNet rides the multi-file spine — one pool, one open per path, compose
/// whatever that file is (frame / LU / fulltext). Nested MultiFile phases inside
/// MultiPhase were a reinvention that swept directories three times.
/// </summary>
public sealed class FrameNetDecomposer : DecomposerMultiFile<FrameNetDecomposer.FnRecord, FrameNetSource, FullScope>, IIngestInventoryProvider
{
    public static readonly Hash128 Source = FrameNetSource.SourceId;
    public static readonly Hash128 TrustClass = FrameNetSource.TrustClass;

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

    public override int LayerOrder => 3;
    public override bool PerFileCompletion => true;
    protected override double SourceTrust => TC.AcademicCurated;

    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => _vocabularyNames;

    protected override async Task OnInitializedAsync(IDecomposerContext context, CancellationToken ct)
    {
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

    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options) =>
        InputFilesLabeled(ecosystemPath);

    protected override async IAsyncEnumerable<FnRecord> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (fileLabel.StartsWith("framenet/frame/", StringComparison.Ordinal))
        {
            if (ParseFrame(filePath) is { } frame)
                yield return new FnFrame(frame);
        }
        else if (fileLabel.StartsWith("framenet/lu/", StringComparison.Ordinal))
        {
            if (FrameNetLuIngest.ParseLu(filePath) is { } lu)
                yield return new FnLu(lu);
        }
        else if (fileLabel.StartsWith("framenet/fulltext/", StringComparison.Ordinal))
        {
            await foreach (var ann in ParseFulltextAsync(filePath, fileLabel, ct))
                yield return new FnFulltext(ann);
        }
    }

    protected override IIngestRecordHandler<FnRecord> CreateHandlerForFile(
        string fileLabel, DecomposerOptions options) =>
        new DirectComposeHandler<FnRecord>(Compose);

    protected override IngestBatchConfig ConfigForFile(
        string fileLabel, ISubstrateReader? reader, DecomposerOptions options)
    {
        string kind = fileLabel.StartsWith("framenet/lu/", StringComparison.Ordinal) ? "lu"
            : fileLabel.StartsWith("framenet/fulltext/", StringComparison.Ordinal) ? "fulltext"
            : "frame";
        return IngestPipelineDefaults.ApplyMaxInputUnits(
            IngestPipelineDefaults.Compose(
                Source, $"FrameNetDecomposer/{kind}", options, reader,
                IngestSourceProfile.FrameNet),
            options);
    }

    private static void Compose(FnRecord record, SubstrateChangeBuilder b)
    {
        switch (record)
        {
            case FnFrame(var frame):
                EmitFrameEntities(b, frame);
                EmitFrameAttestations(b, frame);
                break;
            case FnLu(var lu):
                FrameNetLuIngest.EmitLu(b, lu, Source);
                break;
            case FnFulltext(var ann):
                ComposeFulltextAnno(ann, b);
                break;
        }
    }

    private static void ComposeFulltextAnno(FulltextAnno ann, SubstrateChangeBuilder b)
    {
        var sentId = ContentEmitter.Emit(b, ann.Sentence, Source);
        var targetId = ContentEmitter.Emit(b, ann.TargetText, Source);
        var frameId = CategoryAnchor.Emit(b, ann.FrameName, FrameTypeId, Source, TC.AcademicCurated);
        if (sentId is null || targetId is null || frameId is null) return;

        Hash128 startId = OffsetId(ann.TargetStart);
        Hash128 endId = OffsetId(ann.TargetEnd);
        b.AddEntity(startId, EntityTier.Word, EntityTypeRegistry.Ordinal, Source);
        b.AddEntity(endId, EntityTier.Word, EntityTypeRegistry.Ordinal, Source);

        Hash128 schemaId = Hash128.OfCanonical("framenet/span-annotation/schema/v1");
        b.AddEntity(schemaId, EntityTier.Word, EntityTypeRegistry.SourceReference, Source);
        Hash128[] constituents = [schemaId, sentId.Value, startId, endId, targetId.Value];
        Hash128 annotationId = Hash128.Merkle(EntityTier.Document, constituents);
        b.AddEntity(
            annotationId, EntityTier.Document, EntityTypeRegistry.FrameNetAnnotation, Source);

        byte[] sentenceUtf8 = Encoding.UTF8.GetBytes(ann.Sentence);
        if (!TextEntityBuilder.TryDecomposeRoot(
                sentenceUtf8, out _, out _, out double x, out double y, out double z, out double m))
            throw new InvalidOperationException("FrameNet span annotation has no sentence placement");
        Hash128 physicalityId = PhysicalityId.Compute(annotationId, PhysicalityType.Content);
        if (b.TrySeePhysicality(physicalityId))
        {
            double[] coord = [x, y, z, m];
            b.AddPhysicalityPreSeen(new PhysicalityRow(
                physicalityId, annotationId, Source, PhysicalityType.Content,
                x, y, z, m, Hilbert128.Encode(coord),
                Trajectory.Build(constituents), constituents.Length,
                null, null, 0));
        }

        Hash128 occurrenceId = AnnotationOccurrenceId(ann, annotationId);
        b.AddEntity(
            occurrenceId, EntityTier.Document,
            EntityTypeRegistry.FrameNetAnnotationOccurrence, Source);
        b.AddAttestation(NativeAttestation.Categorical(
            annotationId, "EVOKES_FRAME", frameId.Value,
            Source, TC.AcademicCurated, contextId: occurrenceId));
    }

    internal static Hash128 OffsetId(int offset) =>
        Hash128.OfCanonical($"framenet/character-offset/{offset}/v1");

    private static Hash128 AnnotationOccurrenceId(FulltextAnno ann, Hash128 annotationId)
    {
        static string Hex(string value) => Convert.ToHexString(Encoding.UTF8.GetBytes(value));
        return Hash128.OfCanonical(
            $"framenet/annotation-occurrence/{annotationId}/{Hex(ann.FileLabel)}/"
            + $"{Hex(ann.SentenceReference)}/{Hex(ann.AnnotationReference)}/v1");
    }

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = InputFilesLabeled(context.EcosystemPath).Select(x => x.Path).ToList();
        // One XML file does not equal one input record: frame/LU files yield one,
        // while fulltext files yield every annotated target. Keep record progress and
        // exact file completion as separate grains; the runner publishes the observed
        // record total when the uncapped run finishes.
        return Task.FromResult(IngestInventory.FromFilesWithUnknownUnitCount(
            "records", paths, options.MaxInputUnits, tracksFileCompletion: true));
    }

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        int n = InputFilesLabeled(context.EcosystemPath).Count;
        return Task.FromResult<long?>(n == 0 ? null : n);
    }

    private static List<(string Path, string Label)> InputFilesLabeled(string ecosystemPath)
    {
        var paths = new List<(string, string)>();
        string frameDir = Path.Combine(ecosystemPath, "frame");
        string luDir = Path.Combine(ecosystemPath, "lu");
        string fulltextDir = Path.Combine(ecosystemPath, "fulltext");
        if (Directory.Exists(frameDir))
        {
            foreach (var p in SharedXmlFramesetReader.EnumerateXmlFiles(frameDir))
                paths.Add((p, $"framenet/frame/{Path.GetFileName(p)}"));
        }
        if (Directory.Exists(luDir))
        {
            foreach (var p in Directory.EnumerateFiles(luDir, "lu*.xml").OrderBy(x => x, StringComparer.Ordinal))
                paths.Add((p, $"framenet/lu/{Path.GetFileName(p)}"));
        }
        if (Directory.Exists(fulltextDir))
        {
            foreach (var p in SharedXmlFramesetReader.EnumerateXmlFiles(fulltextDir))
                paths.Add((p, $"framenet/fulltext/{Path.GetFileName(p)}"));
        }
        return paths;
    }

    public abstract record FnRecord;
    public sealed record FnFrame(Frame Frame) : FnRecord;
    public sealed record FnLu(FrameNetLuIngest.LuDocument Lu) : FnRecord;
    public sealed record FnFulltext(FulltextAnno Ann) : FnRecord;

    public override IReadOnlyCollection<string> CanonicalNamesForReadback
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
        Hash128? frameAnchor = CategoryAnchor.Emit(
            b, frame.Name, FrameTypeId, Source, TC.AcademicCurated);
        if (frameAnchor is null) return;
        if (frame.Definition.Length > 0) ContentEmitter.Emit(b, frame.Definition, Source);
        foreach (var ex in frame.Examples) ContentEmitter.Emit(b, ex, Source);

        foreach (var fe in frame.Elements)
        {
            ContentEmitter.Emit(b, fe.Name, Source);
            RoleAnchor.Emit(
                b, RoleIdentityKind.FrameNet, frameAnchor.Value, fe.Name,
                FeTypeId, Source, TC.AcademicCurated);
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
                b.AddAttestation(NativeAttestation.CategoricalResolved(
                    frameId, FrameNetSource.HasDefinitionTypeId, defId.Value,
                    Source, null, TC.AcademicCurated));
        }
        foreach (var ex in frame.Examples)
        {
            var exId = ContentEmitter.RootId(ex);
            if (exId is not null)
                b.AddAttestation(NativeAttestation.Categorical(
                    frameId, "HAS_EXAMPLE", exId.Value, Source, TC.AcademicCurated));
        }

        foreach (var fe in frame.Elements)
        {
            var feNameId = ContentEmitter.RootId(fe.Name);
            var feRoleId = RoleAnchor.Id(RoleIdentityKind.FrameNet, frameId, fe.Name);
            if (feNameId is null || feRoleId is null) continue;
            Hash128? coreCtx = CorenessValues.Contains(fe.CoreType) ? CorenessId(fe.CoreType) : null;
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                frameId, FrameNetSource.HasFrameElementTypeId, feRoleId.Value,
                Source, null, TC.AcademicCurated));
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                feRoleId.Value, FrameNetSource.HasNameAliasTypeId, feNameId.Value,
                Source, null, TC.AcademicCurated));
            if (coreCtx is { } coreness)
                b.AddAttestation(NativeAttestation.CategoricalResolved(
                    feRoleId.Value, FrameNetSource.HasFeatureTypeId, coreness,
                    Source, null, TC.AcademicCurated));

            if (fe.Definition.Length > 0)
            {
                var feDefId = ContentEmitter.RootId(fe.Definition);
                if (feDefId is not null)
                    b.AddAttestation(NativeAttestation.CategoricalResolved(
                        feRoleId.Value, FrameNetSource.HasDefinitionTypeId, feDefId.Value,
                        Source, null, TC.AcademicCurated));
            }


            foreach (var reqName in fe.Requires)
                if (RoleAnchor.Id(RoleIdentityKind.FrameNet, frameId, reqName) is { } reqId)
                    b.AddAttestation(NativeAttestation.CategoricalResolved(
                        feRoleId.Value, FrameNetSource.RequiresTypeId, reqId,
                        Source, null, TC.AcademicCurated));
            foreach (var exName in fe.Excludes)
                if (RoleAnchor.Id(RoleIdentityKind.FrameNet, frameId, exName) is { } exId)
                    b.AddAttestation(NativeAttestation.CategoricalResolved(
                        feRoleId.Value, FrameNetSource.ExcludesTypeId, exId,
                        Source, null, TC.AcademicCurated));
        }

        foreach (var lu in frame.LexUnits)
        {
            var lemmaId = ContentEmitter.RootId(lu.Lemma);
            if (lemmaId is null) continue;

            PosReference.Attest(b, lemmaId.Value, lu.Pos, PosReference.PosTagset.FrameNet,
                Source, null, TC.AcademicCurated, _vocabularyNames);
            b.AddAttestation(NativeAttestation.Categorical(
                lemmaId.Value, "EVOKES_FRAME", frameId, Source, TC.AcademicCurated));
        }

        foreach (var rel in frame.Relations)
        {
            if (!RelationTypes.TryGetValue(rel.Type, out var typeName)) continue;
            Hash128? tgt = CategoryAnchor.Id(rel.TargetFrame);
            if (tgt is null) continue;



            if (rel.Type == "Subframe of")
                b.AddAttestation(NativeAttestation.Categorical(
                    tgt.Value, typeName, frameId, Source, TC.AcademicCurated));
            else
                b.AddAttestation(NativeAttestation.Categorical(
                    frameId, typeName, tgt.Value, Source, TC.AcademicCurated));
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
            string lemma = FrameNetLemmaHelper.LemmaOf(luName);
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

    internal static async IAsyncEnumerable<FulltextAnno> ParseFulltextAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var ann in ParseFulltextAsync(path, Path.GetFileName(path), ct))
            yield return ann;
    }

    internal static async IAsyncEnumerable<FulltextAnno> ParseFulltextAsync(
        string path, string fileLabel, [EnumeratorCancellation] CancellationToken ct)
    {
        var settings = new XmlReaderSettings { Async = true, IgnoreWhitespace = false };
        using var reader = XmlReader.Create(path, settings);

        string sentence = "";
        string? frameName = null;
        string sentenceReference = "";
        string annotationReference = "";
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
                        sentenceReference = reader.GetAttribute("ID") ?? "";
                        break;
                    case "text":
                        sentence = await reader.ReadElementContentAsStringAsync();
                        break;
                    case "annotationSet":
                        frameName = reader.GetAttribute("frameName");
                        annotationReference = reader.GetAttribute("ID") ?? "";
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
                        yield return new FulltextAnno(
                            sentence, target, frameName!, targetStart, targetEnd,
                            fileLabel, sentenceReference, annotationReference);
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

    public sealed record Frame(
        string Name, string Definition, List<string> Examples,
        List<FrameElement> Elements, List<LexUnit> LexUnits, List<FrameRel> Relations);

    public sealed record FrameElement(
        string Name, string CoreType, string Definition,
        List<string> Requires, List<string> Excludes);

    public sealed record LexUnit(int Id, string Lemma, string Pos);

    public sealed record FrameRel(string Type, string TargetFrame);

    public sealed record FulltextAnno(
        string Sentence,
        string TargetText,
        string FrameName,
        int TargetStart,
        int TargetEnd,
        string FileLabel,
        string SentenceReference,
        string AnnotationReference);
}
