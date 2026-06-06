using System.Runtime.CompilerServices;
using System.Xml;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.PropBank;

/// <summary>
/// Emits PropBank (Frames v3.4) into the substrate as "content + attestations".
///
/// PropBank annotates predicate ARGUMENT STRUCTURE: each predicate lemma carries one or more
/// ROLESETS (give.01 "transfer", give.08 …), and each roleset lists numbered semantic roles
/// (Arg0 "giver", Arg1 "thing given", …) with cross-resource rolelinks (VerbNet class + theta
/// role, FrameNet frame + FE) and gold example sentences.
///
/// <para><b>The law applied:</b> predicate LEMMAS, roleset NAME/descr TEXT, role descr TEXT,
/// and example SENTENCES are CONTENT entities (<see cref="ContentEmitter"/>) — they co-assert
/// with VerbNet, FrameNet, WordNet, and every prose/model witness. Only the abstract ROLESET
/// construct keeps a content-addressed meta id (<c>propbank/roleset/&lt;id&gt;</c>) — the EXACT
/// convention SemLink references back. The rolelink VerbNet target reuses VerbNet's class meta
/// convention (<c>verbnet/class/&lt;id&gt;</c>) and theta-role CONTENT so PB↔VN aligns on one
/// substrate.</para>
///
/// <para>Coverage: predicate lemma —HAS_SENSE→ roleset (the SAME arena+direction WordNet uses
/// for lemma→sense, so word-sense inventories co-assert); roleset —HAS_DEFINITION→ its
/// name/descr text; roleset —HAS_SEMANTIC_ROLE→ role descr text, with the arg NUMBER as a
/// context-id ordinal entity (<c>ordinal/&lt;n&gt;/v1</c>); rolelink (VerbNet) → CORRESPONDS_TO
/// roleset↔VN class and arg-role↔VN theta role; example —HAS_EXAMPLE→ roleset.</para>
///
/// <para>Single XML pass per frameset file; each batch self-contained (entities ride the same
/// intent as the attestations referencing them; writer orders entities first) — ON CONFLICT
/// idempotent, batches commit in any order.</para>
/// </summary>
public sealed class PropBankDecomposer : IDecomposer
{
    /// <summary>Meta-entity canonical names — registered post-ingest so
    /// render() answers in names, never hex (2026-06-05).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> MetaNames = new();

    public IReadOnlyCollection<string> CanonicalNamesForReadback
        => System.Linq.Enumerable.ToList(MetaNames.Keys);

    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/PropBankDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    private static readonly Hash128 RolesetTypeId =
        Hash128.OfCanonical("substrate/type/PropBank_Roleset/v1");
    private static readonly Hash128 VerbNetClassTypeId =
        Hash128.OfCanonical("substrate/type/VerbNet_Class/v1");
    private static readonly Hash128 OrdinalTypeId =
        Hash128.OfCanonical("substrate/type/Ordinal/v1");

    // Meta-entity id conventions (the LAW: rolesets get meta ids; SemLink references
    // these EXACT strings). The VerbNet class id reuses VerbNetDecomposer's BARE
    // NUMERIC convention: PropBank rolelinks carry the lemma prefix (give-13.1-1),
    // VerbNet/SemLink use the bare numeric (13.1-1) — strip the prefix so PB↔VN↔SemLink
    // collide on one class entity.
    internal static Hash128 RolesetId(string rolesetId)
    {
        string name = $"propbank/roleset/{rolesetId}";
        MetaNames.TryAdd(name, 0);
        return Hash128.OfCanonical(name);
    }
    internal static Hash128 VnClassId(string vnClass)
    {
        string name = $"verbnet/class/{NumericClassId(vnClass)}";
        MetaNames.TryAdd(name, 0);
        return Hash128.OfCanonical(name);
    }

    /// <summary>Strip a VerbNet class id's lemma prefix to its bare numeric form —
    /// the cross-resource convention shared with VerbNet/SemLink (give-13.1-1 → 13.1-1;
    /// already-bare 13.1-1 → 13.1-1). A class id starts with a digit (bare) or an
    /// alphabetic lemma the first <c>-&lt;digit&gt;</c> separates from the class.</summary>
    internal static string NumericClassId(string classId)
    {
        if (classId.Length == 0 || char.IsDigit(classId[0])) return classId;
        for (int i = classId.IndexOf('-'); i >= 0 && i + 1 < classId.Length; i = classId.IndexOf('-', i + 1))
            if (char.IsDigit(classId[i + 1])) return classId[(i + 1)..];
        return classId;
    }
    // Reuse the seeded ordinal context convention (UcdProperties.OrdinalCtx*).
    internal static Hash128 OrdinalId(string n)         => Hash128.OfCanonical($"ordinal/{n}/v1");

