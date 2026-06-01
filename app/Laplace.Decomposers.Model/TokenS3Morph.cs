using Microsoft.Extensions.Logging;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using DynInterop = Laplace.Engine.Dynamics.NativeInterop;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Model embedding → S³ morph (SUBSTRATE-FOUNDATION truth #3).
///
/// The S³ glome is the canonical, Unicode-anchored embedding frame. A model is a
/// witness: its <c>embed_tokens</c> table is morphed ONTO that shared frame — it is
/// NOT a conventional per-model embedding. Each token becomes a Projection physicality
/// placing the model's view of that token on the shared frame, alongside the token's
/// canonical Content physicality (and every other source's Projection of it) — that
/// shared frame is the cross-model consensus moat.
///
/// The morph is the exact ratified pipeline, using the engine's real dynamics kernels
/// (NOT a PCA/SVD stand-in):
///
///   AI model embedding
///     → Laplacian eigenmaps      (engine <c>laplacian_eigenmaps</c>): the embedding's
///        local-neighbourhood manifold structure, reduced to a low-dim basis via the
///        spectrum of its k-NN graph Laplacian — preserves *which tokens are near which*,
///        not global variance directions (that is what PCA would give, and is wrong here).
///     → Gram-Schmidt             (engine <c>gram_schmidt_orthonormalize</c>): orthonormalize
///        the eigenmap basis directions before alignment (Eigen HouseholderQR).
///     → Procrustes alignment     (engine <c>procrustes_fit</c>/<c>_apply</c>): the rigid
///        rotation + scale + translation that lands the orthonormal eigenmap basis onto the
///        S³ content coords of the tokens in question (their Unicode-anchored placements).
///
/// This reads the embedding table (truth #1) — no recompute, no GEMM, no vocab² blowup.
/// The interior q/k/v/o/gate/up/down resolution is OPEN (foundation doc §47) and is
/// deliberately NOT emitted here — flagged, not fabricated.
///
/// Scaling: the dense <c>laplacian_eigenmaps</c> builds its k-NN graph by brute force
/// (O(n²·d_model), single-threaded — see engine/dynamics/src/eigenmaps.cpp). That is
/// fine for this model's anchorable vocab as a one-time ingest, but does NOT scale to
/// frontier vocabularies; a sublinear k-NN (→ <c>laplacian_eigenmaps_from_sparse_graph</c>)
/// or an out-of-sample (Nyström) extension is the scaling step, and is left explicitly
/// open rather than faked.
/// </summary>
public sealed class TokenS3Morph
{
    private const int S3            = 4;    // S³ ⊂ ℝ⁴
    private const int EigTargetDim  = 64;   // eigenmap basis dim (mirrors the synthesis spectral basis)
    private const int KNeighbors    = 15;   // k-NN graph degree for the Laplacian

    private readonly float[] _embed;   // [vocab × dModel] row-major, f32 (decoded)
    private readonly int _vocab;
    private readonly int _dModel;
    private readonly IReadOnlyList<LlamaTokenizerParser.TokenRecord> _tokens;
    private readonly Hash128 _sourceId;
    private readonly ILogger _log;

    public TokenS3Morph(
        float[] embed, int vocab, int dModel,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Hash128 sourceId, ILogger log)
    {
        _embed = embed; _vocab = vocab; _dModel = dModel;
        _tokens = tokens; _sourceId = sourceId; _log = log;
    }

    public IEnumerable<SubstrateChange> Emit()
    {
        // 1. The tokens in question: anchorable vocab with a precomputed Unicode-anchored
        //    S³ content coord (LlamaTokenizerParser.Parse → TokenRecord.Content*, the
        //    documented Procrustes target). rec.EntityId is the exact entity the vocab
        //    phase emitted (FK-safe); rec.TokenId is the embed-table row. Specials and
        //    invalid-UTF-8 byte-level tokens have no content anchor and are skipped.
        var rowIdx  = new List<int>(_vocab);       // embed row index (= TokenId)
        var rootIds = new List<Hash128>(_vocab);
        var anchors = new List<double[]>(_vocab);  // S³ targets B, [n × 4]
        foreach (var rec in _tokens)
        {
            if (!rec.HasContentCoord) continue;
            if (rec.TokenId < 0 || rec.TokenId >= _vocab) continue;
            rowIdx.Add(rec.TokenId);
            rootIds.Add(rec.EntityId);
            anchors.Add(new[] { rec.ContentX, rec.ContentY, rec.ContentZ, rec.ContentM });
        }
        int n = rowIdx.Count;
        int targetDim = Math.Min(EigTargetDim, Math.Min(_dModel, n - 2));
        int k = Math.Min(KNeighbors, n - 1);
        if (n < S3 + 2 || targetDim < S3 || k < 1)
        {
            _log.LogWarning("S3-morph: only {N} anchorable tokens (need ≥{Min}); skipping morph", n, S3 + 2);
            return Array.Empty<SubstrateChange>();
        }

        // 2. Gather the embedding rows for the anchorable tokens (f32 → f64).
        var src = new double[(long)n * _dModel];
        for (int i = 0; i < n; i++)
        {
            long s = (long)rowIdx[i] * _dModel;
            for (int j = 0; j < _dModel; j++) src[(long)i * _dModel + j] = _embed[s + j];
        }

        // 3. Laplacian eigenmaps: embedding manifold → targetDim coords [n × targetDim].
        var Y = new double[(long)n * targetDim];
        int rc;
        unsafe
        {
            fixed (double* ps = src) fixed (double* py = Y)
                rc = DynInterop.LaplacianEigenmaps(
                    ps, (nuint)n, (nuint)_dModel, (nuint)k, (nuint)targetDim, py);
        }
        if (rc != 0)
        {
            _log.LogWarning("S3-morph: laplacian_eigenmaps rc={Rc} (n={N},d={D},k={K},target={T}); skipping morph",
                rc, n, _dModel, k, targetDim);
            return Array.Empty<SubstrateChange>();
        }

        // 4. Gram-Schmidt orthonormalize the targetDim basis DIRECTIONS. GS orthonormalizes
        //    n_vecs row-vectors of length dim and requires n_vecs ≤ dim — so the data must be
        //    laid out [targetDim × n] (each row one direction over the n tokens); calling it
        //    on the token-major [n × targetDim] layout would return -2 / scramble (the layout
        //    bug). Transpose in, orthonormalize, transpose back.
        var Yt = new double[(long)targetDim * n];
        for (int i = 0; i < n; i++)
            for (int d = 0; d < targetDim; d++) Yt[(long)d * n + i] = Y[(long)i * targetDim + d];
        int gsRc;
        unsafe { fixed (double* pyt = Yt) gsRc = DynInterop.GramSchmidtOrthonormalize(pyt, (nuint)targetDim, (nuint)n); }
        if (gsRc == 0)
            for (int i = 0; i < n; i++)
                for (int d = 0; d < targetDim; d++) Y[(long)i * targetDim + d] = Yt[(long)d * n + i];
        else
            _log.LogWarning("S3-morph: gram_schmidt_orthonormalize rc={Rc}; using raw eigenmap basis", gsRc);

        // 5. Procrustes: fit the orthonormal eigenmap basis [n × targetDim] onto the tokens'
        //    S³ content coords [n × 4], then apply per token. (Schönemann + Umeyama, engine SVD.)
        var B = new double[(long)n * S3];
        for (int i = 0; i < n; i++)
        {
            double[] b = anchors[i];
            B[(long)i * S3 + 0] = b[0]; B[(long)i * S3 + 1] = b[1];
            B[(long)i * S3 + 2] = b[2]; B[(long)i * S3 + 3] = b[3];
        }
        IntPtr T;
        unsafe
        {
            fixed (double* py = Y) fixed (double* pb = B)
                T = DynInterop.ProcrustesFit(py, (nuint)n, (nuint)targetDim, pb);
        }
        if (T == IntPtr.Zero)
        {
            _log.LogWarning("S3-morph: procrustes_fit failed (n={N},source_dim={T}); skipping morph", n, targetDim);
            return Array.Empty<SubstrateChange>();
        }

        try
        {
            double globalResid = DynInterop.ProcrustesResidual(T);
            _log.LogInformation(
                "phase=S3-morph: eigenmaps(target_dim={Td},k={K}) → gram-schmidt → procrustes "
                + "(residual={R:F4}) over {N} anchored tokens", targetDim, k, globalResid, n);

            // 6. Apply the transform per token, re-normalize onto S³, emit one Projection
            //    physicality per token (with the per-token alignment residual + source dim).
            long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
            var bb = new SubstrateChangeBuilder(_sourceId, "model/embed-s3-morph",
                entityCapacity: 0, physicalityCapacity: n, attestationCapacity: 0);
            var outv = new double[S3];
            int emitted = 0;
            for (int i = 0; i < n; i++)
            {
                unsafe
                {
                    fixed (double* py = &Y[(long)i * targetDim]) fixed (double* po = outv)
                        DynInterop.ProcrustesApply(T, py, (nuint)targetDim, po);
                }
                double x = outv[0], y = outv[1], z = outv[2], m = outv[3];
                double norm = Math.Sqrt(x * x + y * y + z * z + m * m);
                if (norm < 1e-12 || double.IsNaN(norm)) continue;   // degenerate; cannot place on S³
                x /= norm; y /= norm; z /= norm; m /= norm;

                double[] b = anchors[i];
                double resid = Math.Sqrt((x - b[0]) * (x - b[0]) + (y - b[1]) * (y - b[1])
                                       + (z - b[2]) * (z - b[2]) + (m - b[3]) * (m - b[3]));

                Hash128 entityId = rootIds[i];
                Hilbert128 hb = Hilbert128.Encode(new[] { x, y, z, m });
                Hash128 physId = PhysicalityId.Compute(
                    entityId, _sourceId, PhysicalityKind.Projection,
                    x, y, z, m, ReadOnlySpan<double>.Empty);

                bb.AddPhysicality(new PhysicalityRow(
                    Id:                physId,
                    EntityId:          entityId,
                    SourceId:          _sourceId,
                    Kind:              PhysicalityKind.Projection,
                    CoordX:            x, CoordY: y, CoordZ: z, CoordM: m,
                    HilbertIndex:      hb,
                    TrajectoryXyzm:    null,
                    NConstituents:     0,
                    AlignmentResidual: resid,
                    SourceDim:         _dModel,
                    ObservedAtUnixUs:  nowUs));
                emitted++;
            }
            _log.LogInformation("phase=S3-morph: {Emitted} Projection physicalities placed "
                + "({N} anchored / {Vocab} vocab)", emitted, n, _vocab);
            return new[] { bb.Build() };
        }
        finally
        {
            DynInterop.ProcrustesFree(T);
        }
    }
}
