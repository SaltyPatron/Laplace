using DynInterop = Laplace.Engine.Dynamics.NativeInterop;

namespace Laplace.Decomposers.Model;

/// <summary>
/// Owns one transient native circuit arena while its complete candidate pages
/// are consumed. The opaque context contains only canonical-entity factors;
/// tokenizer aliases have already been aggregated as sets by native code.
/// </summary>
internal sealed class NativeBilinearContraction : IDisposable
{
    private IntPtr _handle;

    private NativeBilinearContraction(IntPtr handle, double arenaRms, nuint residentBytes)
    {
        _handle = handle;
        ArenaRms = arenaRms;
        ResidentBytes = checked((long)residentBytes);
    }

    public double ArenaRms { get; }
    public long ResidentBytes { get; }

    public static unsafe NativeBilinearContraction Direct(
        float[] leftRows, float[] rightRows, int vocabularyRows, int dimension,
        int[] tokenRows, int[] entityIndexes, int entityCount)
    {
        ValidateRows(leftRows, vocabularyRows, dimension, nameof(leftRows));
        ValidateRows(rightRows, vocabularyRows, dimension, nameof(rightRows));
        ValidateMapping(tokenRows, entityIndexes, entityCount);
        IntPtr handle = IntPtr.Zero;
        double arena = 0;
        nuint resident = 0;
        int rc;
        fixed (float* left = leftRows)
        fixed (float* right = rightRows)
        fixed (int* tokens = tokenRows)
        fixed (int* entities = entityIndexes)
            rc = DynInterop.BilinearDirectContractionCreate(
                left, right, (nuint)vocabularyRows, (nuint)dimension,
                tokens, entities, (nuint)tokenRows.Length, (nuint)entityCount,
                &handle, &arena, &resident);
        if (rc != 0 || handle == IntPtr.Zero)
            throw new InvalidOperationException($"native direct contraction creation failed: {rc}");
        return new(handle, arena, resident);
    }

    public static unsafe NativeBilinearContraction Projected(
        float[] embeddingRows, int vocabularyRows, int dimension,
        int[] tokenRows, int[] entityIndexes, int entityCount,
        float[] leftWeight, float[]? leftBias,
        float[] rightWeight, float[]? rightBias, int rank)
    {
        ValidateRows(embeddingRows, vocabularyRows, dimension, nameof(embeddingRows));
        ValidateMapping(tokenRows, entityIndexes, entityCount);
        if (rank <= 0) throw new ArgumentOutOfRangeException(nameof(rank));
        if (leftWeight.LongLength != (long)rank * dimension)
            throw new ArgumentException("left projection shape disagrees with rank and hidden size", nameof(leftWeight));
        if (rightWeight.LongLength != (long)rank * dimension)
            throw new ArgumentException("right projection shape disagrees with rank and hidden size", nameof(rightWeight));
        if (leftBias is not null && leftBias.Length != rank)
            throw new ArgumentException("left bias shape disagrees with rank", nameof(leftBias));
        if (rightBias is not null && rightBias.Length != rank)
            throw new ArgumentException("right bias shape disagrees with rank", nameof(rightBias));

        IntPtr handle = IntPtr.Zero;
        double arena = 0;
        nuint resident = 0;
        int rc;
        fixed (float* embedding = embeddingRows)
        fixed (int* tokens = tokenRows)
        fixed (int* entities = entityIndexes)
        fixed (float* left = leftWeight)
        fixed (float* leftB = leftBias)
        fixed (float* right = rightWeight)
        fixed (float* rightB = rightBias)
            rc = DynInterop.BilinearProjectedContractionCreate(
                embedding, (nuint)vocabularyRows, (nuint)dimension,
                tokens, entities, (nuint)tokenRows.Length, (nuint)entityCount,
                left, leftB, right, rightB, (nuint)rank,
                &handle, &arena, &resident);
        if (rc != 0 || handle == IntPtr.Zero)
            throw new InvalidOperationException($"native projected contraction creation failed: {rc}");
        return new(handle, arena, resident);
    }

