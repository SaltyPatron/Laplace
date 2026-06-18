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

    private static readonly Hash128 RolesetTypeId      = EntityTypeRegistry.PropBankRoleset;
    private static readonly Hash128 VerbNetClassTypeId = EntityTypeRegistry.VerbNetClass;
    private static readonly Hash128 OrdinalTypeId      = EntityTypeRegistry.Ordinal;

    internal static string NumericClassId(string classId)
    {
        if (classId.Length == 0 || char.IsDigit(classId[0])) return classId;
        for (int i = classId.IndexOf('-'); i >= 0 && i + 1 < classId.Length; i = classId.IndexOf('-', i + 1))
            if (char.IsDigit(classId[i + 1])) return classId[(i + 1)..];
        return classId;
    }
    internal static Hash128 OrdinalId(string n)         => Hash128.OfCanonical($"ordinal/{n}/v1");

    public Hash128 SourceId     => Source;
    public string  SourceName   => "PropBankDecomposer";
    public int     LayerOrder   => 2;
    public Hash128 TrustClassId => TrustClass;

    private const long EstimatedFramesets = 7_566L;

    private static readonly ConcurrentDictionary<string, byte> _canonicalNames = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> CanonicalNamesForReadback => _canonicalNames.Keys.ToArray();

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("PropBank_Roleset");
        boot.AddType("VerbNet_Class");
        boot.AddType("Ordinal");
        boot.AddRelationType("HAS_SENSE");
        boot.AddRelationType("HAS_DEFINITION");
        boot.AddRelationType("HAS_SEMANTIC_ROLE");
        boot.AddRelationType("HAS_EXAMPLE");
        boot.AddRelationType("CORRESPONDS_TO");
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

            foreach (var link in DescendantElements(role, "rolelink"))
            {
                if (!link.GetAttribute("resource").Equals("VerbNet", StringComparison.OrdinalIgnoreCase))
                    continue;
                string vnClass = link.GetAttribute("class").Trim();
                string theta   = link.InnerText.Trim();
                if (vnClass.Length == 0) continue;

                
                
                Hash128? vnAnchor = CategoryAnchor.Emit(b, NumericClassId(vnClass), VerbNetClassTypeId, Source, TC.AcademicCurated);
                if (vnAnchor is null) continue;
                Hash128 vnEntity = vnAnchor.Value;
                b.AddAttestation(NativeAttestation.Categorical(
                    rsEntity, "CORRESPONDS_TO", vnEntity, Source, TC.AcademicCurated));

                if (theta.Length > 0)
                {
                    var thetaId = ContentEmitter.Emit(b, theta, Source);
                    if (thetaId is not null)
                        b.AddAttestation(NativeAttestation.Categorical(
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
                    b.AddAttestation(NativeAttestation.Categorical(
                        rsEntity, "HAS_EXAMPLE", exId.Value, Source, TC.AcademicCurated));
            }
    }

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
