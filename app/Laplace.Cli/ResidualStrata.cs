using Laplace.Engine.Core;

namespace Laplace.Cli;

/// <summary>
/// Typed residual stratum allocation — docs/specs/18 §2, GH #521.
///
/// <para>The residual stream is anonymous today: <c>BuildBasis</c> lays a single spectral
/// basis across [0, k) and reserves a hilbert PE tail, and every head thereafter reads and
/// writes the whole width. That is what makes a WSD head able to clobber a frame head, and
/// it is why <c>embed = I</c> was the honest fallback — with no named subspaces there is
/// nothing for a composed representation to compose *into*.</para>
///
/// <para>This allocates d_model into named, disjoint blocks:</para>
/// <list type="bullet">
///   <item><b>S</b> surface/positional identity — which token, where (hilbert content-PE
///   dims + RoPE sequence position).</item>
///   <item><b>W</b> word/lemma identity — the tier-2 entity, spectral dims of the
///   word-level graph.</item>
///   <item><b>C</b> sense/ILI concept — the internal lingua franca. Every semantic relation
///   plane is defined over C, which is what makes cross-lingual transfer free: every
///   language's surfaces map into the same subspace because ILI is the address.</item>
///   <item><b>F</b> active frames + role bindings — which frames the prompt evoked and
///   which arguments fill which roles.</item>
///   <item><b>G</b> relation-gate signals — which relation families the context activated;
///   the highway-band indicator directions that feed §4 gating.</item>
/// </list>
///
/// <para>Heads become typed maps between strata — WSD reads W+context writes C, frame
/// reads C+W writes F, relation reads C gated by F/G writes C, realization reads C writes
/// W/S. Two heads writing different strata cannot collide, which kills the write-collision
/// problem structurally rather than statistically.</para>
///
/// <para><b>WIDTHS ARE COUNTED, NOT CHOSEN.</b> Same law as heads and layers: G is the band
/// count, F is the number of frames carrying witnessed lexical units, S is the PE budget
/// the positional encoding actually needs, and W/C are the spectral ranks of their own
/// graphs. Nothing here is a tuned hyperparameter, which is the whole point — a number
/// somebody picked is a number somebody has to defend.</para>
/// </summary>
internal static class ResidualStrata
{
    /// <summary>The five strata of doc 18 §2, in allocation order.</summary>
    internal enum Stratum { S = 0, W = 1, C = 2, F = 3, G = 4 }

    /// <summary>One stratum's half-open dim range <c>[Offset, Offset + Width)</c>.</summary>
    internal readonly record struct Block(Stratum Kind, int Offset, int Width)
    {
        public int End => Offset + Width;
        public bool IsEmpty => Width <= 0;
    }

    /// <summary>
    /// The counted inputs. Every field is a census of something the substrate holds, not a
    /// knob: pass what was measured and the layout follows.
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
    /// <para>STRUCTURAL STRATA ARE ALLOCATED FIRST AND ARE NEVER SQUEEZED. S, F and G are
    /// counts of things that exist — positional dims the encoder needs, frames that carry
    /// witnessed LUs, bands in the catalog. Truncating them does not lose resolution, it
    /// loses *entries*: a frame with no dim cannot be represented at all, and a missing band
    /// direction silently disables that band's gate. W and C are spectral, so truncating
    /// them is a rank cut with well-defined error, which is the only kind of squeeze that
    /// degrades gracefully.</para>
    ///
    /// <para>Throws when d_model cannot hold the structural strata plus a minimum of two
    /// spectral dims each. That is a real configuration error — a recipe asking for a width
    /// that cannot carry the ontology it was pointed at — and failing closed here is
    /// cheaper than discovering it as silently dead directions after synthesis.</para>
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
            // Counted rank fits. Hand the slack to C: the sense subspace is where every
            // semantic relation plane is defined and where cross-lingual transfer happens,
            // so an extra direction buys more there than in W, whose job is identity.
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

        // Order matters and is not arbitrary. S sits at the low dims because the positional
        // encoding is written by the embedding step before any head runs. W then C keeps the
        // two spectral blocks adjacent, so a head reading "identity plus concept" reads one
        // contiguous span. F and G are trailing indicator blocks, which keeps their
        // one-hot-ish structure out of the middle of the spectral geometry.
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
    /// Block Gram-Schmidt: orthonormalize each stratum's columns within itself, then against
    /// every earlier block. Disjoint dim ranges make the blocks non-overlapping in *storage*;
    /// this makes them orthogonal in *geometry*, which is what actually stops one head's
    /// write from projecting onto another's read.
    ///
    /// <para>Operates in place on a row-major <paramref name="basis"/> of
    /// <paramref name="rows"/> x <c>layout.DModel</c>. Returns the number of directions that
    /// collapsed (norm below tolerance after projection) — a nonzero count means the strata
    /// are not independent in the data, which is a finding about the ontology rather than an
    /// error, and the caller should report it rather than swallow it.</para>
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
