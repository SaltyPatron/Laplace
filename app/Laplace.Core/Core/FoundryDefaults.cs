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
    public const double AttnGain = 1.0;
    public const double ResidGain = 1.0;
    public const double GateZ = 6.0;
    public const double CtxQk = 8.0;
    public const double CapFrac = 0.05;
    public const bool Ppmi = true;
    public const bool Procrustes = true;
    public const bool CoordOnly = false;
    public const bool CoordDirect = false;
    public const bool Generative = true;
    public const string AttnMetric = "";

    public static int TrajGap(int nLayers) => Math.Max(2, Math.Min(nLayers, 8));

    public static double CoordHeadScale(int headDim) => Math.Sqrt(Math.Max(1, headDim));
}
