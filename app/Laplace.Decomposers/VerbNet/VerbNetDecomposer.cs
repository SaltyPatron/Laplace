using System.Runtime.CompilerServices;
using System.Xml;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.VerbNet;

public sealed class VerbNetDecomposer : IDecomposer
{







    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/VerbNetDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    private static readonly Hash128 ClassTypeId = EntityTypeRegistry.VerbNetClass;



    public Hash128 SourceId => Source;
    public string SourceName => "VerbNetDecomposer";
    public int LayerOrder => 2;
    public Hash128 TrustClassId => TrustClass;

    private const long EstimatedClasses = 329L;

    public Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            typeNodeNames: ["VerbNet_Class"],
            relationNodeNames: ["IS_A", "MEMBER_OF_VERBNET_CLASS", "HAS_THEMATIC_ROLE",
                "HAS_VERB_FRAME", "HAS_EXAMPLE", "CORRESPONDS_TO", "EVOKES_FRAME", "HAS_NAME_ALIAS"],
            ct: ct);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string classDir = DecomposerFileDiscovery.ResolveSubdir(
            context.EcosystemPath, "*.xml",
            Path.Combine("verbnet-master", "verbnet3.4"), "verbnet3.4");
        int batch = options.BatchSize > 1 ? options.BatchSize : 4096;

        await foreach (var change in DecomposerBatch.RunAsync(
            ParseVnClassesAsync(classDir, ct),
            static (root, b) => EmitClass(b, root, parentClassId: null),
            Source, "verbnet", batch, context.Reader, options, ct))
            yield return change;
    }

    private static async IAsyncEnumerable<XmlElement> ParseVnClassesAsync(
        string classDir, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var file in EnumerateClassFiles(classDir))
        {
            ct.ThrowIfCancellationRequested();
            var doc = new XmlDocument();
            try { doc.Load(file); }
            catch (XmlException) { continue; }
            var root = doc.DocumentElement;
            if (root is null || !root.Name.Equals("VNCLASS", StringComparison.Ordinal)) continue;
            yield return root;
        }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(EstimatedClasses);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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

        foreach (var member in ChildElements(el, "MEMBERS", "MEMBER"))
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

        foreach (var role in ChildElements(el, "THEMROLES", "THEMROLE"))
        {
            string type = role.GetAttribute("type").Trim();
            if (type.Length == 0) continue;
            var roleId = ContentEmitter.Emit(b, type, Source);
            if (roleId is null) continue;
            b.AddAttestation(NativeAttestation.Categorical(
                classEntity, "HAS_THEMATIC_ROLE", roleId.Value, Source, TC.AcademicCurated));
        }

        foreach (var frame in ChildElements(el, "FRAMES", "FRAME"))
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

        foreach (var subWrap in DirectChildren(el, "SUBCLASSES"))
            foreach (var sub in DirectChildren(subWrap, "VNSUBCLASS"))
                EmitClass(b, sub, parentClassId: classId);
    }


    private static IEnumerable<string> EnumerateClassFiles(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.xml")
                                   .OrderBy(p => p, StringComparer.Ordinal))
            yield return f;
    }

    private static IEnumerable<XmlElement> DirectChildren(XmlElement el, string name)
    {
        foreach (XmlNode child in el.ChildNodes)
            if (child is XmlElement ce && ce.Name.Equals(name, StringComparison.Ordinal))
                yield return ce;
    }

    private static IEnumerable<XmlElement> ChildElements(XmlElement el, string wrapper, string item)
    {
        foreach (var w in DirectChildren(el, wrapper))
            foreach (var it in DirectChildren(w, item))
                yield return it;
    }
}
