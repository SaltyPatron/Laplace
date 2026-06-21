using System.Runtime.CompilerServices;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.SemLink;

public sealed class SemLinkDecomposer : IDecomposer{
    public static readonly Hash128 Source =
        Hash128.OfCanonical("substrate/source/SemLinkDecomposer/v1");
    public static readonly Hash128 TrustClass =
        Hash128.OfCanonical("substrate/trust_class/AcademicCurated/v1");

    public Hash128 SourceId     => Source;
    public string  SourceName   => "SemLinkDecomposer";
    public int     LayerOrder   => 3;
    public Hash128 TrustClassId => TrustClass;

    // SemLink mapping files are top-level JSON objects (pb-vn2, vn-fn2, optional pb/vn/fn-wn JSON bridges)
    // or Predicate Matrix TSV (PredicateMatrix.txt). Each record stages category anchors and CORRESPONDS_TO
    // edges over content-addressed ids; WordNet synset targets resolve via CILI/ILI (ConceptAnchor).

    public async Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        var boot = new BootstrapIntentBuilder(Source, SourceName, TrustClass);
        boot.AddType("VerbNet_Class");
        boot.AddType("PropBank_Roleset");
        boot.AddType("FrameNet_Frame");
        boot.AddRelationType("CORRESPONDS_TO");
        boot.AddRelationType("ROLE_CORRESPONDS_TO");
        await context.Writer.ApplyAsync(boot.Build(), ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        SourceEntityIdConventions.EnsureCiliMapForIngest(context.Logger, SourceName);

        string instancesDir = ResolveInstancesDir(context.EcosystemPath);
        int batchSize = options.BatchSize > 0 ? options.BatchSize : 1;

        foreach (var (path, kind, label) in JsonDocumentSpecs(instancesDir))
        {
            var witness = new SemLinkGrammarWitness(kind);
            await foreach (var change in StreamJsonDocumentAsync(
                               path, witness, label, batchSize, ct))
            {
                if (!options.DryRun)
                    yield return change;
            }
        }

        foreach (string pmPath in PredicateMatrixIngest.ResolvePaths(context.EcosystemPath))
        {
            int pmBatch = options.BatchSize > 0 ? options.BatchSize : 4096;
            await foreach (var change in PredicateMatrixIngest.StreamAsync(
                               pmPath, pmBatch, options.Languages, ct))
            {
                if (!options.DryRun)
                    yield return change;
            }
            break;
        }
    }

    private static async IAsyncEnumerable<SubstrateChange> StreamJsonDocumentAsync(
        string path,
        SemLinkGrammarWitness witness,
        string label,
        int batchSize,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        IntPtr recipe = GrammarDecomposer.LookupById(witness.ModalityId);
        if (recipe == IntPtr.Zero) yield break;

        byte[] utf8 = await File.ReadAllBytesAsync(path, ct);
        if (utf8.Length == 0) yield break;

        // One structural parse to locate the top-level pair spans. The AST is small relative to the
        // composed batches; the per-batch compose/witness work below is what we bound and stream.
        var pairSpans = ReadTopLevelPairSpans(utf8, recipe);
        if (pairSpans.Count == 0) yield break;

        int batchIndex = 0;
        for (int start = 0; start < pairSpans.Count; start += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            int end = Math.Min(start + batchSize, pairSpans.Count);
            byte[] subDoc = BuildSubDocument(utf8, pairSpans, start, end);

            var change = ComposeBatch(
                subDoc, recipe, witness, $"{label}/{batchIndex}", recordCount: end - start, ct);
            batchIndex++;
            if (change is not null)
                yield return change;
        }
    }

    /// <summary>
    /// Parses the document and returns the byte span (relative to <paramref name="utf8"/>) of each
    /// top-level pair under the root object. Each span is the full <c>"key": value</c> text and is
    /// concatenated verbatim to reconstruct per-batch sub-documents.
    /// </summary>
    private static List<(uint Start, uint End)> ReadTopLevelPairSpans(byte[] utf8, IntPtr recipe)
    {
        var spans = new List<(uint, uint)>();
        using var ast = GrammarDecomposer.Parse(utf8, recipe);
        int rootObj = JsonGrammarHelper.FindRootObjectNode(ast);
        if (rootObj < 0) return spans;

        for (int i = 0; i < ast.NodeCount; i++)
        {
            var node = ast.GetNode(i);
            if (node.Parent != (uint)rootObj) continue;
            if (ast.NodeTypeName(node.NodeTypeId) != "pair") continue;
            if (node.EndByte <= node.StartByte || node.EndByte > utf8.Length) continue;
            spans.Add((node.StartByte, node.EndByte));
        }
        return spans;
    }

    private static byte[] BuildSubDocument(
        byte[] utf8, List<(uint Start, uint End)> spans, int start, int end)
    {
        long size = 2; // '{' + '}'
        for (int i = start; i < end; i++)
        {
            size += spans[i].End - spans[i].Start;
            if (i > start) size++; // ','
        }

        var buf = new byte[size];
        int w = 0;
        buf[w++] = (byte)'{';
        for (int i = start; i < end; i++)
        {
            if (i > start) buf[w++] = (byte)',';
            int len = (int)(spans[i].End - spans[i].Start);
            Array.Copy(utf8, (int)spans[i].Start, buf, w, len);
            w += len;
        }
        buf[w++] = (byte)'}';
        return buf;
    }

