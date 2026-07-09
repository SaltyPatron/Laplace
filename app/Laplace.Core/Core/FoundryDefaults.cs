namespace Laplace.Engine.Core;

/// <summary>Foundry synthesis knobs — constants in code, not env or config files.</summary>
public static class FoundryDefaults
{
    public const int CrawlSeeds = 1000;
    public const int WordTrajs = 400_000;
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
    /// 2026-07-09: block outputs must PERTURB the residual, not bury it — at
    /// depth 4, gain-1.0 additions (~13/layer) out-accumulate a norm-sqrt(d)
    /// token 3:1 and the final state goes shared-dominated (cos 0.99 measured).
    public const double AttnGain = 0.5;
    public const double ResidGain = 0.5;
    /// Conditional-floor pours: correction layers must ride ON the calibrated
    /// floor (MaxEnt: log-linear perturbations), not overwrite it — measured
    /// 2026-07-09: at full scale the layers erased the determiner slot
    /// (the→close/short became the→before/after; Spearman L0↔full 0.1).
    /// Gain sweep 2026-07-09 (16-probe likelihood, V=7134): correction planes
    /// are tail-only — hits@50 flat 0.448 at every gain, MRR never exceeds the
    /// floor's, mean rank improves monotonically with gain. 0.10 is the Pareto
    /// knee (mean rank 1675→1430, MRR −2.8%, canary Spearman 0.54).
    /// Output scales are multiplied by this when embed op == "conditional".
    public const double FloorCorrectionGain = 0.10;
    /// Phase 4 v1b: MaxEnt log-linear sum of the class-transition table into the
    /// conditional floor (embed op "conditional_pos") — 1.0 is the plain sum.
    public const double PosFloorGain = 1.0;
    public const double GateZ = 6.0;
    public const double CtxQk = 8.0;
    public const double CapFrac = 0.05;
    public const bool Ppmi = true;
    public const bool Procrustes = true;
    /// Plan Phase 0 (2026-07-08 rope-probe verdict: CORRUPTS, 191% drift): poured
    /// QK operators are content-relational; llama-arch RoPE rotates them by absolute
    /// position. True = write rope.freq_base=1e9, flattening every rotary pair
    /// except pair 0 (which rotates at frequency 1 regardless of theta — residual
    /// exposure ~2/headDim of Q·K energy; re-probe each pour, gate at 0.15 drift).
    public const bool DisableRope = true;
    /// Plan Phase 5 (doc 14 P7): scale of the hilbert content-PE written into the
    /// trailing capacity dims of the embedding (content dims are row-normalized to 1).
    public const double HilbertPeScale = 0.25;
    /// 2026-07-08 rank-collapse fix (doc 14 M3 at the OPERATOR level): factored
    /// plane spectra are hub-dominated (observed 29:1 V-row ratio → effectively
    /// rank-1 operators → prompt-independent final states, logit corr 1.0000).
    /// Factor scales direction r by (s_r/s0)^alpha; 0.5 was the old sqrt (each side
    /// sqrt(s/s0)); 0.25 flattens the spectrum enough for sub-dominant structure
    /// to survive the stack while preserving ordering.
    public const double FactorSpectrumAlpha = 0.25;
    public const bool CoordOnly = false;
    public const bool CoordDirect = false;
    public const bool Generative = true;
    public const string AttnMetric = "";

    public static int TrajGap(int nLayers) => Math.Max(2, Math.Min(nLayers, 8));

    public static double CoordHeadScale(int headDim) => Math.Sqrt(Math.Max(1, headDim));
}
