using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Xml;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.PropBank;

public sealed class PropBankDecomposer : IDecomposer{
    // Each unit's rows are self-contained. PropBank owns its rolesets (it emits + types them);
    // the VerbNet-class / FrameNet-frame correspondence targets are owned by their own decomposers
    // and resolve by content-addressed id wherever they land (this source or another, this batch or
    // a later one). With the per-batch referential EXISTS pre-check gone these cross-source anchors
    // are legal, so N workers can commit batches concurrently.

    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/PropBankDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    private static readonly Hash128 RolesetTypeId = EntityTypeRegistry.PropBankRoleset;
    private static readonly Hash128 OrdinalTypeId = EntityTypeRegistry.Ordinal;
    // VerbNet_Class / FrameNet_Frame correspondence targets are now anchored by content-addressed id
    // (CategoryAnchor.Id) and typed by their owning decomposers, so PropBank no longer references
    // their type ids here. The types are still declared in InitializeAsync for vocab readback.

    internal static string NumericClassId(string classId) =>
        SourceEntityIdConventions.NumericVerbNetClassId(classId);

    internal static Hash128 OrdinalId(string n)         => Hash128.OfCanonical($"ordinal/{n}/v1");

    public Hash128 SourceId     => Source;
    public string  SourceName   => "PropBankDecomposer";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    // 7,566 under frames/ plus the top-level AMR-UMR-91-rolesets.xml frameset (see EnumerateFramesetFiles).
    private const long EstimatedFramesets = 7_567L;

    private static readonly ConcurrentDictionary<string, byte> _canonicalNames = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames.Keys.ToArray();

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("PropBank_Roleset");
        boot.AddType("VerbNet_Class");
        boot.AddType("FrameNet_Frame");
        boot.AddType("Ordinal");
        boot.AddRelationType("HAS_SENSE");
        boot.AddRelationType("HAS_DEFINITION");
        boot.AddRelationType("HAS_SEMANTIC_ROLE");
        boot.AddRelationType("HAS_EXAMPLE");
        boot.AddRelationType("CORRESPONDS_TO");
        boot.AddRelationType("ROLE_CORRESPONDS_TO");
        boot.AddRelationType("HAS_FEATURE");
        await context.Writer.ApplyAsync(boot.Build(), ct);
        foreach (var n in boot.CanonicalNames)
            _canonicalNames.TryAdd(n, 0);
    }

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
            string num   = role.GetAttribute("n").Trim();
            if (descr.Length == 0) continue;
            var roleId = ContentEmitter.Emit(b, descr, Source);
            if (roleId is null) continue;

            Hash128? ctx = null;
            if (num.Length > 0)
            {
                string ord = num.Equals("M", StringComparison.OrdinalIgnoreCase) ? "m" : num;
                _canonicalNames.TryAdd($"ordinal/{ord}/v1", 0);
                Hash128 ordEntity = OrdinalId(ord);
                b.AddEntity(new EntityRow(ordEntity, EntityTier.Vocabulary, OrdinalTypeId, Source));
                ctx = ordEntity;
            }

            b.AddAttestation(NativeAttestation.Categorical(
                rsEntity, "HAS_SEMANTIC_ROLE", roleId.Value, Source, TC.AcademicCurated,
                contextId: ctx));

            // The role's function tag (PAG/PPT/GOL proto-roles; TMP/LOC/MNR/DIR/... for ARGM modifiers) is
            // a distinct semantic channel — emit it as a feature of the role instead of dropping it.
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
                string cls      = link.GetAttribute("class").Trim();
                string inner    = link.InnerText.Trim();
                if (cls.Length == 0) continue;

                // VerbNet: class = numeric VN class (lemma-prefixed), inner text = thematic role.
                // FrameNet: class = frame name (NOT numeric — must not pass through NumericClassId, which
                // strips lemma prefixes and would corrupt a frame name), inner text = frame element.
                // Both map roleset -CORRESPONDS_TO- class/frame and role -ROLE_CORRESPONDS_TO- theta/FE,
                // distinguished by the object's entity type. The FrameNet_Frame / VerbNet_Class anchors
                // share identity with their owning decomposers' entities so the resources converge.
                // We only need the anchor's content-addressed id to hang the CORRESPONDS_TO edge on;
                // the entity row and its typing edge are emitted by the resource that owns it (VerbNet
                // / FrameNet), wherever that lands. Pre-typing it here existed solely to keep the
                // attestation from dangling under the deleted referential EXISTS pre-check, so the
                // Emit is downgraded to Id (no entity/typing row written for another source's anchor).
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

    // The distribution root (propbank-frames-main/, i.e. the parent of the resolved frames/
    // directory) carries the bulk of framesets under frames/, but also ships a handful of
    // standalone top-level frameset files (e.g. AMR-UMR-91-rolesets.xml for AMR/UMR reification
    // rolesets) that never landed under frames/. Scan both frames/ and its parent — deduped by
    // full path — and rely on the existing root.Name == "frameset" guard in DecomposeAsync to skip
    // any non-frameset XML that happens to live alongside (there are none today, but the guard
    // makes that safe). The parent scan is skipped when ResolveFramesDir already fell back to the
    // ecosystem root itself, so this never walks outside the vault.
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
