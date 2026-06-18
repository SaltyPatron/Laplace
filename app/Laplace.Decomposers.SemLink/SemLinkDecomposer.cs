using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.SemLink;

public sealed class SemLinkDecomposer : IDecomposer
{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/SemLinkDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

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

        foreach (var (path, kind, label) in DocumentSpecs(instancesDir))
        {
            var witness = new SemLinkGrammarWitness(kind);
            var change = await StructuredGrammarIngest.IngestJsonDocumentAsync(
                path, witness.ModalityId, Source, witness, witnessWeight: 1.0, label, ct);
            if (change is not null && !options.DryRun)
                yield return change;
        }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
        => Task.FromResult<long?>(2L);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static IEnumerable<(string Path, SemLinkDocumentKind Kind, string Label)> DocumentSpecs(string dir)
    {
        string pbVn = Path.Combine(dir, "pb-vn2.json");
        if (File.Exists(pbVn))
            yield return (pbVn, SemLinkDocumentKind.PbVn, "semlink/pb-vn2");

        string vnFn = Path.Combine(dir, "vn-fn2.json");
        if (File.Exists(vnFn))
            yield return (vnFn, SemLinkDocumentKind.VnFn, "semlink/vn-fn2");
    }

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
