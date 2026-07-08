using System.Runtime.CompilerServices;
using System.Xml;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.VerbNet;

public sealed class VerbNetDecomposer : ComposeDecomposer<XmlElement>
{







    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/VerbNetDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    private static readonly Hash128 ClassTypeId = EntityTypeRegistry.VerbNetClass;



    private const long EstimatedClasses = 329L;

    public override Hash128 SourceId => Source;
    public override string SourceName => "VerbNetDecomposer";
    public override int LayerOrder => 2;
    public override Hash128 TrustClassId => TrustClass;
    protected override double SourceTrust => TC.AcademicCurated;
    protected override string BatchLabelPrefix => "verbnet";
    protected override int DefaultBatchSize => BatchConfigDefaults.HighVolume;

    public override Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            typeNodeNames: ["VerbNet_Class"],
            relationNodeNames: ["IS_A", "MEMBER_OF_VERBNET_CLASS", "HAS_THEMATIC_ROLE",
                "HAS_VERB_FRAME", "HAS_EXAMPLE", "CORRESPONDS_TO", "EVOKES_FRAME", "HAS_NAME_ALIAS"],
            ct: ct);

    protected override async IAsyncEnumerable<XmlElement> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string classDir = DecomposerFileDiscovery.ResolveSubdir(
            ecosystemPath, "*.xml",
            Path.Combine("verbnet-master", "verbnet3.4"), "verbnet3.4");
        await foreach (var root in SharedXmlFramesetReader.ReadRootsAsync(
                           SharedXmlFramesetReader.EnumerateXmlFiles(classDir), "VNCLASS", ct))
            yield return root;
    }

    protected override void Compose(XmlElement root, SubstrateChangeBuilder b) =>
        EmitClass(b, root, parentClassId: null);

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(EstimatedClasses);

    private static void EmitClass(SubstrateChangeBuilder b, XmlElement el, string? parentClassId)
    {
        string? classId = el.GetAttribute("ID");
        if (string.IsNullOrEmpty(classId)) return;



        Hash128? classAnchor = CategoryAnchor.Emit(b, SourceEntityIdConventions.NumericVerbNetClassId(classId), ClassTypeId, Source, TC.AcademicCurated);
        if (classAnchor is null) return;
        Hash128 classEntity = classAnchor.Value;



        if (ContentEmitter.Emit(b, classId, Source) is { } classNameId)
            b.AddAttestation(NativeAttestation.Categorical(
                classEntity, "HAS_NAME_ALIAS", classNameId, Source, TC.AcademicCurated));

        if (parentClassId is not null)
        {




            Hash128? parentAnchor = CategoryAnchor.Id(SourceEntityIdConventions.NumericVerbNetClassId(parentClassId));
            if (parentAnchor is not null)
                b.AddAttestation(NativeAttestation.Categorical(
                    classEntity, "IS_A", parentAnchor.Value, Source, TC.AcademicCurated));
        }

        foreach (var member in SharedXmlFramesetReader.ChildElements(el, "MEMBERS", "MEMBER"))
        {
            string name = member.GetAttribute("name").Replace('_', ' ').Trim();
            if (name.Length == 0) continue;
            var lemmaId = ContentEmitter.Emit(b, name, Source);
            if (lemmaId is null) continue;
            b.AddAttestation(NativeAttestation.Categorical(
                lemmaId.Value, "MEMBER_OF_VERBNET_CLASS", classEntity, Source, TC.AcademicCurated));

            string wn = member.GetAttribute("wn");
            if (wn.Length > 0)
                foreach (var raw in wn.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    string? key = SourceEntityIdConventions.NormalizeSenseKey(raw);
                    if (key is null) continue;
                    var senseEntity = SenseAnchor.Id(key);
                    if (senseEntity is null) continue;
                    b.AddAttestation(NativeAttestation.Categorical(
                        lemmaId.Value, "CORRESPONDS_TO", senseEntity.Value, Source, TC.AcademicCurated));
                }

            string fnframe = member.GetAttribute("fnframe").Trim();
            if (fnframe.Length > 0)
            {
                var frameId = CategoryAnchor.Id(fnframe);
                if (frameId is not null)
                    b.AddAttestation(NativeAttestation.Categorical(
                        lemmaId.Value, "EVOKES_FRAME", frameId.Value, Source, TC.AcademicCurated));
            }
        }

        foreach (var role in SharedXmlFramesetReader.ChildElements(el, "THEMROLES", "THEMROLE"))
        {
            string type = role.GetAttribute("type").Trim();
            if (type.Length == 0) continue;
            var roleId = ContentEmitter.Emit(b, type, Source);
            if (roleId is null) continue;
            b.AddAttestation(NativeAttestation.Categorical(
                classEntity, "HAS_THEMATIC_ROLE", roleId.Value, Source, TC.AcademicCurated));
        }

        foreach (var frame in SharedXmlFramesetReader.ChildElements(el, "FRAMES", "FRAME"))
        {
            string primary = "";
            foreach (XmlNode d in frame.GetElementsByTagName("DESCRIPTION"))
            {
                if (d is XmlElement de) primary = de.GetAttribute("primary").Trim();
                break;
            }
            if (primary.Length > 0)
            {
                var frameId = ContentEmitter.Emit(b, primary, Source);
                if (frameId is not null)
                    b.AddAttestation(NativeAttestation.Categorical(
                        classEntity, "HAS_VERB_FRAME", frameId.Value, Source, TC.AcademicCurated));
            }

            foreach (XmlNode exNode in frame.GetElementsByTagName("EXAMPLE"))
            {
                string ex = exNode.InnerText.Trim();
                if (ex.Length == 0) continue;
                var exId = ContentEmitter.Emit(b, ex, Source);
                if (exId is not null)
                    b.AddAttestation(NativeAttestation.Categorical(
                        classEntity, "HAS_EXAMPLE", exId.Value, Source, TC.AcademicCurated,
                        contextId: classEntity));
            }





            foreach (XmlNode semNode in frame.GetElementsByTagName("SEMANTICS"))
            {
                if (semNode is not XmlElement sem) continue;
                foreach (XmlNode predNode in sem.GetElementsByTagName("PRED"))
                {
                    if (predNode is not XmlElement pred) continue;
                    string predVal = pred.GetAttribute("value").Trim();
                    if (predVal.Length == 0) continue;
                    var predId = ContentEmitter.Emit(b, predVal, Source);
                    if (predId is null) continue;
                    b.AddAttestation(NativeAttestation.Categorical(
                        classEntity, "ENTAILS", predId.Value, Source, TC.AcademicCurated));
                    foreach (XmlNode argNode in pred.GetElementsByTagName("ARG"))
                    {
                        if (argNode is not XmlElement arg) continue;
                        if (!arg.GetAttribute("type").Trim().Equals("ThemRole", StringComparison.OrdinalIgnoreCase))
                            continue;
                        string roleVal = arg.GetAttribute("value").Trim().TrimStart('?');
                        if (roleVal.Length == 0) continue;
                        var roleId = ContentEmitter.Emit(b, roleVal, Source);
                        if (roleId is not null)
                            b.AddAttestation(NativeAttestation.Categorical(
                                predId.Value, "HAS_SEMANTIC_ROLE", roleId.Value, Source, TC.AcademicCurated));
                    }
                }
            }
        }

        foreach (var subWrap in SharedXmlFramesetReader.DirectChildren(el, "SUBCLASSES"))
            foreach (var sub in SharedXmlFramesetReader.DirectChildren(subWrap, "VNSUBCLASS"))
                EmitClass(b, sub, parentClassId: classId);
    }
}