    public unsafe (long[] Scores, short[] Outcomes) Score(int[] rows, int[] cols)
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        if (rows.Length != cols.Length)
            throw new ArgumentException("candidate row and column arrays must have equal length");
        var scores = new long[rows.Length];
        var outcomes = new short[rows.Length];
        int rc;
        fixed (int* rowPtr = rows)
        fixed (int* colPtr = cols)
        fixed (long* scorePtr = scores)
        fixed (short* outcomePtr = outcomes)
            rc = DynInterop.BilinearContractionCandidatesCalibrate(
                _handle, rowPtr, colPtr, (nuint)rows.Length, scorePtr, outcomePtr);
        if (rc != 0) throw new InvalidOperationException($"native candidate calibration failed: {rc}");
        return (scores, outcomes);
    }

    public static unsafe (long[] Scores, short[] Outcomes) AggregateCircuitScores(
        IReadOnlyList<long[]> circuitScores,
        IReadOnlyList<long> opponentRatingsFp1e9,
        IReadOnlyList<long> opponentRdsFp1e9)
    {
        ArgumentNullException.ThrowIfNull(circuitScores);
        ArgumentNullException.ThrowIfNull(opponentRatingsFp1e9);
        ArgumentNullException.ThrowIfNull(opponentRdsFp1e9);
        if (circuitScores.Count == 0
            || circuitScores.Count != opponentRatingsFp1e9.Count
            || circuitScores.Count != opponentRdsFp1e9.Count)
            throw new ArgumentException("circuit scores and opponent states must have equal non-zero counts");
        int candidates = circuitScores[0].Length;
        if (candidates == 0 || circuitScores.Any(scores => scores.Length != candidates))
            throw new ArgumentException("every circuit must score the same non-empty candidate set");

        var flattened = new long[checked(circuitScores.Count * candidates)];
        for (int circuit = 0; circuit < circuitScores.Count; circuit++)
            circuitScores[circuit].CopyTo(flattened, circuit * candidates);
        long[] ratings = opponentRatingsFp1e9.ToArray();
        long[] rds = opponentRdsFp1e9.ToArray();
        var scoresOut = new long[candidates];
        var outcomesOut = new short[candidates];
        int rc;
        fixed (long* scorePtr = flattened)
        fixed (long* ratingPtr = ratings)
        fixed (long* rdPtr = rds)
        fixed (long* scoreOutPtr = scoresOut)
        fixed (short* outcomeOutPtr = outcomesOut)
            rc = DynInterop.ModelCircuitCalibrateGlicko(
                scorePtr, ratingPtr, rdPtr,
                (nuint)circuitScores.Count, (nuint)candidates,
                scoreOutPtr, outcomeOutPtr);
        if (rc != 0) throw new InvalidOperationException($"native circuit aggregation failed: {rc}");
        return (scoresOut, outcomesOut);
    }

    public void Dispose()
    {
        IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero) DynInterop.BilinearContractionFree(handle);
        GC.SuppressFinalize(this);
    }

    ~NativeBilinearContraction() => Dispose();

    private static void ValidateRows(float[] rows, int count, int dimension, string parameter)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (dimension <= 0) throw new ArgumentOutOfRangeException(nameof(dimension));
        if (rows.LongLength != (long)count * dimension)
            throw new ArgumentException("tensor shape disagrees with vocabulary and hidden size", parameter);
    }

    private static void ValidateMapping(int[] tokenRows, int[] entityIndexes, int entityCount)
    {
        if (tokenRows.Length == 0 || tokenRows.Length != entityIndexes.Length)
            throw new ArgumentException("token rows and canonical entity indexes must be a non-empty parallel mapping");
        if (entityCount <= 0) throw new ArgumentOutOfRangeException(nameof(entityCount));
    }
}
