using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Xml;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.PropBank;

public sealed class PropBankDecomposer : IDecomposer
{






    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/PropBankDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    private static readonly Hash128 RolesetTypeId = EntityTypeRegistry.PropBankRoleset;
    private static readonly Hash128 OrdinalTypeId = EntityTypeRegistry.Ordinal;




    internal static string NumericClassId(string classId) =>
        SourceEntityIdConventions.NumericVerbNetClassId(classId);

    internal static Hash128 OrdinalId(string n) => Hash128.OfCanonical($"ordinal/{n}/v1");

    public Hash128 SourceId => Source;
    public string SourceName => "PropBankDecomposer";
    public int LayerOrder => 2;
    public Hash128 TrustClassId => TrustClass;


    private const long EstimatedFramesets = 7_567L;

    private static readonly ConcurrentDictionary<string, byte> _canonicalNames = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames.Keys.ToArray();

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default) =>
        await SourceVocabularyBootstrap.RegisterAsync(context, Source, SourceName, TrustClass,
            typeNodeNames: ["PropBank_Roleset", "VerbNet_Class", "FrameNet_Frame", "Ordinal"],
            relationNodeNames: ["HAS_SENSE", "HAS_DEFINITION", "HAS_SEMANTIC_ROLE", "HAS_EXAMPLE",
                "CORRESPONDS_TO", "ROLE_CORRESPONDS_TO", "HAS_FEATURE"],
            readbackNames: _canonicalNames, ct: ct);

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string framesDir = ResolveFramesDir(context.EcosystemPath);
        int batch = options.BatchSize > 1 ? options.BatchSize : 4096;

        await foreach (var change in DecomposerBatch.RunAsync(
            ParseFramesetsAsync(framesDir, context.EcosystemPath, ct),
            static (root, b) => ComposeFrameset(root, b),
            Source, "propbank", batch, context.Reader, options, ct))
            yield return change;
    }

    private static async IAsyncEnumerable<XmlElement> ParseFramesetsAsync(
        string framesDir, string ecosystemPath, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var file in EnumerateFramesetFiles(framesDir, ecosystemPath))
        {
            ct.ThrowIfCancellationRequested();
            var doc = new XmlDocument();
            try { doc.Load(file); }
            catch (XmlException) { continue; }
            var root = doc.DocumentElement;
            if (root is null || !root.Name.Equals("frameset", StringComparison.Ordinal)) continue;
            yield return root;
        }
    }

    private static void ComposeFrameset(XmlElement root, SubstrateChangeBuilder b)
    {
        foreach (XmlNode pNode in root.GetElementsByTagName("predicate"))
            if (pNode is XmlElement predicate)
                EmitPredicate(b, predicate);
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(EstimatedFramesets);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void EmitPredicate(SubstrateChangeBuilder b, XmlElement predicate)
    {
        string lemma = predicate.GetAttribute("lemma").Replace('_', ' ').Trim();
        if (lemma.Length == 0) return;
        var lemmaId = ContentEmitter.Emit(b, lemma, Source);
        if (lemmaId is null) return;

        foreach (XmlNode rNode in predicate.GetElementsByTagName("roleset"))
        {
            if (rNode is not XmlElement roleset) continue;
            string rsId = roleset.GetAttribute("id").Trim();
            if (rsId.Length == 0) continue;



            Hash128? rsAnchor = CategoryAnchor.Emit(b, rsId, RolesetTypeId, Source, TC.AcademicCurated);
            if (rsAnchor is null) continue;
            Hash128 rsEntity = rsAnchor.Value;

            b.AddAttestation(NativeAttestation.Categorical(
                lemmaId.Value, "HAS_SENSE", rsEntity, Source, TC.AcademicCurated));

            string name = roleset.GetAttribute("name").Trim();
            if (name.Length > 0)
            {
                var defId = ContentEmitter.Emit(b, name, Source);
                if (defId is not null)
                    b.AddAttestation(NativeAttestation.Categorical(
                        rsEntity, "HAS_DEFINITION", defId.Value, Source, TC.AcademicCurated));
            }

            EmitRoles(b, roleset, rsEntity);
            EmitExamples(b, roleset, rsEntity);
        }
    }

    private static void EmitRoles(SubstrateChangeBuilder b, XmlElement roleset, Hash128 rsEntity)
    {
        foreach (var role in DescendantElements(roleset, "role"))
        {
            string descr = role.GetAttribute("descr").Trim();
            string num = role.GetAttribute("n").Trim();
            if (descr.Length == 0) continue;
            var roleId = ContentEmitter.Emit(b, descr, Source);
            if (roleId is null) continue;

            Hash128? ctx = null;
            if (num.Length > 0)
            {
                string ord = num.Equals("M", StringComparison.OrdinalIgnoreCase) ? "m" : num;
                _canonicalNames.TryAdd($"ordinal/{ord}/v1", 0);
                Hash128 ordEntity = OrdinalId(ord);
                b.AddEntity(new EntityRow(ordEntity, EntityTier.Word, OrdinalTypeId, Source));
                ctx = ordEntity;
            }

            b.AddAttestation(NativeAttestation.Categorical(
                rsEntity, "HAS_SEMANTIC_ROLE", roleId.Value, Source, TC.AcademicCurated,
                contextId: ctx));



            string func = role.GetAttribute("f").Trim();
            if (func.Length > 0)
            {
                var funcId = ContentEmitter.Emit(b, func, Source);
                if (funcId is not null)
                    b.AddAttestation(NativeAttestation.Categorical(
                        roleId.Value, "HAS_FEATURE", funcId.Value, Source, TC.AcademicCurated));
            }

            foreach (var link in DescendantElements(role, "rolelink"))
            {
                string resource = link.GetAttribute("resource");
                string cls = link.GetAttribute("class").Trim();
                string inner = link.InnerText.Trim();
                if (cls.Length == 0) continue;












                Hash128? anchor =
                    resource.Equals("VerbNet", StringComparison.OrdinalIgnoreCase)
                        ? CategoryAnchor.Id(NumericClassId(cls))
                    : resource.Equals("FrameNet", StringComparison.OrdinalIgnoreCase)
                        ? CategoryAnchor.Id(cls)
                        : null;
                if (anchor is null) continue;
                Hash128 classEntity = anchor.Value;
                b.AddAttestation(NativeAttestation.Categorical(
                    rsEntity, "CORRESPONDS_TO", classEntity, Source, TC.AcademicCurated));

                if (inner.Length > 0)
                {
                    var thetaId = ContentEmitter.Emit(b, inner, Source);
                    if (thetaId is not null)
                        b.AddAttestation(NativeAttestation.Categorical(
                            roleId.Value, "ROLE_CORRESPONDS_TO", thetaId.Value, Source, TC.AcademicCurated,
                            contextId: classEntity));
                }
            }
        }
    }

    private static void EmitExamples(SubstrateChangeBuilder b, XmlElement roleset, Hash128 rsEntity)
    {
        foreach (var example in DescendantElements(roleset, "example"))
            foreach (var text in DescendantElements(example, "text"))
            {
                string ex = text.InnerText.Trim();
                if (ex.Length == 0) continue;
                var exId = ContentEmitter.Emit(b, ex, Source);
                if (exId is not null)
                    b.AddAttestation(NativeAttestation.Categorical(
                        rsEntity, "HAS_EXAMPLE", exId.Value, Source, TC.AcademicCurated));
            }
    }


    private static IEnumerable<XmlElement> DescendantElements(XmlElement el, string name)
    {
        foreach (XmlNode node in el.GetElementsByTagName(name))
            if (node is XmlElement ce) yield return ce;
    }

    private static string ResolveFramesDir(string ecosystemPath)
    {
        foreach (var c in new[]
                 {
                     Path.Combine(ecosystemPath, "propbank-frames-main", "frames"),
                     Path.Combine(ecosystemPath, "frames"),
                     ecosystemPath,
                 })
            if (Directory.Exists(c) && Directory.EnumerateFiles(c, "*.xml").Any())
                return c;
        return ecosystemPath;
    }









    private static IEnumerable<string> EnumerateFramesetFiles(string framesDir, string ecosystemPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? parent = string.Equals(
            Path.GetFullPath(framesDir).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(ecosystemPath).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase)
            ? null
            : Path.GetDirectoryName(framesDir);

        foreach (var d in new[] { framesDir, parent })
        {
            if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
            foreach (var f in Directory.EnumerateFiles(d, "*.xml")
                                       .OrderBy(p => p, StringComparer.Ordinal))
                if (seen.Add(Path.GetFullPath(f)))
                    yield return f;
        }
    }
}
