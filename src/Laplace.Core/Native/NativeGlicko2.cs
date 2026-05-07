namespace Laplace.Core.Native;

using System.Runtime.InteropServices;

internal static partial class NativeGlicko2
{
    /// <summary>Native layout matching <c>laplace_glicko2_state_t</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct State
    {
        public double Mu;
        public double Phi;
        public double Sigma;
        public int    Games;
    }

    /// <summary>Native layout matching <c>laplace_glicko2_observation_t</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Observation
    {
        public double OpponentMu;
        public double OpponentPhi;
        public double Score;
        public double Weight;
    }

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_glicko2_apply")]
    internal static unsafe partial void Apply(
        in State input,
        Observation* observations,
        nuint nObservations,
        double tau,
        out State output);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_glicko2_period_decay")]
    internal static partial void PeriodDecay(in State input, out State output);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_glicko2_to_rating")]
    internal static partial double ToRating(double mu);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_glicko2_to_rating_dev")]
    internal static partial double ToRatingDev(double phi);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_glicko2_from_rating")]
    internal static partial double FromRating(double rating);

    [LibraryImport(NativeLibrary.Name, EntryPoint = "laplace_glicko2_from_rating_dev")]
    internal static partial double FromRatingDev(double ratingDev);
}