    public Hash128 SourceId     => Source;
    public string  SourceName   => "PropBankDecomposer";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    private const long EstimatedFramesets = 7_566L;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("PropBank_Roleset");
        boot.AddType("VerbNet_Class");        // matches VerbNetDecomposer's class-entity type
        boot.AddType("Ordinal");
        boot.AddRelationType("HAS_SENSE");            // lemma → roleset, SAME arena/direction as WordNet
        boot.AddRelationType("HAS_DEFINITION");
        boot.AddRelationType("HAS_SEMANTIC_ROLE");
        boot.AddRelationType("HAS_EXAMPLE");
        boot.AddRelationType("CORRESPONDS_TO");
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string framesDir = ResolveFramesDir(context.EcosystemPath);
        int batch = options.BatchSize > 1 ? options.BatchSize : 64;

        var b = NewBuilder("propbank/batch-0", batch);
        int n = 0, bn = 0;

        foreach (var file in EnumerateFramesetFiles(framesDir))
        {
            ct.ThrowIfCancellationRequested();
            var doc = new XmlDocument();
            try { doc.Load(file); }
            catch (XmlException) { continue; }
            var root = doc.DocumentElement;
            if (root is null || !root.Name.Equals("frameset", StringComparison.Ordinal)) continue;

            foreach (XmlNode pNode in root.GetElementsByTagName("predicate"))
                if (pNode is XmlElement predicate)
                    EmitPredicate(b, predicate);

            if (++n >= batch)
            {
                if (!options.DryRun) yield return b.Build();
                b = NewBuilder($"propbank/batch-{++bn}", batch);
                n = 0; await Task.Yield();
            }
        }
        if (n > 0 && !options.DryRun) yield return b.Build();
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(EstimatedFramesets);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── emission ──────────────────────────────────────────────────────────────

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

            Hash128 rsEntity = RolesetId(rsId);
            b.AddEntity(new EntityRow(rsEntity, (byte)MetaTier.Meta, RolesetTypeId, Source));

            // predicate lemma —HAS_SENSE→ roleset (lemma→sense, the WordNet arena/direction).
            b.AddAttestation(RelationTypeRegistry.Attest(
                lemmaId.Value, "HAS_SENSE", rsEntity, Source, TC.AcademicCurated));

            // roleset —HAS_DEFINITION→ its name/descr text (the gloss of this sense).
            string name = roleset.GetAttribute("name").Trim();
            if (name.Length > 0)
            {
                var defId = ContentEmitter.Emit(b, name, Source);
                if (defId is not null)
                    b.AddAttestation(RelationTypeRegistry.Attest(
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
            string num   = role.GetAttribute("n").Trim();
            if (descr.Length == 0) continue;
            var roleId = ContentEmitter.Emit(b, descr, Source);
            if (roleId is null) continue;

            // arg NUMBER as a context-id ordinal entity (reuse the seeded ordinal
            // convention). 'm'/'M' (modifier args) normalize to the lowercase form.
            Hash128? ctx = null;
            if (num.Length > 0)
            {
                string ord = num.Equals("M", StringComparison.OrdinalIgnoreCase) ? "m" : num;
                Hash128 ordEntity = OrdinalId(ord);
                b.AddEntity(new EntityRow(ordEntity, (byte)MetaTier.Meta, OrdinalTypeId, Source));
                ctx = ordEntity;
            }

            b.AddAttestation(RelationTypeRegistry.Attest(
                rsEntity, "HAS_SEMANTIC_ROLE", roleId.Value, Source, TC.AcademicCurated,
                contextId: ctx));

            // rolelinks → CORRESPONDS_TO: roleset↔VN class, role text↔VN theta role.
            foreach (var link in DescendantElements(role, "rolelink"))
            {
                if (!link.GetAttribute("resource").Equals("VerbNet", StringComparison.OrdinalIgnoreCase))
                    continue;
                string vnClass = link.GetAttribute("class").Trim();
                string theta   = link.InnerText.Trim();
                if (vnClass.Length == 0) continue;

                Hash128 vnEntity = VnClassId(vnClass);
                b.AddEntity(new EntityRow(vnEntity, (byte)MetaTier.Meta, VerbNetClassTypeId, Source));
                // roleset ↔ VN class (symmetric equivalence arena).
                b.AddAttestation(RelationTypeRegistry.Attest(
                    rsEntity, "CORRESPONDS_TO", vnEntity, Source, TC.AcademicCurated));

                // PB arg role text ↔ VN theta-role text (both CONTENT), with the
                // VN class pair as provenance context where present.
                if (theta.Length > 0)
                {
                    var thetaId = ContentEmitter.Emit(b, theta, Source);
                    if (thetaId is not null)
                        b.AddAttestation(RelationTypeRegistry.Attest(
                            roleId.Value, "CORRESPONDS_TO", thetaId.Value, Source, TC.AcademicCurated,
                            contextId: vnEntity));
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
                    b.AddAttestation(RelationTypeRegistry.Attest(
                        rsEntity, "HAS_EXAMPLE", exId.Value, Source, TC.AcademicCurated,
                        contextId: rsEntity));
            }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static SubstrateChangeBuilder NewBuilder(string unit, int batch) =>
        new(Source, unit, null,
            entityCapacity:      batch * 96,
            physicalityCapacity: batch * 96,
            attestationCapacity: batch * 48);

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

    private static IEnumerable<string> EnumerateFramesetFiles(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.xml")
                                   .OrderBy(p => p, StringComparer.Ordinal))
            yield return f;
    }
}
