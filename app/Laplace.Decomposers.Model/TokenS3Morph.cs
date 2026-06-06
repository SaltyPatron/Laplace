using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
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
    private readonly Hash128 _tokenizerEntityId;
    private readonly ILogger _log;

    public TokenS3Morph(
        float[] embed, int vocab, int dModel,
        IReadOnlyList<LlamaTokenizerParser.TokenRecord> tokens,
        Hash128 sourceId, Hash128 tokenizerEntityId, ILogger log)
    {
        _embed = embed; _vocab = vocab; _dModel = dModel;
        _tokens = tokens; _sourceId = sourceId; _tokenizerEntityId = tokenizerEntityId;
        _log = log;
    }

    public IEnumerable<SubstrateChange> Emit()
    {
        // 1. TWO different sets, deliberately (the 2026-06-05 correction —
        //    "fit on anchors, place ALL"):
        //    - The FIT set: anchorable vocab with a precomputed Unicode-anchored
        //      S³ content coord (LlamaTokenizerParser.Parse → TokenRecord.Content*,
        //      the documented Procrustes target). Alignment ground truth exists
        //      only here (TinyLlama: 31,869 of 32,000).
        //    - The PLACEMENT set: EVERY vocab row. The model witnesses geometry
        //      for specials and invalid-UTF-8 byte-level tokens too — their
        //      embedding rows exist — so the fitted transform APPLIES to all of
        //      them; they are placed without an alignment residual (NULL: no
        //      anchor to measure against). rec.EntityId is the exact entity the
        //      vocab phase emitted (FK-safe); rec.TokenId is the embed-table row.
        var fitIdx     = new List<int>(_vocab);       // index INTO the full eigenmap rows
        var anchors    = new List<double[]>(_vocab);  // S³ targets B, [nFit × 4]
        var rows       = new List<LlamaTokenizerParser.TokenRecord>(_vocab);
        foreach (var rec in _tokens)
        {
            if (rec.TokenId < 0 || rec.TokenId >= _vocab) continue;
            rows.Add(rec);
        }
        int n = rows.Count;                            // full placement set
        for (int i = 0; i < n; i++)
        {
            var rec = rows[i];
            if (!rec.HasContentCoord) continue;
            fitIdx.Add(i);
            anchors.Add(new[] { rec.ContentX, rec.ContentY, rec.ContentZ, rec.ContentM });
        }
        int nFit = fitIdx.Count;
        int targetDim = Math.Min(EigTargetDim, Math.Min(_dModel, n - 2));
        int k = Math.Min(KNeighbors, n - 1);
        if (nFit < S3 + 2 || targetDim < S3 || k < 1)
        {
            _log.LogWarning("S3-morph: only {N} anchorable tokens (need ≥{Min}); skipping morph", nFit, S3 + 2);
            return Array.Empty<SubstrateChange>();
        }

        // 2. Gather the embedding rows for the FULL vocab (f32 → f64).
        var src = new double[(long)n * _dModel];
        for (int i = 0; i < n; i++)
        {
            long s = (long)rows[i].TokenId * _dModel;
            for (int j = 0; j < _dModel; j++) src[(long)i * _dModel + j] = _embed[s + j];
        }

        // 3. Laplacian eigenmaps over the FULL vocab manifold → [n × targetDim].
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

        // 5. Procrustes: FIT on the anchored correspondences only (the eigenmap
        //    rows of the anchorable tokens [nFit × targetDim] onto their S³
        //    content coords [nFit × 4] — you can only align where ground truth
        //    exists), then APPLY the fitted transform to every vocab row.
        //    (Schönemann + Umeyama, engine SVD.)
        var Yfit = new double[(long)nFit * targetDim];
        for (int f = 0; f < nFit; f++)
            Array.Copy(Y, (long)fitIdx[f] * targetDim, Yfit, (long)f * targetDim, targetDim);
        var B = new double[(long)nFit * S3];
        for (int f = 0; f < nFit; f++)
        {
            double[] b = anchors[f];
            B[(long)f * S3 + 0] = b[0]; B[(long)f * S3 + 1] = b[1];
            B[(long)f * S3 + 2] = b[2]; B[(long)f * S3 + 3] = b[3];
        }
        IntPtr T;
        unsafe
        {
            fixed (double* py = Yfit) fixed (double* pb = B)
                T = DynInterop.ProcrustesFit(py, (nuint)nFit, (nuint)targetDim, pb);
        }
        if (T == IntPtr.Zero)
        {
            _log.LogWarning("S3-morph: procrustes_fit failed (n={N},source_dim={T}); skipping morph", nFit, targetDim);
            return Array.Empty<SubstrateChange>();
        }

        try
        {
            double globalResid = DynInterop.ProcrustesResidual(T);
            _log.LogInformation(
                "phase=S3-morph: eigenmaps(target_dim={Td},k={K}) over {N} vocab rows → gram-schmidt → "
                + "procrustes fit on {NFit} anchored (residual={R:F4})", targetDim, k, n, nFit, globalResid);

            // 6. APPLY pass: transform every vocab row, re-normalize onto S³,
            //    record per-token residuals where an anchor exists. The residual
            //    is then THE metric of the TOKEN_MAPS_TO matchup ("the distance
            //    becomes a metric in the Glicko-2 match" — 2026-06-05 ruling),
            //    so the arena scale M = RMS(resid) over the anchored set is
            //    measured BEFORE any score is assigned — never a knob.
            long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
            var outv = new double[S3];
            var px = new double[n]; var pyv = new double[n];
            var pz = new double[n]; var pm = new double[n];
            var resids = new double?[n];
            var placed = new bool[n];
            double sumSq = 0; int nResid = 0;
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
                px[i] = x / norm; pyv[i] = y / norm; pz[i] = z / norm; pm[i] = m / norm;
                placed[i] = true;

                var rec = rows[i];
                if (rec.HasContentCoord)
                {
                    double r = Math.Sqrt((px[i] - rec.ContentX) * (px[i] - rec.ContentX)
                                       + (pyv[i] - rec.ContentY) * (pyv[i] - rec.ContentY)
                                       + (pz[i] - rec.ContentZ) * (pz[i] - rec.ContentZ)
                                       + (pm[i] - rec.ContentM) * (pm[i] - rec.ContentM));
                    resids[i] = r;
                    sumSq += r * r; nResid++;
                }
            }
            double arenaM = nResid > 0 ? Math.Sqrt(sumSq / nResid) : 0.0;

            // 7. EMIT pass: one Projection physicality per placed token
            //    (alignment_residual NULL where unanchored) + one TOKEN_MAPS_TO
            //    matchup per placed token:
            //      anchored  → magnitude m = (M − resid), arena scale M: a token
            //                  landing on its content coordinate is a strong win
            //                  (~0.76), the typical distance is a draw, far
            //                  outliers are losses — ONE measured quantity is
            //                  both center and scale;
            //      unanchored (specials / non-UTF-8 bytes) or degenerate M →
            //                  categorical confirm at registry rank.
            var bb = new SubstrateChangeBuilder(_sourceId, "model/embed-s3-morph",
                entityCapacity: 0, physicalityCapacity: n, attestationCapacity: n);
            int emitted = 0, emittedAnchored = 0;
            for (int i = 0; i < n; i++)
            {
                if (!placed[i]) continue;
                var rec = rows[i];
                Hash128 entityId = rec.EntityId;
                Hilbert128 hb = Hilbert128.Encode(new[] { px[i], pyv[i], pz[i], pm[i] });
                Hash128 physId = PhysicalityId.Compute(
                    entityId, _sourceId, PhysicalityType.Projection,
                    px[i], pyv[i], pz[i], pm[i], ReadOnlySpan<double>.Empty);

                bb.AddPhysicality(new PhysicalityRow(
                    Id:                physId,
                    EntityId:          entityId,
                    SourceId:          _sourceId,
                    Type:              PhysicalityType.Projection,
                    CoordX:            px[i], CoordY: pyv[i], CoordZ: pz[i], CoordM: pm[i],
                    HilbertIndex:      hb,
                    TrajectoryXyzm:    null,
                    NConstituents:     0,
                    AlignmentResidual: resids[i],
                    SourceDim:         _dModel,
                    ObservedAtUnixUs:  nowUs));
                emitted++;

                if (resids[i] is double resid && arenaM > 0)
                {
                    bb.AddAttestation(RelationTypeRegistry.AttestWeighted(
                        _tokenizerEntityId, "TOKEN_MAPS_TO", entityId, _sourceId,
                        SourceTrust.AiModelProbe,
                        magnitude: arenaM - resid, arenaScale: arenaM));
                    emittedAnchored++;
                }
                else
                {
                    bb.AddAttestation(RelationTypeRegistry.Attest(
                        _tokenizerEntityId, "TOKEN_MAPS_TO", entityId, _sourceId,
                        SourceTrust.AiModelProbe));
                }
            }
            _log.LogInformation("phase=S3-morph: {Emitted} Projection placements + TOKEN_MAPS_TO matchups "
                + "({Anchored} residual-scored at M={M:F6} / {Vocab} vocab — specials + byte tokens categorical)",
                emitted, emittedAnchored, arenaM, _vocab);
            return new[] { bb.Build() };
        }
        finally
        {
            DynInterop.ProcrustesFree(T);
        }
    }
}
