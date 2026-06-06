using System.Runtime.CompilerServices;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.SemLink;

/// <summary>
/// Emits SemLink (instance mappings) into the substrate as cross-resource CORRESPONDS_TO
/// attestations — the alignment layer that ties VerbNet, PropBank, and FrameNet together.
///
/// Two instance JSONs:
/// <list type="bullet">
///   <item><c>pb-vn2.json</c>: <c>{"give.01": {"13.1-1": {"ARG0": "agent", "ARG1": "theme"}}}</c>
///   — PB roleset → VN class, plus PB arg → VN theta-role role-level map.</item>
///   <item><c>vn-fn2.json</c>: <c>{"26.5-shake": ["Moving_in_place", …]}</c> — a VN
///   class(-member) key → the FrameNet frames it maps onto.</item>
/// </list>
///
/// <para><b>The law applied:</b> SemLink mints NO new identity — every endpoint is a meta
/// entity another resource already owns (<c>propbank/roleset/&lt;id&gt;</c>,
/// <c>verbnet/class/&lt;bare-numeric&gt;</c>, <c>framenet/frame/&lt;name&gt;</c>) or a role-name
/// CONTENT entity. It is pure CORRESPONDS_TO (the symmetric equivalence arena — NEVER welded
/// into IS_A). Role-level alignments (PB arg ↔ VN theta) link the role-name CONTENT entities
/// with the class as provenance context.</para>
///
/// <para><b>LayerOrder 3</b> (after VerbNet+PropBank): it references their entities. But writer
/// batches are SELF-CONTAINED — each referenced meta entity is ALSO emitted idempotently here
/// (ON CONFLICT) so SemLink ingests standalone even if VN/PB have not run, and the FK floor
/// always holds. Single pass per file; both files small (&lt;400 KB) — one batch each.</para>
/// </summary>
public sealed class SemLinkDecomposer : IDecomposer
{
    /// <summary>Meta-entity canonical names — registered post-ingest so
    /// render() answers in names, never hex (2026-06-05).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> MetaNames = new();

    public IReadOnlyCollection<string> CanonicalNamesForReadback
        => System.Linq.Enumerable.ToList(MetaNames.Keys);

    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/SemLinkDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    private static readonly Hash128 VerbNetClassTypeId =
        Hash128.OfCanonical("substrate/type/VerbNet_Class/v1");
    private static readonly Hash128 PropBankRolesetTypeId =
        Hash128.OfCanonical("substrate/type/PropBank_Roleset/v1");
    private static readonly Hash128 FrameNetFrameTypeId =
        Hash128.OfCanonical("substrate/type/FrameNet_Frame/v1");

    // Cross-resource meta-entity id conventions — the EXACT strings VerbNet /
    // PropBank / FrameNet emit, so SemLink's CORRESPONDS_TO endpoints collide
    // with their owners' entities (the omni-source consensus, not a fork).
    internal static Hash128 VnClassId(string vnClass)
    {
        string name = $"verbnet/class/{NumericClassId(vnClass)}";
        MetaNames.TryAdd(name, 0);
        return Hash128.OfCanonical(name);
    }
    internal static Hash128 RolesetId(string rolesetId)
    {
        string name = $"propbank/roleset/{rolesetId}";
        MetaNames.TryAdd(name, 0);
        return Hash128.OfCanonical(name);
    }
    internal static Hash128 FrameId(string frameName)
    {
        string name = $"framenet/frame/{frameName}";
        MetaNames.TryAdd(name, 0);
        return Hash128.OfCanonical(name);
    }

    public Hash128 SourceId     => Source;
    public string  SourceName   => "SemLinkDecomposer";
    public int     LayerOrder   => 3;   // after VerbNet + PropBank (references their entities)
    public Hash128 TrustClassId => TrustClass;

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("VerbNet_Class");
        boot.AddType("PropBank_Roleset");
        boot.AddType("FrameNet_Frame");
        boot.AddRelationType("CORRESPONDS_TO");
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string instancesDir = ResolveInstancesDir(context.EcosystemPath);

        foreach (var change in EmitPbVn(instancesDir, ct))
        { if (!options.DryRun) yield return change; await Task.Yield(); }

        foreach (var change in EmitVnFn(instancesDir, ct))
        { if (!options.DryRun) yield return change; await Task.Yield(); }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(2L);   // two instance files

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── pb-vn2.json: PB roleset ↔ VN class, PB arg ↔ VN theta role ───────────

