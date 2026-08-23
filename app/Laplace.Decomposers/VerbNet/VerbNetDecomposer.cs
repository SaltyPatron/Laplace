using System.Runtime.CompilerServices;
using System.Xml;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.VerbNet;

public sealed class VerbNetDecomposer
    : ComposeDecomposerMultiFile<XmlElement, VerbNetSource, FullScope>, IIngestInventoryProvider
{







    public static readonly Hash128 Source = VerbNetSource.SourceId;
    public static readonly Hash128 TrustClass = VerbNetSource.TrustClass;

    private static readonly Hash128 ClassTypeId = EntityTypeRegistry.VerbNetClass;



    private const long EstimatedClasses = 329L;

    public override int LayerOrder => 2;
    protected override double SourceTrust => TC.AcademicCurated;
    protected override string BatchLabelPrefix => "verbnet";

    protected override IReadOnlyList<(string Path, string Label)> ListFiles(
        string ecosystemPath, DecomposerOptions options)
    {
        string classDir = IngestInput.ResolveSubdir(
            ecosystemPath, "*.xml",
            Path.Combine("verbnet-master", "verbnet3.4"), "verbnet3.4");
        return SharedXmlFramesetReader.EnumerateXmlFiles(classDir)
            .Select((file, i) => (file, $"verbnet/{i}/{Path.GetFileName(file)}"))
            .ToList();
    }

    protected override async IAsyncEnumerable<XmlElement> ExtractFileAsync(
        string filePath, string fileLabel, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var root in SharedXmlFramesetReader.ReadRootsAsync(
                           [filePath], "VNCLASS", ct))
            yield return root;
    }

    protected override void Compose(XmlElement root, SubstrateChangeBuilder b) =>
        EmitClass(b, root, parentClassId: null);

    public override Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(EstimatedClasses);

    public Task<IngestInventory?> DescribeInputAsync(
        IDecomposerContext context, DecomposerOptions options, CancellationToken ct = default)
    {
        var paths = ListFiles(context.EcosystemPath, options).Select(x => x.Path).ToList();
        return Task.FromResult(IngestInventory.FromFileUnits(
            "classes", paths, options.MaxInputUnits, tracksFileCompletion: true));
    }

    private static void EmitClass(SubstrateChangeBuilder b, XmlElement el, string? parentClassId)
    {
        string? classId = el.GetAttribute("ID");
        if (string.IsNullOrEmpty(classId)) return;



        Hash128? classAnchor = AnchorAdmission.Emit(
            b, SourceEntityIdConventions.NumericVerbNetClassId(classId),
            ClassTypeId, Source, TC.AcademicCurated);
        if (classAnchor is null) return;
        Hash128 classEntity = classAnchor.Value;
        if (parentClassId is not null)
        {




            Hash128? parentAnchor = AnchorAdmission.Id(
                SourceEntityIdConventions.NumericVerbNetClassId(parentClassId), ClassTypeId);
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

            // A VerbNet member is a class-owned lexical entry, not the globally
            // reusable lemma content. Ten entries in the 3.4 corpus omit
            // verbnet_key; the class-bound source name is their lossless fallback.
            string memberKey = member.GetAttribute("verbnet_key").Trim();
            if (memberKey.Length == 0) memberKey = $"name:{name}";
            var memberId = LexicalMemberAnchor.Emit(
                b, LexicalMemberIdentityKind.VerbNet, classEntity, memberKey,
                EntityTypeRegistry.VerbNetMember, Source, TC.AcademicCurated);
            if (memberId is null) continue;

            b.AddAttestation(NativeAttestation.CategoricalResolved(
                memberId.Value, VerbNetSource.HasNameAliasTypeId, lemmaId.Value,
                Source, null, TC.AcademicCurated));
            b.AddAttestation(NativeAttestation.Categorical(
                memberId.Value, "MEMBER_OF_VERBNET_CLASS", classEntity, Source, TC.AcademicCurated));

            string wn = member.GetAttribute("wn");
            if (wn.Length > 0)
                foreach (var raw in wn.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    string? key = SourceEntityIdConventions.NormalizeSenseKey(raw);
                    if (key is null) continue;
                    var senseEntity = SenseAnchor.Id(key);
                    if (senseEntity is null) continue;
                    b.AddAttestation(NativeAttestation.CategoricalResolved(
                        memberId.Value, VerbNetSource.CorrespondsToTypeId, senseEntity.Value,
                        Source, null, TC.AcademicCurated));
                }

            foreach (string rolesetKey in member.GetAttribute("grouping")
                         .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Distinct(StringComparer.Ordinal))
            {
                var rolesetId = ReferenceAnchor.Declare(
                    b, ReferenceIdentityKind.PropBankRoleset, rolesetKey,
                    EntityTypeRegistry.PropBankRoleset, Source);
                if (rolesetId is not null)
                    b.AddAttestation(NativeAttestation.CategoricalResolved(
                        memberId.Value, VerbNetSource.CorrespondsToTypeId, rolesetId.Value,
                        Source, null, TC.AcademicCurated));
            }

            // VerbNet 3.4 calls this field fn_mapping. Values are FrameNet frame
            // names, whitespace-separated when one member maps to multiple frames;
            // the literal None is the source sentinel for no mapping. The previous
            // fnframe lookup matched zero attributes in the 3.4 vault corpus and
            // silently dropped every direct member mapping.
            foreach (string frameName in member.GetAttribute("fn_mapping")
                         .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .Where(static value => !value.Equals("None", StringComparison.OrdinalIgnoreCase))
                         .Distinct(StringComparer.Ordinal))
            {
                // The mapping row witnesses member->frame. Do not also re-witness
                // the frame's intrinsic type once for every member that names it;
                // FrameNet owns that node declaration and the relation already
                // fixes the object domain for readers.
                var frameId = ContentEmitter.Emit(b, frameName, Source);
                if (frameId is not null)
                    b.AddAttestation(NativeAttestation.Categorical(
                        memberId.Value, "EVOKES_FRAME", frameId.Value, Source, TC.AcademicCurated));
            }
        }

        foreach (var role in SharedXmlFramesetReader.ChildElements(el, "THEMROLES", "THEMROLE"))
        {
            string type = role.GetAttribute("type").Trim();
            if (type.Length == 0) continue;
            var roleLabelId = ContentEmitter.Emit(b, type, Source);
            var roleId = RoleAnchor.Emit(
                b, RoleIdentityKind.VerbNet, classEntity, type,
                EntityTypeRegistry.VerbNetRole, Source, TC.AcademicCurated);
            if (roleLabelId is null || roleId is null) continue;
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                classEntity, VerbNetSource.HasThematicRoleTypeId, roleId.Value,
                Source, null, TC.AcademicCurated));
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                roleId.Value, VerbNetSource.HasNameAliasTypeId, roleLabelId.Value,
                Source, null, TC.AcademicCurated));
        }

        int frameOrdinal = -1;
        foreach (var frame in SharedXmlFramesetReader.ChildElements(el, "FRAMES", "FRAME"))
        {
            frameOrdinal++;
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





            int predicateOrdinal = 0;
            foreach (XmlNode semNode in frame.GetElementsByTagName("SEMANTICS"))
            {
                if (semNode is not XmlElement sem) continue;
                foreach (XmlNode predNode in sem.GetElementsByTagName("PRED"))
                {
                    if (predNode is not XmlElement pred) continue;
                    int currentPredicateOrdinal = predicateOrdinal++;
                    string predVal = pred.GetAttribute("value").Trim();
                    if (predVal.Length == 0) continue;
                    var predLabelId = ContentEmitter.Emit(b, predVal, Source);
                    if (predLabelId is null) continue;
                    var arguments = new List<SemanticPredicateArgument>();
                    var roleValues = new List<string>();
                    foreach (XmlNode argNode in pred.GetElementsByTagName("ARG"))
                    {
                        if (argNode is not XmlElement arg) continue;
                        string argType = arg.GetAttribute("type").Trim();
                        string argValue = arg.GetAttribute("value").Trim();
                        arguments.Add(new SemanticPredicateArgument(argType, argValue));
                        string roleValue = argValue.TrimStart('?');
                        if (argType.Equals("ThemRole", StringComparison.OrdinalIgnoreCase)
                            && roleValue.Length > 0)
                            roleValues.Add(roleValue);
                    }

                    // <PRED bool="!"> IS A REFUTATION, AND IT WAS ENTERED AS AN ASSERTION.
                    //
                    // VerbNet negates a predicate with bool="!": escape-51.1 entails that the
                    // theme is NOT at its initial location. The attribute was never read, so
                    // 2,860 of 19,490 PREDs in verbnet-master (14.7%) were deposited as
                    // ENTAILS -- the substrate asserting the negation of what the source
                    // states. A further 39 carry bool="?" (optional), which is a DRAW, not a
                    // confirmation: laplace_score_fp(0, m) is exactly 0.5.
                    //
                    // outcome is the field for this. `confirm: false` folds a Refute against
                    // the same cell a positive ENTAILS would fold into, which is what makes
                    // it adjudicate rather than accumulate: the negation is deliberately NOT
                    // part of the predicate id preimage, so an assertion in one frame and a
                    // denial in another meet in one consensus cell and contest it.
                    //
                    // Without this the substrate has almost no losses anywhere -- every
                    // observation is a win against a fixed opponent, so rating is a monotone
                    // function of witness count (docs/evidence-flattening-2026-08-23.md).
                    string predBool = pred.GetAttribute("bool").Trim();
                    bool negated = predBool == "!";
                    bool optional = predBool == "?";

                    Hash128 predicateId = SemanticPredicateAnchor.Declare(
                        b, SemanticPredicateIdentityKind.VerbNet, classEntity,
                        frameOrdinal, currentPredicateOrdinal, predLabelId.Value, arguments,
                        EntityTypeRegistry.VerbNetPredicate, Source);
                    CategoryAnchor.AttestCategory(
                        b, predicateId, EntityTypeRegistry.VerbNetPredicate,
                        Source, TC.AcademicCurated);
                    b.AddAttestation(optional
                        ? NativeAttestation.ResolvedScored(
                            classEntity, VerbNetSource.EntailsTypeId, predicateId,
                            Source, null, TC.AcademicCurated,
                            signedMagnitude: 0.0, arenaScale: 1.0)
                        : NativeAttestation.CategoricalResolved(
                            classEntity, VerbNetSource.EntailsTypeId, predicateId,
                            Source, null, TC.AcademicCurated, confirm: !negated));
                    b.AddAttestation(NativeAttestation.CategoricalResolved(
                        predicateId, VerbNetSource.HasNameAliasTypeId, predLabelId.Value,
                        Source, null, TC.AcademicCurated));
                    foreach (string roleVal in roleValues)
                    {
                        var roleId = RoleAnchor.Declare(
                            b, RoleIdentityKind.VerbNet, classEntity, roleVal,
                            EntityTypeRegistry.VerbNetRole, Source);
                        if (roleId is not null)
                            b.AddAttestation(NativeAttestation.CategoricalResolved(
                                predicateId, VerbNetSource.HasSemanticRoleTypeId, roleId.Value,
                                Source, null, TC.AcademicCurated));
                    }
                }
            }
        }

        foreach (var subWrap in SharedXmlFramesetReader.DirectChildren(el, "SUBCLASSES"))
            foreach (var sub in SharedXmlFramesetReader.DirectChildren(subWrap, "VNSUBCLASS"))
                EmitClass(b, sub, parentClassId: classId);
    }
}
