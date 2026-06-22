using System.Runtime.CompilerServices;
using System.Xml;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.VerbNet;

public sealed class VerbNetDecomposer : IDecomposer{
    // Each unit's rows are self-contained. VerbNet owns its classes (it emits + types them); the
    // IS_A parent-class edge, MEMBER_OF_VERBNET_CLASS lemma membership, and the WordNet-sense
    // CORRESPONDS_TO edge resolve by content-addressed
    // id against entities owned by VerbNet (the parent class, in another batch) and WordNet (the
    // sense, in another source). With the per-batch referential EXISTS pre-check gone these
    // cross-batch / cross-source anchors are legal, so N workers can commit batches concurrently.

    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/VerbNetDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    private static readonly Hash128 ClassTypeId = EntityTypeRegistry.VerbNetClass;
    // WordNet_Sense correspondence targets are now anchored by content-addressed id (CategoryAnchor.Id)
    // and typed by the WordNet decomposer, so no WordNet_Sense type id is referenced here.

    internal static string NumericClassId(string classId) =>
        SourceEntityIdConventions.NumericVerbNetClassId(classId);

    public Hash128 SourceId     => Source;
    public string  SourceName   => "VerbNetDecomposer";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    private const long EstimatedClasses = 329L;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("VerbNet_Class");
        boot.AddRelationType("IS_A");
        boot.AddRelationType("MEMBER_OF_VERBNET_CLASS");
        boot.AddRelationType("HAS_THEMATIC_ROLE");
        boot.AddRelationType("HAS_VERB_FRAME");
        boot.AddRelationType("HAS_EXAMPLE");
        boot.AddRelationType("CORRESPONDS_TO");
        boot.AddRelationType("EVOKES_FRAME");
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string classDir = ResolveClassDir(context.EcosystemPath);
        int batch = options.BatchSize > 1 ? options.BatchSize : 4096;

        var b = NewBuilder("verbnet/batch-0", batch);
        int n = 0, bn = 0;

        foreach (var file in EnumerateClassFiles(classDir))
        {
            ct.ThrowIfCancellationRequested();
            var doc = new XmlDocument();
            try { doc.Load(file); }
            catch (XmlException) { continue; }
            var root = doc.DocumentElement;
            if (root is null || !root.Name.Equals("VNCLASS", StringComparison.Ordinal)) continue;

            EmitClass(b, root, parentClassId: null);

            if (++n >= batch)
            {
                if (!options.DryRun) yield return b.Build();
                b = NewBuilder($"verbnet/batch-{++bn}", batch);
                n = 0; await Task.Yield();
            }
        }
        if (n > 0 && !options.DryRun) yield return b.Build();
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(EstimatedClasses);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void EmitClass(SubstrateChangeBuilder b, XmlElement el, string? parentClassId)
    {
        string? classId = el.GetAttribute("ID");
        if (string.IsNullOrEmpty(classId)) return;

        
        
        Hash128? classAnchor = CategoryAnchor.Emit(b, NumericClassId(classId), ClassTypeId, Source, TC.AcademicCurated);
        if (classAnchor is null) return;
        Hash128 classEntity = classAnchor.Value;

        if (parentClassId is not null)
        {
            // The parent class is itself a VerbNet class; its own EmitClass call writes and types its
            // entity row (this same batch for an enclosing class, or another batch for a top-level
            // parent). We only need its content-addressed id to anchor the IS_A edge — pre-emitting a
            // typed anchor here was solely to satisfy the deleted referential EXISTS pre-check.
            Hash128? parentAnchor = CategoryAnchor.Id(NumericClassId(parentClassId));
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
        }

        foreach (var subWrap in DirectChildren(el, "SUBCLASSES"))
            foreach (var sub in DirectChildren(subWrap, "VNSUBCLASS"))
                EmitClass(b, sub, parentClassId: classId);
    }

    private static SubstrateChangeBuilder NewBuilder(string unit, int batch) =>
        new(Source, unit, null,
            entityCapacity:      batch * 64,
            physicalityCapacity: batch * 64,
            attestationCapacity: batch * 32);

    private static string ResolveClassDir(string ecosystemPath)
    {
        foreach (var c in new[]
                 {
                     Path.Combine(ecosystemPath, "verbnet-master", "verbnet3.4"),
                     Path.Combine(ecosystemPath, "verbnet3.4"),
                     ecosystemPath,
                 })
            if (Directory.Exists(c) &&
                Directory.EnumerateFiles(c, "*.xml").Any())
                return c;
        return ecosystemPath;
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
