namespace Laplace.Engine.Core;

/// <summary>
/// Constants of the model-export recipe: how current consensus standing, trajectories,
/// and geometry are read and written into the exported operator tensors.
/// </summary>
public static class FoundryDefaults
{
    public const int CrawlSeeds = 1000;
    public const int WordTrajs = 400_000;

    /// Faithful writer: per-row cap and the relation-rank band [lo, hi] of the knowledge
    /// readout plane. The band excludes low-rank order/metadata relation types and must
    /// include the rank of the hypernym relation.
    public const int FaithfulCap = 128;
    public const double FaithfulRankLo = 0.55;
    public const double FaithfulRankHi = 0.95;
    public const bool FaithfulKnowledge = true;
    public const bool FaithfulTrajOrder = true;
    public const bool FaithfulLookup = true;
    public const int LeDegree = 48;
    public const int MetricK = 16;
    public const int MetricProbe = 64;
    public const int CorpusMax = 200_000;
    public const int BasisRank = 256;
    public const int DenseSvdMax = 6000;
    public const int RsvdOversample = 16;
    public const int RsvdPower = 1;
    public const double MetricBasisGain = 4.0;
    public const double CoordScale = 20.0;
    public const double RelErrTol = 0.0;
    /// Attention and residual block gains (scaled by nLayers^-1/4): block outputs perturb
    /// the residual stream rather than accumulate over the token embedding.
    public const double AttnGain = 0.5;
    public const double ResidGain = 0.5;
    /// Layer output scale when the embedding carries the conditional floor (embed op
    /// "conditional"): correction layers are log-linear perturbations of the calibrated
    /// floor and do not overwrite it.
    public const double FloorCorrectionGain = 0.10;
    /// Weight of the class-transition table in its log-linear sum into the conditional
    /// floor (embed op "conditional_pos"); 1.0 is the plain sum.
    public const double PosFloorGain = 1.0;
    public const double GateZ = 6.0;
    public const double CtxQk = 8.0;
    public const double CapFrac = 0.05;
    /// PPMI reweighting of the adjacency plane. Off: trajectory synthesis carries the
    /// rated continuation evidence unchanged; PPMI would drop every non-positive PMI edge
    /// before operator construction.
    public const bool Ppmi = false;
    public const bool Procrustes = true;
    /// Synthesized QK operators are content-relational, and llama-arch RoPE would rotate
    /// them by absolute position. True writes rope.freq_base=1e9, flattening every rotary
    /// pair except pair 0 (which rotates at frequency 1 regardless of theta), and leaves
    /// rotary pair 0 unfilled when writing heads.
    public const bool DisableRope = true;
    /// Scale of the Hilbert content position encoding written into the trailing capacity
    /// dims of the embedding (content dims are row-normalized to 1).
    public const double HilbertPeScale = 0.25;
    /// Highway salience band count — the width of the relation-gate stratum (one indicator
    /// direction per band). Equals the row count of converse.relation_band_catalog()
    /// (mandate..probationary, bands 0..12) and changes only with that catalog.
    public const int HighwayBandCount = 13;
    /// Exact normalized SVD factorization. Factor() normalizes the operator by its
    /// leading singular value before splitting each singular ratio across both
    /// factors. Alpha=1 preserves that normalized operator's spectrum; rank truncation
    /// is the only declared loss to target width.
    public const double FactorSpectrumAlpha = 1.0;
    public static readonly bool CoordOnly = false;
    public const bool CoordDirect = false;
    public const bool Generative = true;
    public const string AttnMetric = "";

    public static int TrajGap(int nLayers) => Math.Max(2, Math.Min(nLayers, 8));

    public static double CoordHeadScale(int headDim) => Math.Sqrt(Math.Max(1, headDim));
}
