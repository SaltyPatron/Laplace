using System.Runtime.CompilerServices;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using TC = Laplace.Decomposers.Abstractions.SourceTrust;

namespace Laplace.Decomposers.SemLink;

public sealed class SemLinkDecomposer : IDecomposer
{
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
    public int     LayerOrder   => 3;
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
        => Task.FromResult<long?>(2L);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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

                b.AddAttestation(RelationTypeRegistry.Attest(
                    rsEntity, "CORRESPONDS_TO", vnEntity, Source, TC.AcademicCurated));

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

    private static IEnumerable<SubstrateChange> EmitVnFn(string dir, CancellationToken ct)
    {
        string path = Path.Combine(dir, "vn-fn2.json");
        if (!File.Exists(path)) yield break;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var b = NewBuilder("semlink/vn-fn2");

        foreach (var keyProp in doc.RootElement.EnumerateObject())
        {
            ct.ThrowIfCancellationRequested();
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
                b.AddAttestation(RelationTypeRegistry.Attest(
                    vnEntity, "CORRESPONDS_TO", fnEntity, Source, TC.AcademicCurated));
            }
        }
        yield return b.Build();
    }

    private static SubstrateChangeBuilder NewBuilder(string unit) =>
        new(Source, unit, null,
            entityCapacity:      1 << 16,
            physicalityCapacity: 1 << 16,
            attestationCapacity: 1 << 15);

    internal static string VnClassFromKey(string key)
    {
        int last = key.LastIndexOf('-');
        if (last > 0 && last + 1 < key.Length && char.IsLetter(key[last + 1]))
            return key[..last];
        return key;
    }

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