    /// <summary>
    /// Composes one sub-document the same way <see cref="StructuredGrammarIngest"/> composes a whole
    /// JSON document: parse, drain compose entities/physicalities/PRECEDES, then run the witness to
    /// stage CORRESPONDS_TO / ROLE_CORRESPONDS_TO. Hashing and compose are reused; nothing here
    /// reimplements identity.
    /// </summary>
    private static SubstrateChange? ComposeBatch(
        byte[] subDoc, IntPtr recipe, SemLinkGrammarWitness witness,
        string batchLabel, int recordCount, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var ast = GrammarDecomposer.Parse(subDoc, recipe);
        using var composer = new GrammarRowComposer(subDoc, ast, Source, witness.ModalityId);
        var (ents, phys, atts, root) = composer.Materialize(witnessWeight: 1.0);

        var b = new SubstrateChangeBuilder(Source, batchLabel, null,
            entityCapacity: recordCount,
            physicalityCapacity: recordCount,
            attestationCapacity: recordCount * 4);
        foreach (var e in ents) b.AddEntity(e);
        foreach (var p in phys) b.AddPhysicality(p);
        foreach (var a in atts) b.AddAttestation(a);

        var ctx = new GrammarComposeContext(subDoc, ast, root, composer,
            JsonGrammarHelper.FindRootObjectNode(ast));
        witness.WalkRow(ctx, new RowContext(0, recordCount), b);
        return b.SetInputUnitsConsumed(recordCount).Build();
    }

    public async Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default)
    {
        string instancesDir = ResolveInstancesDir(context.EcosystemPath);
        long total = 0;
        foreach (var (path, _, _) in JsonDocumentSpecs(instancesDir))
        {
            IntPtr recipe = GrammarDecomposer.LookupById("json");
            if (recipe == IntPtr.Zero) continue;
            byte[] utf8 = await File.ReadAllBytesAsync(path, ct);
            if (utf8.Length == 0) continue;
            total += ReadTopLevelPairSpans(utf8, recipe).Count;
        }

        foreach (string pmPath in PredicateMatrixIngest.ResolvePaths(context.EcosystemPath))
        {
            long? lines = await PredicateMatrixIngest.EstimateLineCountAsync(pmPath, ct);
            if (lines is not null) total += lines.Value;
            break;
        }

        return total > 0 ? total : null;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static IEnumerable<(string Path, SemLinkDocumentKind Kind, string Label)> JsonDocumentSpecs(string dir)
    {
        string pbVn = Path.Combine(dir, "pb-vn2.json");
        if (File.Exists(pbVn))
            yield return (pbVn, SemLinkDocumentKind.PbVn, "semlink/pb-vn2");

        string vnFn = Path.Combine(dir, "vn-fn2.json");
        if (File.Exists(vnFn))
            yield return (vnFn, SemLinkDocumentKind.VnFn, "semlink/vn-fn2");

        string pbWn = Path.Combine(dir, "pb-wn.json");
        if (File.Exists(pbWn))
            yield return (pbWn, SemLinkDocumentKind.PbWn, "semlink/pb-wn");

        string vnWn = Path.Combine(dir, "vn-wn.json");
        if (File.Exists(vnWn))
            yield return (vnWn, SemLinkDocumentKind.VnWn, "semlink/vn-wn");

        string fnWn = Path.Combine(dir, "fn-wn.json");
        if (File.Exists(fnWn))
            yield return (fnWn, SemLinkDocumentKind.FnWn, "semlink/fn-wn");
    }

    internal static string VnClassFromKey(string key) =>
        SourceEntityIdConventions.VerbNetClassFromSemLinkKey(key);

    internal static string NumericClassId(string classId) =>
        SourceEntityIdConventions.NumericVerbNetClassId(classId);

    private static string ResolveInstancesDir(string ecosystemPath)
    {
        foreach (var c in InstanceDirCandidates(ecosystemPath))
            if (HasJsonMappings(c) || HasPredicateMatrix(c))
                return c;
        return ecosystemPath;
    }

    private static IEnumerable<string> InstanceDirCandidates(string ecosystemPath)
    {
        yield return Path.Combine(ecosystemPath, "semlink-master", "instances");
        yield return Path.Combine(ecosystemPath, "instances");
        yield return ecosystemPath;
    }

    private static bool HasJsonMappings(string dir) =>
        File.Exists(Path.Combine(dir, "pb-vn2.json"))
        || File.Exists(Path.Combine(dir, "vn-fn2.json"))
        || File.Exists(Path.Combine(dir, "pb-wn.json"))
        || File.Exists(Path.Combine(dir, "vn-wn.json"))
        || File.Exists(Path.Combine(dir, "fn-wn.json"));

    private static bool HasPredicateMatrix(string dir) =>
        PredicateMatrixIngest.ExistsLocally(dir);
}