    private static IEnumerable<SubstrateChange> EmitPbVn(string dir, CancellationToken ct)
    {
        string path = Path.Combine(dir, "pb-vn2.json");
        if (!File.Exists(path)) yield break;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var b = NewBuilder("semlink/pb-vn2");

        foreach (var rolesetProp in doc.RootElement.EnumerateObject())
        {
            ct.ThrowIfCancellationRequested();
            string rolesetKey = rolesetProp.Name.Trim();
            if (rolesetKey.Length == 0 || rolesetProp.Value.ValueKind != JsonValueKind.Object) continue;
            Hash128 rsEntity = RolesetId(rolesetKey);
            b.AddEntity(new EntityRow(rsEntity, (byte)MetaTier.Meta, PropBankRolesetTypeId, Source));

            foreach (var classProp in rolesetProp.Value.EnumerateObject())
            {
                string vnClass = classProp.Name.Trim();
                if (vnClass.Length == 0) continue;
                Hash128 vnEntity = VnClassId(vnClass);
                b.AddEntity(new EntityRow(vnEntity, (byte)MetaTier.Meta, VerbNetClassTypeId, Source));

                // roleset ↔ VN class (symmetric equivalence).
                b.AddAttestation(RelationTypeRegistry.Attest(
                    rsEntity, "CORRESPONDS_TO", vnEntity, Source, TC.AcademicCurated));

                // Role-level: PB arg name (content) ↔ VN theta role (content), with
                // the VN class as provenance context.
                if (classProp.Value.ValueKind == JsonValueKind.Object)
                    foreach (var roleProp in classProp.Value.EnumerateObject())
                    {
                        string arg = roleProp.Name.Trim();
                        string theta = roleProp.Value.ValueKind == JsonValueKind.String
                            ? (roleProp.Value.GetString() ?? "").Trim() : "";
                        if (arg.Length == 0 || theta.Length == 0) continue;
                        var argId   = ContentEmitter.Emit(b, arg, Source);
                        var thetaId = ContentEmitter.Emit(b, theta, Source);
                        if (argId is null || thetaId is null) continue;
                        b.AddAttestation(RelationTypeRegistry.Attest(
                            argId.Value, "CORRESPONDS_TO", thetaId.Value, Source, TC.AcademicCurated,
                            contextId: vnEntity));
                    }
            }
        }
        yield return b.Build();
    }

    // ── vn-fn2.json: VN class ↔ FrameNet frame ────────────────────────────────

    private static IEnumerable<SubstrateChange> EmitVnFn(string dir, CancellationToken ct)
    {
        string path = Path.Combine(dir, "vn-fn2.json");
        if (!File.Exists(path)) yield break;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var b = NewBuilder("semlink/vn-fn2");

        foreach (var keyProp in doc.RootElement.EnumerateObject())
        {
            ct.ThrowIfCancellationRequested();
            // key = "<vnclass>-<member>" (e.g. "26.5-shake", "21.1-1-chip"); the
            // member lemma is the trailing alpha token — the VN class is the rest.
            string vnClass = VnClassFromKey(keyProp.Name);
            if (vnClass.Length == 0) continue;
            Hash128 vnEntity = VnClassId(vnClass);
            b.AddEntity(new EntityRow(vnEntity, (byte)MetaTier.Meta, VerbNetClassTypeId, Source));

            if (keyProp.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var frameElem in keyProp.Value.EnumerateArray())
            {
                if (frameElem.ValueKind != JsonValueKind.String) continue;
                string frame = (frameElem.GetString() ?? "").Trim();
                if (frame.Length == 0) continue;
                Hash128 fnEntity = FrameId(frame);
                b.AddEntity(new EntityRow(fnEntity, (byte)MetaTier.Meta, FrameNetFrameTypeId, Source));
                // VN class ↔ FN frame (symmetric equivalence; FrameNet agent owns
                // framenet/frame/<name> — this co-asserts onto it).
                b.AddAttestation(RelationTypeRegistry.Attest(
                    vnEntity, "CORRESPONDS_TO", fnEntity, Source, TC.AcademicCurated));
            }
        }
        yield return b.Build();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static SubstrateChangeBuilder NewBuilder(string unit) =>
        new(Source, unit, null,
            entityCapacity:      1 << 16,
            physicalityCapacity: 1 << 16,
            attestationCapacity: 1 << 15);

    /// <summary>Extract the VN class from a vn-fn2 key (<c>26.5-shake</c> → <c>26.5</c>;
    /// <c>21.1-1-chip</c> → <c>21.1-1</c>): the member lemma is the trailing
    /// hyphen-delimited token whose first char is alphabetic; the class is the rest.</summary>
    internal static string VnClassFromKey(string key)
    {
        int last = key.LastIndexOf('-');
        if (last > 0 && last + 1 < key.Length && char.IsLetter(key[last + 1]))
            return key[..last];
        return key;   // no member suffix (defensive)
    }

    /// <summary>Strip a VerbNet class id's lemma prefix to its bare numeric form —
    /// the cross-resource convention shared with VerbNet/PropBank. SemLink's classes
    /// are already bare numeric (start with a digit → returned as-is); the prefixed
    /// branch is kept identical so any prefixed form (defensive) still collides.</summary>
    internal static string NumericClassId(string classId)
    {
        if (classId.Length == 0 || char.IsDigit(classId[0])) return classId;
        for (int i = classId.IndexOf('-'); i >= 0 && i + 1 < classId.Length; i = classId.IndexOf('-', i + 1))
            if (char.IsDigit(classId[i + 1])) return classId[(i + 1)..];
        return classId;
    }

    private static string ResolveInstancesDir(string ecosystemPath)
    {
        foreach (var c in new[]
                 {
                     Path.Combine(ecosystemPath, "semlink-master", "instances"),
                     Path.Combine(ecosystemPath, "instances"),
                     ecosystemPath,
                 })
            if (File.Exists(Path.Combine(c, "pb-vn2.json")) ||
                File.Exists(Path.Combine(c, "vn-fn2.json")))
                return c;
        return ecosystemPath;
    }
}
