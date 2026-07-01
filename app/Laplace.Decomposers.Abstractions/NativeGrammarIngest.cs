using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

public static unsafe class NativeGrammarIngest
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, UsesNativeIngestAttribute>
        s_attrs = new(StringComparer.Ordinal);

    public static void RegisterType<T>() where T : class
    {
        var attr = typeof(T).GetCustomAttributes(typeof(UsesNativeIngestAttribute), false)
                            .OfType<UsesNativeIngestAttribute>().FirstOrDefault();
        if (attr is not null) s_attrs[typeof(T).Name] = attr;
    }

    public static bool CanUseNative(in EtlSource src, DecomposerOptions? options = null)
    {
        if (s_attrs.TryGetValue(src.Name, out var attr))
        {
            if (attr.RequiresEnvOpt)
            {
                if (options?.Languages?.IsActive == true) return false;
                return string.Equals(
                    Environment.GetEnvironmentVariable("LAPLACE_INGEST_NATIVE"), "1",
                    StringComparison.Ordinal);
            }
            return true;
        }

        return src.NodeEdgeMap.Count > 0
               && src.Anchor is AnchorResolver.None or AnchorResolver.IliSynset
               && !EtlWitnessFactory.IsRegistered(src.Name);
    }

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
        DecomposerOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!CanUseNative(src, options))
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
                    var stage = IntentStage.New(Math.Max(batchSize * 32, 4096));
                    long emitted = FeedBatch(sess, filePath, batchSize, fileCap, stage, probeFn, probeCtx,
                        acceptFn: null, acceptCtx: IntPtr.Zero, out int rc);

                    if (rc < 0)
                    {
                        stage.Dispose();
                        yield break;
                    }
                    if (emitted == 0 && rc == 0)
                    {
                        stage.Dispose();
                        break;
                    }

                    rowsReported += emitted;
                    reportUnits?.Invoke(rowsReported);

                    var b = new SubstrateChangeBuilder(src.SourceId, $"{batchLabelPrefix}/{bn++}", null,
                            entityCapacity: batchSize, physicalityCapacity: batchSize, attestationCapacity: batchSize * 4)
                        .SetCommitEpoch(commitEpoch);
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
        if (src.Anchor == AnchorResolver.IliSynset)
            Environment.SetEnvironmentVariable("LAPLACE_CILI_DIR", SourceEntityIdConventions.CiliDirectory());

        int witnessKind = ResolveWitnessKind(src);
        var allocs = new List<IntPtr>();
        try
        {
            NativeInterop.EtlEdgeRuleNative[]? edgeArray = null;
            if (witnessKind == NativeInterop.EtlWitnessFieldEdges)
            {
                edgeArray = new NativeInterop.EtlEdgeRuleNative[src.NodeEdgeMap.Count];
                for (int i = 0; i < src.NodeEdgeMap.Count; i++)
                {
                    var r = src.NodeEdgeMap[i];
                    IntPtr rel = Marshal.StringToCoTaskMemUTF8(r.RelationType);
                    allocs.Add(rel);
                    edgeArray[i] = new NativeInterop.EtlEdgeRuleNative
                    {
                        SubjectField = (ushort)r.SubjectField,
                        ObjectField = (ushort)r.ObjectField,
                        SubjectKind = (byte)(r.SubjectKind == EdgeRoleKind.Content ? 0 : 1),
                        ObjectKind = (byte)(r.ObjectKind == EdgeRoleKind.Content ? 0 : 1),
                        RelationSurface = rel,
                    };
                }
            }

            long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
            IntPtr modalityId = Marshal.StringToCoTaskMemUTF8(src.Modality.GrammarId);
            allocs.Add(modalityId);
            IntPtr sess = IntPtr.Zero;

            fixed (NativeInterop.EtlEdgeRuleNative* pRules = edgeArray)
            {
                var cfg = new NativeInterop.EtlConfigNative
                {
                    ModalityId = modalityId,
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
                    LineFramed = (byte)(src.Modality.RecordFraming == GrammarRecordFraming.Line ? 1 : 0),
                };
                NativeInterop.EtlSessionOpen(&cfg, &sess);
            }
            return sess;
        }
        finally
        {
            foreach (var p in allocs) Marshal.FreeCoTaskMem(p);
        }
    }

    private static int ResolveWitnessKind(EtlSource src)
    {
        if (string.Equals(src.Name, "Atomic2020Decomposer", StringComparison.Ordinal))
            return NativeInterop.EtlWitnessAtomic2020;
        if (string.Equals(src.Name, "ConceptNetDecomposer", StringComparison.Ordinal))
            return NativeInterop.EtlWitnessConceptNet;
        if (src.NodeEdgeMap.Count > 0)
            return NativeInterop.EtlWitnessFieldEdges;
        return NativeInterop.EtlWitnessNone;
    }

    private static unsafe long FeedBatch(
        IntPtr sess,
        string filePath,
        int batchSize,
        long fileCap,
        IntentStage stage,
        NativeInterop.EtlExistProbeFn? probeFn,
        IntPtr probeCtx,
        NativeInterop.EtlAcceptRowFn? acceptFn,
        IntPtr acceptCtx,
        out int rc)
    {
        var stats = default(NativeInterop.EtlStatsNative);
        rc = NativeInterop.EtlSessionFeedFile(
            sess, filePath, (nuint)batchSize, (nuint)Math.Max(0, fileCap),
            stage.DangerousNativeHandle, probeFn, probeCtx, acceptFn, acceptCtx, &stats);
        return (long)stats.RowsEmitted;
    }

    private sealed class ProbeBox(ISubstrateReader reader)
    {
        public readonly ISubstrateReader Reader = reader;
    }

    private static unsafe int ProbeCallbackImpl(
        IntPtr ctx, Hash128* ids, int* parents, nuint n, byte* outBitmap, nuint bitmapBits)
    {
        try
        {
            var box = (ProbeBox)GCHandle.FromIntPtr(ctx).Target!;
            int count = (int)n.ToUInt64();
            if (count == 0) return 0;
            if (bitmapBits < (nuint)count) return -1;

            var candidates = new Hash128[count];
            for (int i = 0; i < count; i++) candidates[i] = ids[i];

            byte[] bm;
            if (parents != null)
            {
                var parentList = new int[count];
                for (int i = 0; i < count; i++) parentList[i] = parents[i];
                bm = box.Reader.ContentDescentBitmapAsync(candidates, parentList, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            else
            {
                bm = box.Reader.EntitiesExistBitmapAsync(candidates, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }

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
