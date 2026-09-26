using Laplace.Engine.Core;

namespace Laplace.Cli;

/// <summary>
/// Allocates an exported model's d_model into named, disjoint residual strata:
/// <list type="bullet">
///   <item><b>S</b> surface/positional identity (Hilbert content-PE dims and the bias dim).</item>
///   <item><b>W</b> word identity: spectral dims of the word-level graph.</item>
///   <item><b>C</b> sense/concept: spectral dims of the sense graph, the space relation planes
///   are defined over.</item>
///   <item><b>F</b> active frames and role bindings.</item>
///   <item><b>G</b> relation-gate signals: the highway band indicator directions.</item>
/// </list>
/// <para>Widths are counted from a census of current structure, not chosen: G is the band
/// count, F the number of frames with witnessed lexical units, S the PE budget, and W/C the
/// spectral ranks of their graphs.</para>
/// </summary>
internal static class ResidualStrata
{
    /// <summary>The five strata, in allocation order.</summary>
    internal enum Stratum { S = 0, W = 1, C = 2, F = 3, G = 4 }

    /// <summary>One stratum's half-open dim range <c>[Offset, Offset + Width)</c>.</summary>
    internal readonly record struct Block(Stratum Kind, int Offset, int Width)
    {
        public int End => Offset + Width;
        public bool IsEmpty => Width <= 0;
    }

    /// <summary>
    /// The counted inputs. Every field is a census of something the substrate holds; the
    /// layout follows from them.
    /// </summary>
    /// <param name="DModel">Total residual width the recipe casts to.</param>
    /// <param name="PeDims">Positional dims the encoding needs (hilbert content-PE + RoPE).</param>
    /// <param name="WordSpectralRank">Spectral rank of the word-level graph.</param>
    /// <param name="SenseSpectralRank">Spectral rank of the sense/ILI graph.</param>
    /// <param name="FramesWithWitnessedLus">Frames carrying at least one witnessed LU.</param>
    /// <param name="BandCount">Highway salience bands (relation_band_catalog rows).</param>
    internal readonly record struct Census(
        int DModel,
        int PeDims,
        int WordSpectralRank,
        int SenseSpectralRank,
        int FramesWithWitnessedLus,
        int BandCount);

    /// <summary>
    /// Result of an allocation: the five blocks plus what the fit cost.
    /// </summary>
    internal sealed record Layout(
        Block S, Block W, Block C, Block F, Block G,
        int DModel, bool Truncated, double SpectralKept)
    {
        public IEnumerable<Block> Blocks => [S, W, C, F, G];

        /// <summary>The stratum owning a dim, or null for unallocated slack.</summary>
        public Stratum? Owner(int dim)
        {
            foreach (var b in Blocks)
                if (!b.IsEmpty && dim >= b.Offset && dim < b.End) return b.Kind;
            return null;
        }

        public string Describe() =>
            $"strata: S[{S.Offset},{S.End}) W[{W.Offset},{W.End}) C[{C.Offset},{C.End}) "
            + $"F[{F.Offset},{F.End}) G[{G.Offset},{G.End}) of {DModel}"
            + (Truncated ? $" — spectral truncated to {SpectralKept:P1} of counted rank" : "");
    }

