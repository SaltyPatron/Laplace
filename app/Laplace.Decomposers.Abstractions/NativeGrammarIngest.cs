using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

/// <summary>
/// Native file ingest: tree-sitter row framing, grammar compose, trunk-containment probe, witness
/// edges, and intent-stage drain all run in <c>laplace_core</c> — one P/Invoke per batch, not per row.
/// </summary>
public static unsafe class NativeGrammarIngest
{
    public static bool CanUseNative(in EtlSource src) =>
        string.Equals(src.Name, "Atomic2020Decomposer", StringComparison.Ordinal)
        || (src.NodeEdgeMap.Count > 0
            && src.Anchor == AnchorResolver.None
            && !EtlWitnessFactory.IsRegistered(src.Name));

    private static readonly NativeInterop.EtlExistProbeFn ProbeCallback = ProbeCallbackImpl;

    public static async IAsyncEnumerable<SubstrateChange> IngestFileAsync(
        string filePath,
        EtlSource src,
        int batchSize,
        string batchLabelPrefix,
        Action<long>? reportUnits,
        Hash128? contextId,
        int commitEpoch,
        long maxInputUnits,
        ISubstrateReader? containmentReader,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!CanUseNative(src))
            throw new InvalidOperationException($"source {src.Name} is not on the native ETL path");

        IntPtr sess = OpenSession(src, contextId);
        if (sess == IntPtr.Zero) yield break;

        try
        {
            int bn = 0;
            long rowsReported = 0;
            long cap = maxInputUnits;
            var probeBox = containmentReader is not null ? new ProbeBox(containmentReader) : null;
            GCHandle probeHandle = default;
            IntPtr probeCtx = IntPtr.Zero;
            NativeInterop.EtlExistProbeFn? probeFn = probeBox is not null ? ProbeCallback : null;

            if (probeBox is not null)
            {
                probeHandle = GCHandle.Alloc(probeBox);
                probeCtx = GCHandle.ToIntPtr(probeHandle);
            }

            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (cap > 0 && rowsReported >= cap) yield break;

                    long fileCap = cap > 0 ? cap - rowsReported : 0;
                    using var stage = IntentStage.New(Math.Max(batchSize * 32, 4096));
                    long emitted = FeedBatch(sess, filePath, batchSize, fileCap, stage, probeFn, probeCtx,
                        out int rc);

                    if (rc < 0) yield break;
                    if (emitted == 0 && rc == 0) break;

                    rowsReported += emitted;
                    reportUnits?.Invoke(rowsReported);

                    var b = new SubstrateChangeBuilder(src.SourceId, $"{batchLabelPrefix}/{bn++}", null,
                            entityCapacity: batchSize, physicalityCapacity: batchSize, attestationCapacity: batchSize * 4)
                        .SetCommitEpoch(commitEpoch)
                        .EnableDeferredContent(containmentReader);
                    b.AddIntentStage(stage);
                    yield return await b.SetInputUnitsConsumed(emitted).BuildAsync(ct);

                    if (rc == 0) break;
                }
            }
            finally
            {
                if (probeHandle.IsAllocated) probeHandle.Free();
            }
        }
        finally
        {
            NativeInterop.EtlSessionClose(sess);
        }
    }

    private static unsafe IntPtr OpenSession(EtlSource src, Hash128? contextId)
    {
        NativeInterop.EtlEdgeRuleNative[]? edgeArray = null;
        int witnessKind = string.Equals(src.Name, "Atomic2020Decomposer", StringComparison.Ordinal)
            ? NativeInterop.EtlWitnessAtomic2020
            : src.NodeEdgeMap.Count > 0
                ? NativeInterop.EtlWitnessFieldEdges
                : NativeInterop.EtlWitnessNone;

        if (witnessKind == NativeInterop.EtlWitnessFieldEdges)
        {
            edgeArray = new NativeInterop.EtlEdgeRuleNative[src.NodeEdgeMap.Count];
            for (int i = 0; i < src.NodeEdgeMap.Count; i++)
            {
                var r = src.NodeEdgeMap[i];
                edgeArray[i] = new NativeInterop.EtlEdgeRuleNative
                {
                    SubjectField = (ushort)r.SubjectField,
                    ObjectField = (ushort)r.ObjectField,
                    SubjectKind = (byte)(r.SubjectKind == EdgeRoleKind.Content ? 0 : 1),
                    ObjectKind = (byte)(r.ObjectKind == EdgeRoleKind.Content ? 0 : 1),
                    RelationSurface = r.RelationType,
                };
            }
        }

        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        IntPtr sess = IntPtr.Zero;

        fixed (NativeInterop.EtlEdgeRuleNative* pRules = edgeArray)
        {
            var cfg = new NativeInterop.EtlConfigNative
            {
                ModalityId = src.Modality.GrammarId,
                SourceId = src.SourceId,
                TypeMetaId = BootstrapIntentBuilder.TypeMetaTypeId,
                WitnessWeight = 1.0,
                TrustWeight = src.Trust,
                NowUnixUs = nowUs,
                WitnessKind = witnessKind,
                EdgeRules = edgeArray is null ? IntPtr.Zero : (IntPtr)pRules,
                EdgeRuleCount = (nuint)(edgeArray?.Length ?? 0),
                ContextId = contextId ?? default,
                ContextIsNull = (byte)(contextId is null ? 1 : 0),
                SkipCommentRows = (byte)(src.AcceptCommentRows ? 0 : 1),
            };
            NativeInterop.EtlSessionOpen(&cfg, &sess);
        }

        return sess;
    }

    private static unsafe long FeedBatch(
        IntPtr sess,
        string filePath,
        int batchSize,
        long fileCap,
        IntentStage stage,
        NativeInterop.EtlExistProbeFn? probeFn,
        IntPtr probeCtx,
        out int rc)
    {
        var stats = default(NativeInterop.EtlStatsNative);
        rc = NativeInterop.EtlSessionFeedFile(
            sess, filePath, (nuint)batchSize, (nuint)Math.Max(0, fileCap),
            stage.DangerousNativeHandle, probeFn, probeCtx, &stats);
        return (long)stats.RowsEmitted;
    }

    private sealed class ProbeBox(ISubstrateReader reader)
    {
        public readonly ISubstrateReader Reader = reader;
    }

    private static unsafe int ProbeCallbackImpl(
        IntPtr ctx, Hash128* ids, nuint n, byte* outBitmap, nuint bitmapBits)
    {
        try
        {
            var box = (ProbeBox)GCHandle.FromIntPtr(ctx).Target!;
            int count = (int)n.ToUInt64();
            if (count == 0) return 0;
            if (bitmapBits < (nuint)count) return -1;

            var candidates = new Hash128[count];
            for (int i = 0; i < count; i++) candidates[i] = ids[i];

            byte[] bm = box.Reader.EntitiesExistBitmapAsync(candidates, CancellationToken.None)
                .GetAwaiter().GetResult();

            int need = (count + 7) / 8;
            for (int i = 0; i < need; i++) outBitmap[i] = i < bm.Length ? bm[i] : (byte)0;
            return 0;
        }
        catch
        {
            return -1;
        }
    }
}
