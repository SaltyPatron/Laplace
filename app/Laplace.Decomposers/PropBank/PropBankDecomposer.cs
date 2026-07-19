using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Xml;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.PropBank;

public sealed class PropBankDecomposer : ComposeDecomposer<XmlElement, PropBankSource, FullScope>
{






    public static readonly Hash128 Source = PropBankSource.SourceId;
    public static readonly Hash128 TrustClass = PropBankSource.TrustClass;

    private static readonly Hash128 RolesetTypeId = EntityTypeRegistry.PropBankRoleset;
    private static readonly Hash128 OrdinalTypeId = EntityTypeRegistry.Ordinal;




    internal static Hash128 OrdinalId(string n) => Hash128.OfCanonical($"ordinal/{n}/v1");

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.AcademicCurated;
    protected override string BatchLabelPrefix => "propbank";
    protected override int DefaultBatchSize => BatchConfigDefaults.HighVolume;


    private const long EstimatedFramesets = 7_567L;

    private static readonly ConcurrentDictionary<string, byte> _canonicalNames = new(StringComparer.Ordinal);

    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames.Keys.ToArray();

    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => _canonicalNames;

    protected override async IAsyncEnumerable<XmlElement> ExtractRecordsAsync(
        string ecosystemPath, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string framesDir = DecomposerFileDiscovery.ResolveSubdir(
            ecosystemPath, "*.xml",
            Path.Combine("propbank-frames-main", "frames"), "frames");
        await foreach (var root in SharedXmlFramesetReader.ReadRootsAsync(
                           SharedXmlFramesetReader.EnumerateFramesetFiles(framesDir, ecosystemPath),
                           "frameset", ct))
            yield return root;
    }

    protected override void Compose(XmlElement root, SubstrateChangeBuilder b) => ComposeFrameset(root, b);

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(EstimatedFramesets);

    private static void ComposeFrameset(XmlElement root, SubstrateChangeBuilder b)
    {
        foreach (XmlNode pNode in root.GetElementsByTagName("predicate"))
            if (pNode is XmlElement predicate)
                EmitPredicate(b, predicate);
    }

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
        foreach (var role in SharedXmlFramesetReader.DescendantElements(roleset, "role"))
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

            foreach (var link in SharedXmlFramesetReader.DescendantElements(role, "rolelink"))
            {
                string resource = link.GetAttribute("resource");
                string cls = link.GetAttribute("class").Trim();
                string inner = link.InnerText.Trim();
                if (cls.Length == 0) continue;












                Hash128? anchor =
                    resource.Equals("VerbNet", StringComparison.OrdinalIgnoreCase)
                        ? CategoryAnchor.Id(SourceEntityIdConventions.NumericVerbNetClassId(cls))
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
        foreach (var example in SharedXmlFramesetReader.DescendantElements(roleset, "example"))
            foreach (var text in SharedXmlFramesetReader.DescendantElements(example, "text"))
            {
                string ex = text.InnerText.Trim();
                if (ex.Length == 0) continue;
                var exId = ContentEmitter.Emit(b, ex, Source);
                if (exId is not null)
                    b.AddAttestation(NativeAttestation.Categorical(
                        rsEntity, "HAS_EXAMPLE", exId.Value, Source, TC.AcademicCurated));
            }
    }










}