    /// <summary>
    /// Allocate d_model into disjoint strata from a census.
    ///
    /// <para>Structural strata (S, F, G) are allocated first and never truncated: each dim is
    /// an entry (a frame, a band, a PE dim), and a missing one cannot be represented. Only the
    /// spectral strata W and C are cut, which is a rank truncation with defined error.</para>
    ///
    /// <para>Throws when d_model cannot hold the structural strata plus two spectral dims each
    /// for W and C.</para>
    /// </summary>
    internal static Layout Allocate(in Census census)
    {
        if (census.DModel <= 0)
            throw new ArgumentOutOfRangeException(nameof(census), "d_model must be positive");

        int s = Math.Max(0, census.PeDims);
        int f = Math.Max(0, census.FramesWithWitnessedLus);
        int g = Math.Max(0, census.BandCount);

        // Minimum two dims per spectral stratum: one direction cannot express a
        // neighbourhood, and a rank-1 subspace collapses every distinction inside it.
        const int MinSpectral = 2;
        int structural = s + f + g;
        int floorNeeded = structural + 2 * MinSpectral;
        if (floorNeeded > census.DModel)
            throw new InvalidOperationException(
                $"d_model={census.DModel} cannot hold the counted strata: S={s} + F={f} + G={g} "
                + $"= {structural} structural dims, leaving {census.DModel - structural} for W and C "
                + $"(need >= {2 * MinSpectral}). Widen the recipe or narrow the ontology scope; "
                + "silently dropping frames or band gates is not an option this returns.");

        int spectralBudget = census.DModel - structural;
        int wWant = Math.Max(MinSpectral, census.WordSpectralRank);
        int cWant = Math.Max(MinSpectral, census.SenseSpectralRank);
        long want = (long)wWant + cWant;

        int w, c;
        bool truncated = want > spectralBudget;
        if (!truncated)
        {
            // Counted rank fits. Slack goes to C, the space relation planes are defined over;
            // W carries identity.
            w = wWant;
            c = spectralBudget - wWant;
        }
        else
        {
            // Proportional cut, then repair the rounding against C for the same reason.
            w = (int)Math.Round((double)wWant / want * spectralBudget, MidpointRounding.AwayFromZero);
            w = Math.Clamp(w, MinSpectral, spectralBudget - MinSpectral);
            c = spectralBudget - w;
        }

        double kept = want <= 0 ? 1.0 : Math.Min(1.0, (double)(w + c) / want);

        // S at the low dims (written by the embedding step); W then C adjacent so identity plus
        // concept is one contiguous span; F and G trail as indicator blocks outside the
        // spectral geometry.
        int off = 0;
        var bS = new Block(Stratum.S, off, s); off += s;
        var bW = new Block(Stratum.W, off, w); off += w;
        var bC = new Block(Stratum.C, off, c); off += c;
        var bF = new Block(Stratum.F, off, f); off += f;
        var bG = new Block(Stratum.G, off, g); off += g;

        if (off != census.DModel)
            throw new InvalidOperationException(
                $"stratum allocation covered {off} of {census.DModel} dims — the layout must be "
                + "exact. An unallocated dim is a direction no head owns, which is precisely "
                + "the anonymous residual stream this replaces.");

        return new Layout(bS, bW, bC, bF, bG, census.DModel, truncated, kept);
    }

    /// <summary>
    /// Block Gram-Schmidt: orthonormalize each stratum's columns within itself and against
    /// every earlier block, so disjoint dim ranges are also orthogonal directions and one
    /// stratum's write does not project onto another's read.
    ///
    /// <para>Operates in place on a row-major <paramref name="basis"/> of
    /// <paramref name="rows"/> x <c>layout.DModel</c>. Returns the number of directions that
    /// collapsed (norm below tolerance after projection) and were zeroed; a nonzero count means
    /// the strata are not independent in the data.</para>
    /// </summary>
    internal static int BlockOrthonormalize(double[] basis, int rows, Layout layout, double tol = 1e-9)
    {
        ArgumentNullException.ThrowIfNull(basis);
        int d = layout.DModel;
        if ((long)rows * d > basis.Length)
            throw new ArgumentException($"basis holds {basis.Length} values, need {(long)rows * d}");

        int collapsed = 0;
        var done = new List<int>(d);

        foreach (var block in layout.Blocks)
        {
            if (block.IsEmpty) continue;
            for (int col = block.Offset; col < block.End; col++)
            {
                // Project out every already-orthonormal direction, earlier blocks included.
                foreach (int prev in done)
                {
                    double dot = 0.0;
                    for (int r = 0; r < rows; r++) dot += basis[(long)r * d + col] * basis[(long)r * d + prev];
                    if (dot == 0.0) continue;
                    for (int r = 0; r < rows; r++) basis[(long)r * d + col] -= dot * basis[(long)r * d + prev];
                }

                double norm = 0.0;
                for (int r = 0; r < rows; r++)
                {
                    double v = basis[(long)r * d + col];
                    norm += v * v;
                }
                norm = Math.Sqrt(norm);

                if (norm <= tol)
                {
                    // Zero it rather than leaving numerical dust that later reads as signal.
                    for (int r = 0; r < rows; r++) basis[(long)r * d + col] = 0.0;
                    collapsed++;
                    continue;
                }

                double inv = 1.0 / norm;
                for (int r = 0; r < rows; r++) basis[(long)r * d + col] *= inv;
                done.Add(col);
            }
        }

        return collapsed;
    }
}
