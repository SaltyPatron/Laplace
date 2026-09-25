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
        ContentEmitter.RootId(coreType)
        ?? throw new InvalidOperationException($"FrameNet coreness could not be composed: {coreType}");

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
            entityCapacity: CorenessValues.Length * 4,
            physicalityCapacity: CorenessValues.Length * 4,
            attestationCapacity: CorenessValues.Length)
            .DeclareSourcePrior(SourceTrust);
        foreach (string value in CorenessValues)
        {
            Hash128 id = ContentEmitter.Emit(seed, value, Source)
                ?? throw new InvalidOperationException(
                    $"FrameNet coreness could not be admitted: {value}");
        }
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
            if (FrameNetLuIngest.ParseLu(filePath, fileLabel) is { } lu)
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

    internal static void ComposeFulltextAnno(FulltextAnno ann, SubstrateChangeBuilder b)
    {
        bool unresolved = ann.HasUnresolvedSpans;
        bool resolvedTarget = ann.HasResolvedTarget;
        int sourceLength = ann.Sentence.EnumerateRunes().Count();
        var sentId = ContentEmitter.Emit(b, ann.Sentence, Source);
        Hash128? targetId = resolvedTarget
            ? ContentEmitter.Emit(b, ann.TargetText!, Source) : null;
        var frameId = CategoryAnchor.Emit(b, ann.FrameName, Source);
        if (sentId is null || frameId is null || (resolvedTarget && targetId is null)) return;

        // These are structural delimiters inside the exact annotation trajectory.
        // They are ordinary reusable content entities with their own physicalities,
        // never opaque path hashes or bare support rows.
        foreach (string marker in AnnotationMarkerTexts)
            RequireAnnotationMarker(b, marker);
        if (unresolved)
            RequireAnnotationMarker(b, UnresolvedAnnotationSchemaText);
        // Typed source annotation, not a text continuation: labels retain their
        // layer, source rank, offsets, null instantiation and frame-scoped role.
        // Equal labels on different spans or under different frames stay distinct.
        var constituents = new List<Hash128>
        {
            unresolved ? UnresolvedAnnotationSchemaId : AnnotationSchemaId,
            sentId.Value, frameId.Value, targetId ?? AnnotationNoneId,
            ContentOrNone(ann.Status),
        };
        foreach (var layer in ann.Layers)
        {
            constituents.Add(AnnotationLayerId);
            constituents.Add(ContentOrNone(layer.Name));
            constituents.Add(ContentOrNone(layer.Rank));
            foreach (var label in layer.Labels)
            {
                constituents.Add(AnnotationLabelId);
                constituents.Add(ContentOrNone(label.Name));
                constituents.Add(OffsetOrNone(label.Start));
                constituents.Add(OffsetOrNone(label.End));
                constituents.Add(ContentOrNone(label.InstantiationType));
                Hash128 roleId = AnnotationNoneId;
                if (layer.Name == "FE" && label.Name.Length > 0)
                    roleId = RoleAnchor.Declare(
                        b, RoleIdentityKind.FrameNet, frameId.Value, label.Name,
                        FeTypeId, Source) ?? AnnotationNoneId;
                constituents.Add(roleId);
                if (unresolved)
                {
                    SpanResolution state = ClassifySpan(label, sourceLength);
                    Hash128 resolution = RequireAnnotationMarker(
                        b, SpanResolutionText(state));
                    constituents.Add(resolution);
                }
            }
            constituents.Add(AnnotationLayerEndId);
        }
        constituents.Add(AnnotationLayersEndId);

        Hash128[] flat = constituents.ToArray();
        Hash128 annotationId = Hash128.Merkle(EntityTier.Document, flat);
        b.AddEntity(annotationId, EntityTier.Document, EntityTypeRegistry.FrameNetAnnotation);

        byte[] sentenceUtf8 = Encoding.UTF8.GetBytes(ann.Sentence);
        if (!TextEntityBuilder.TryDecomposeRoot(
                sentenceUtf8, out _, out _, out double x, out double y, out double z, out double m))
            throw new InvalidOperationException("FrameNet span annotation has no sentence placement");
        Hash128 physicalityId = PhysicalityId.Compute(annotationId, PhysicalityType.ParseStructure);
        {
            double[] coord = [x, y, z, m];
            b.AddPhysicality(new PhysicalityRow(
                physicalityId, annotationId, Source, PhysicalityType.ParseStructure,
                x, y, z, m, Hilbert128.Encode(coord),
                Trajectory.Build(flat), flat.Length, null, null, 0));
        }

        OrderedCompositionComponent annotationComponent =
            new(annotationId, EntityTier.Document, x, y, z, m);
        OrderedCompositionComponent fileComponent =
            RequireAnnotationComponent(b, ann.FileLabel);
        OrderedCompositionComponent sentenceReferenceComponent =
            RequireAnnotationComponent(b, ann.SentenceReference);
        OrderedCompositionComponent annotationReferenceComponent =
            RequireAnnotationComponent(b, ann.AnnotationReference);
        Span<OrderedCompositionResult> occurrenceResult = stackalloc OrderedCompositionResult[1];
        OrderedComposition.StageBatch(
            b.ContentStage,
            [new OrderedCompositionRequest(
                [
                    annotationComponent,
                    fileComponent,
                    sentenceReferenceComponent,
                    annotationReferenceComponent,
                ],
                EntityTypeRegistry.FrameNetAnnotationOccurrence,
                Source,
                0)],
            occurrenceResult);
        Hash128 occurrenceId = occurrenceResult[0].Id;
        Hash128 expectedOccurrence = AnnotationOccurrenceId(ann, annotationId);
        if (occurrenceId != expectedOccurrence)
            throw new InvalidOperationException(
                "FrameNet annotation occurrence identity diverged from its trajectory");

        b.AddAttestation(NativeAttestation.CategoricalResolved(
            sentId.Value, FrameNetSource.HasParseTypeId, annotationId,
            Source, occurrenceId, TC.AcademicCurated));
        // A source annotation remains evidence even when its coordinates do not
        // resolve. Do not turn a partial or invented target into a frame witness.
        if (targetId is not null)
            b.AddAttestation(NativeAttestation.Categorical(
                annotationId, "EVOKES_FRAME", frameId.Value,
                Source, TC.AcademicCurated, contextId: occurrenceId));

        Hash128 ContentOrNone(string? value) =>
            value is null ? AnnotationNoneId
                : ContentEmitter.Emit(b, value, Source) ?? AnnotationNoneId;

        Hash128 OffsetOrNone(int? offset)
        {
            if (offset is null) return AnnotationNoneId;
            string value = offset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Hash128 id = ContentEmitter.Emit(b, value, Source)
                ?? throw new InvalidOperationException(
                    $"FrameNet character offset could not be admitted: {value}");
            return id;
        }
    }

    internal static Hash128 OffsetId(int offset) =>
        ContentEmitter.RootId(offset.ToString(System.Globalization.CultureInfo.InvariantCulture))
        ?? throw new InvalidOperationException($"FrameNet character offset could not be composed: {offset}");

    private const string AnnotationSchemaText = "framenet/span-annotation/schema/v2";
    private const string UnresolvedAnnotationSchemaText = "framenet/span-annotation/schema/v3";
    private const string AnnotationNoneText = "framenet/span-annotation/none/v2";
    private const string AnnotationLayerText = "framenet/span-annotation/layer/v2";
    private const string AnnotationLabelText = "framenet/span-annotation/label/v2";
    private const string AnnotationLayerEndText = "framenet/span-annotation/layer-end/v2";
    private const string AnnotationLayersEndText = "framenet/span-annotation/layers-end/v2";

    internal static readonly Hash128 AnnotationSchemaId = RequiredAnnotationRoot(AnnotationSchemaText);
    internal static readonly Hash128 UnresolvedAnnotationSchemaId =
        RequiredAnnotationRoot(UnresolvedAnnotationSchemaText);

    internal enum SpanResolution { Resolved, NoSpan, Incomplete, Reversed, OutOfRange }

    private static string SpanResolutionText(SpanResolution resolution) =>
        "framenet/span-resolution/" + (resolution switch
        {
            SpanResolution.Resolved => "resolved",
            SpanResolution.NoSpan => "no-span",
            SpanResolution.Incomplete => "incomplete",
            SpanResolution.Reversed => "reversed",
            SpanResolution.OutOfRange => "out-of-range",
            _ => throw new ArgumentOutOfRangeException(nameof(resolution)),
        }) + "/v1";

    internal static Hash128 SpanResolutionId(SpanResolution resolution) =>
        RequiredAnnotationRoot(SpanResolutionText(resolution));

    internal static readonly Hash128 AnnotationNoneId = RequiredAnnotationRoot(AnnotationNoneText);
    internal static readonly Hash128 AnnotationLayerId = RequiredAnnotationRoot(AnnotationLayerText);
    internal static readonly Hash128 AnnotationLabelId = RequiredAnnotationRoot(AnnotationLabelText);
    internal static readonly Hash128 AnnotationLayerEndId = RequiredAnnotationRoot(AnnotationLayerEndText);
    internal static readonly Hash128 AnnotationLayersEndId = RequiredAnnotationRoot(AnnotationLayersEndText);

    private static readonly string[] AnnotationMarkerTexts =
    [
        AnnotationSchemaText,
        AnnotationNoneText,
        AnnotationLayerText,
        AnnotationLabelText,
        AnnotationLayerEndText,
        AnnotationLayersEndText,
    ];

    private static Hash128 RequiredAnnotationRoot(string value) =>
        ContentEmitter.RootId(value)
        ?? throw new InvalidOperationException(
            $"FrameNet annotation constituent could not be composed: {value}");

    private static Hash128 RequireAnnotationMarker(SubstrateChangeBuilder builder, string value) =>
        ContentEmitter.Emit(builder, value, Source)
        ?? throw new InvalidOperationException(
            $"FrameNet annotation constituent could not be admitted: {value}");

    private static OrderedCompositionComponent RequireAnnotationComponent(
        SubstrateChangeBuilder builder, string value)
    {
        if (string.IsNullOrEmpty(value))
            throw new InvalidDataException(
                "FrameNet annotation occurrence has an empty source reference constituent");
        return ContentEmitter.StageComponent(builder, value, Source)
            ?? throw new InvalidOperationException(
                $"FrameNet annotation occurrence constituent could not be admitted: {value}");
    }

    private static Hash128 AnnotationOccurrenceId(FulltextAnno ann, Hash128 annotationId)
    {
        Span<Hash128> constituents = stackalloc Hash128[4]
        {
            annotationId,
            RequiredAnnotationRoot(ann.FileLabel),
            RequiredAnnotationRoot(ann.SentenceReference),
            RequiredAnnotationRoot(ann.AnnotationReference),
        };
        return Hash128.Merkle(EntityTier.Document, constituents);
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
            foreach (var value in CorenessValues)
                _vocabularyNames.TryAdd(value, 0);
            return _vocabularyNames.Keys.ToList();
        }
    }

    private static void EmitFrameEntities(SubstrateChangeBuilder b, Frame frame)
    {
        Hash128? frameAnchor = CategoryAnchor.Emit(b, frame.Name, Source);
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
        string? status = null;
        var layers = new List<AnnotationLayer>();
        AnnotationLayer? currentLayer = null;

        bool advance = true;
        while (!reader.EOF)
        {
            if (advance && !await reader.ReadAsync()) break;
            advance = true;
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
                        advance = false;
                        break;
                    case "annotationSet":
                        frameName = reader.GetAttribute("frameName");
                        annotationReference = reader.GetAttribute("ID") ?? "";
                        status = reader.GetAttribute("status");
                        layers.Clear();
                        currentLayer = null;
                        break;
                    case "layer":
                        currentLayer = new AnnotationLayer(
                            reader.GetAttribute("name") ?? "", reader.GetAttribute("rank"), []);
                        layers.Add(currentLayer);
                        if (reader.IsEmptyElement) currentLayer = null;
                        break;
                    case "label":
                        if (currentLayer is not null)
                            currentLayer.Labels.Add(ReadAnnotationLabel(
                                reader.GetAttribute("name"), reader.GetAttribute("start"),
                                reader.GetAttribute("end"), reader.GetAttribute("itype")));
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "layer")
                currentLayer = null;
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "annotationSet")
            {
                if (CreateAnnotation(sentence, frameName, layers.ToArray(), fileLabel,
                        sentenceReference, annotationReference, status) is { } annotation)
                    yield return annotation;
                frameName = null;
                layers.Clear();
                currentLayer = null;
            }
        }
    }

    internal static AnnotationLabel ReadAnnotationLabel(
        string? name, string? start, string? end, string? instantiationType)
    {
        static int? Offset(string? value)
        {
            if (value is null) return null;
            if (int.TryParse(value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int offset) && offset >= 0)
                return offset;
            throw new FormatException($"Invalid FrameNet character offset '{value}'");
        }
        int? first = Offset(start), last = Offset(end);
        return new AnnotationLabel(name ?? "", first, last, instantiationType);
    }

    internal static SpanResolution ClassifySpan(AnnotationLabel label, int sourceLength)
    {
        if (label.Start is null && label.End is null) return SpanResolution.NoSpan;
        if (label.Start is not { } first || label.End is not { } last) return SpanResolution.Incomplete;
        if (first < 0 || last < 0) return SpanResolution.OutOfRange;
        if (last < first) return SpanResolution.Reversed;
        return last >= sourceLength ? SpanResolution.OutOfRange : SpanResolution.Resolved;
    }

    private static List<int> SourceBoundaries(string sentence)
    {
        // Keep the original XML text; source positions count Unicode scalars,
        // whereas .NET substring positions count UTF-16 code units.
        var boundaries = new List<int>(sentence.Length + 1) { 0 };
        foreach (Rune rune in sentence.EnumerateRunes())
            boundaries.Add(boundaries[^1] + rune.Utf16SequenceLength);
        return boundaries;
    }

    internal static string ReadResolvedSpan(string sentence, AnnotationLabel label) =>
        ReadResolvedSpan(sentence, SourceBoundaries(sentence), label);

    private static string ReadResolvedSpan(string sentence, IReadOnlyList<int> boundaries, AnnotationLabel label)
    {
        SpanResolution resolution = ClassifySpan(label, boundaries.Count - 1);
        if (resolution != SpanResolution.Resolved)
            throw new FormatException($"FrameNet label span is not resolved: {resolution} "
                + $"(start={label.Start}, end={label.End}, source length={boundaries.Count - 1})");
        return sentence[boundaries[label.Start!.Value]..boundaries[label.End!.Value + 1]];
    }

    private static IEnumerable<AnnotationLabel> TargetLabels(IReadOnlyList<AnnotationLayer> layers) =>
        layers.Where(layer => layer.Name == "Target").SelectMany(layer => layer.Labels)
            .Where(label => label.Name == "Target");

    private static bool ContainsUnresolvedSpans(IReadOnlyList<AnnotationLayer> layers, int sourceLength) =>
        layers.SelectMany(layer => layer.Labels).Any(label =>
            ClassifySpan(label, sourceLength) is not (SpanResolution.Resolved or SpanResolution.NoSpan))
        || TargetLabels(layers).Any(label => ClassifySpan(label, sourceLength) != SpanResolution.Resolved);

    internal static FulltextAnno? CreateAnnotation(
        string sentence, string? frameName, IReadOnlyList<AnnotationLayer> layers,
        string fileLabel, string sentenceReference, string annotationReference, string? status)
    {
        if (string.IsNullOrEmpty(frameName) || sentence.Length == 0) return null;
        var boundaries = SourceBoundaries(sentence);
        int sourceLength = boundaries.Count - 1;
        AnnotationLabel[] targets = TargetLabels(layers)
            .OrderBy(label => label.Start).ThenBy(label => label.End).ToArray();
        bool resolvedTarget = targets.Length > 0 &&
            targets.All(label => ClassifySpan(label, sourceLength) == SpanResolution.Resolved);
        bool unresolved = ContainsUnresolvedSpans(layers, sourceLength);
        if (targets.Length == 0 && !unresolved) return null;

        // An invalid segment prevents resolution of the complete target. Every
        // raw segment remains in Layers; never silently choose its valid subset.
        TargetSpan[] spans = resolvedTarget
            ? targets.Select(label => new TargetSpan(label.Start!.Value, label.End!.Value)).ToArray()
            : [];
        string? targetText = resolvedTarget
            ? string.Join(' ', targets.Select(label => ReadResolvedSpan(sentence, boundaries, label).Trim()).ToArray())
            : null;
        return new FulltextAnno(sentence, targetText, frameName,
            spans, fileLabel, sentenceReference, annotationReference, status, layers);
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

    public readonly record struct TargetSpan(int Start, int End);

    public sealed record AnnotationLabel(
        string Name, int? Start, int? End, string? InstantiationType);

    public sealed record AnnotationLayer(string Name, string? Rank, List<AnnotationLabel> Labels);

    public sealed record FulltextAnno(
        string Sentence,
        string? TargetText,
        string FrameName,
        IReadOnlyList<TargetSpan> TargetSpans,
        string FileLabel,
        string SentenceReference,
        string AnnotationReference,
        string? Status,
        IReadOnlyList<AnnotationLayer> Layers)
    {
        // Compatibility accessors for the original single-span shape. For a
        // discontinuous target these name its first textual segment; TargetSpans is
        // authoritative and preserves every segment.
        public bool HasUnresolvedSpans =>
            ContainsUnresolvedSpans(Layers, Sentence.EnumerateRunes().Count());

        public bool HasResolvedTarget
        {
            get
            {
                if (TargetText is null || TargetSpans.Count == 0) return false;
                int sourceLength = Sentence.EnumerateRunes().Count();
                return TargetLabels(Layers).All(label => ClassifySpan(label, sourceLength) == SpanResolution.Resolved);
            }
        }

        public int TargetStart => HasResolvedTarget ? TargetSpans[0].Start
            : throw new InvalidOperationException("FrameNet annotation has no resolved target span");
        public int TargetEnd => HasResolvedTarget ? TargetSpans[0].End
            : throw new InvalidOperationException("FrameNet annotation has no resolved target span");
    }
}
