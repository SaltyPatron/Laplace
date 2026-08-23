using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Xml;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.PropBank;

public sealed class PropBankDecomposer
    : ComposeDecomposerMultiFile<XmlElement, PropBankSource, FullScope>, IIngestInventoryProvider
{






    public static readonly Hash128 Source = PropBankSource.SourceId;
    public static readonly Hash128 TrustClass = PropBankSource.TrustClass;

    private static readonly Hash128 RolesetTypeId = EntityTypeRegistry.PropBankRoleset;
    private static readonly Hash128 OrdinalTypeId = EntityTypeRegistry.Ordinal;




    internal static Hash128 OrdinalId(string n) => Hash128.OfCanonical($"ordinal/{n}/v1");

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.AcademicCurated;
    protected override string BatchLabelPrefix => "propbank";


    private const long EstimatedFramesets = 7_567L;

    private static readonly ConcurrentDictionary<string, byte> _canonicalNames = new(StringComparer.Ordinal);

    public override IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames.Keys.ToArray();

    protected override ConcurrentDictionary<string, byte>? VocabularyReadback => _canonicalNames;

    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options)
    {
        string framesDir = IngestInput.ResolveSubdir(
            ecosystemPath, "*.xml",
            Path.Combine("propbank-frames-main", "frames"), "frames");
        return SharedXmlFramesetReader.EnumerateFramesetFiles(framesDir, ecosystemPath)
            .Select((file, i) => (file, $"propbank/{i}/{Path.GetFileName(file)}"))
            .ToList();
    }

    protected override async IAsyncEnumerable<XmlElement> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var root in SharedXmlFramesetReader.ReadRootsAsync(
                           [filePath],
                           "frameset", ct))
            yield return root;
    }

    protected override void Compose(XmlElement root, SubstrateChangeBuilder b) => ComposeFrameset(root, b);

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(EstimatedFramesets);

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = ListFiles(context.EcosystemPath, options).Select(x => x.Path).ToList();
        return Task.FromResult(IngestInventory.FromFileUnits(
            "framesets", paths, options.MaxInputUnits, tracksFileCompletion: true));
    }

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



            Hash128? rsAnchor = AnchorAdmission.Emit(
                b, rsId, RolesetTypeId, Source, TC.AcademicCurated);
            if (rsAnchor is null) continue;
            Hash128 rsEntity = rsAnchor.Value;

            b.AddAttestation(NativeAttestation.Categorical(
                lemmaId.Value, "HAS_SENSE", rsEntity, Source, TC.AcademicCurated));

            string name = roleset.GetAttribute("name").Trim();
            if (name.Length > 0)
            {
                var defId = ContentEmitter.Emit(b, name, Source);
                if (defId is not null)
                    b.AddAttestation(NativeAttestation.CategoricalResolved(
                        rsEntity, PropBankSource.HasDefinitionTypeId, defId.Value,
                        Source, null, TC.AcademicCurated));
            }

            EmitRoles(b, roleset, rsEntity);
            EmitExamples(b, roleset, rsEntity);
        }
    }

    /// <summary>
    /// Resolve a lexlink/rolelink target to its class entity. One body so the roleset-level
    /// mapping and the role-level one cannot disagree about what a class name addresses.
    /// </summary>
    private static Hash128? ResolveLinkedClass(string resource, string cls)
        => cls.Length == 0
            ? null
            : resource.Equals("VerbNet", StringComparison.OrdinalIgnoreCase)
                ? AnchorAdmission.Id(
                    SourceEntityIdConventions.NumericVerbNetClassId(cls),
                    EntityTypeRegistry.VerbNetClass)
                : resource.Equals("FrameNet", StringComparison.OrdinalIgnoreCase)
                    ? CategoryAnchor.Id(cls)
                    : null;

    /// <summary>
    /// &lt;lexlinks&gt; carries the ONLY explicit graded confidence in this corpus --
    /// 16,250 rows at 0.8 or 1.0 -- and the element was never read at all. The roleset's
    /// mapping to a FrameNet frame or VerbNet class was therefore either absent, or
    /// present via &lt;rolelink&gt; at a flat unscored 1.0, discarding a hand-curated
    /// distinction the source went to the trouble of recording.
    ///
    /// Emitted BEFORE the role pass and sharing its dedup set, so the confidence-bearing
    /// witness is the one that lands and rolelink does not overwrite it with a constant.
    /// </summary>
    private static void EmitLexLinks(
        SubstrateChangeBuilder b, XmlElement roleset, Hash128 rsEntity,
        HashSet<Hash128> linkedClasses)
    {
        foreach (var link in SharedXmlFramesetReader.DescendantElements(roleset, "lexlink"))
        {
            Hash128? anchor = ResolveLinkedClass(
                link.GetAttribute("resource"), link.GetAttribute("class").Trim());
            if (anchor is null || !linkedClasses.Add(anchor.Value)) continue;

            // arenaScale 1.0 keeps the confidence on its own natural scale: 0.8 and 1.0
            // land at distinct scores instead of collapsing onto the categorical constant.
            double confidence =
                double.TryParse(link.GetAttribute("confidence"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double c)
                && c > 0.0 ? c : 1.0;

            b.AddAttestation(NativeAttestation.ResolvedScored(
                rsEntity, PropBankSource.CorrespondsToTypeId, anchor.Value,
                Source, null, TC.AcademicCurated,
                signedMagnitude: confidence, arenaScale: 1.0));
        }
    }

    private static void EmitRoles(SubstrateChangeBuilder b, XmlElement roleset, Hash128 rsEntity)
    {
        var linkedClasses = new HashSet<Hash128>();
        EmitLexLinks(b, roleset, rsEntity, linkedClasses);
        foreach (var role in SharedXmlFramesetReader.DescendantElements(roleset, "role"))
        {
            string descr = role.GetAttribute("descr").Trim();
            string num = role.GetAttribute("n").Trim();
            if (descr.Length == 0) continue;
            var roleLabelId = ContentEmitter.Emit(b, descr, Source);
            if (roleLabelId is null) continue;

            string roleKey = num.Length > 0
                ? $"ARG{(num.Equals("M", StringComparison.OrdinalIgnoreCase) ? "M" : num)}"
                : descr;
            Hash128? roleAnchor = RoleAnchor.Emit(
                b, RoleIdentityKind.PropBank, rsEntity, roleKey,
                EntityTypeRegistry.PropBankRole, Source, TC.AcademicCurated);
            if (roleAnchor is null) continue;
            Hash128 roleEntity = roleAnchor.Value;

            b.AddAttestation(NativeAttestation.CategoricalResolved(
                rsEntity, PropBankSource.HasSemanticRoleTypeId, roleEntity,
                Source, null, TC.AcademicCurated));
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                roleEntity, PropBankSource.HasDefinitionTypeId, roleLabelId.Value,
                Source, null, TC.AcademicCurated));

            if (num.Length > 0)
            {
                string ord = num.Equals("M", StringComparison.OrdinalIgnoreCase) ? "m" : num;
                _canonicalNames.TryAdd($"ordinal/{ord}/v1", 0);
                Hash128 ordEntity = OrdinalId(ord);
                b.AddEntity(new EntityRow(ordEntity, EntityTier.Word, OrdinalTypeId, Source));
                b.AddAttestation(NativeAttestation.CategoricalResolved(
                    roleEntity, PropBankSource.HasFeatureTypeId, ordEntity,
                    Source, null, TC.AcademicCurated));
            }



            string func = role.GetAttribute("f").Trim();
            if (func.Length > 0)
            {
                var funcId = ContentEmitter.Emit(b, func, Source);
                if (funcId is not null)
                    b.AddAttestation(NativeAttestation.CategoricalResolved(
                        roleEntity, PropBankSource.HasFeatureTypeId, funcId.Value,
                        Source, null, TC.AcademicCurated));
            }

            foreach (var link in SharedXmlFramesetReader.DescendantElements(role, "rolelink"))
            {
                string resource = link.GetAttribute("resource");
                string cls = link.GetAttribute("class").Trim();
                string inner = link.InnerText.Trim();
                if (cls.Length == 0) continue;












                Hash128? anchor = ResolveLinkedClass(resource, cls);
                if (anchor is null) continue;
                Hash128 classEntity = anchor.Value;
                if (linkedClasses.Add(classEntity))
                    b.AddAttestation(NativeAttestation.CategoricalResolved(
                        rsEntity, PropBankSource.CorrespondsToTypeId, classEntity,
                        Source, null, TC.AcademicCurated));

                if (inner.Length > 0)
                {
                    RoleIdentityKind targetKind =
                        resource.Equals("VerbNet", StringComparison.OrdinalIgnoreCase)
                            ? RoleIdentityKind.VerbNet
                            : RoleIdentityKind.FrameNet;
                    Hash128? targetRole = RoleAnchor.Declare(
                        b, targetKind, classEntity, inner,
                        RoleAnchor.EntityTypeFor(targetKind), Source);
                    if (targetRole is not null)
                        b.AddAttestation(NativeAttestation.CategoricalResolved(
                            roleEntity, PropBankSource.RoleCorrespondsToTypeId, targetRole.Value,
                            Source, null, TC.AcademicCurated));
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
