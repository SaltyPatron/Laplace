using Laplace.Engine.Core;

namespace Laplace.Modality;

public readonly record struct RecordedEdge(
    string     SubjectKey,
    string     ObjectKey,
    string?    MoveKey,
    PlyOutcome MoverOutcome);

public interface IContentAddresser
{
    Hash128 Address(string canonicalSurface);
}

public static class ConsensusKeys
{
    private static readonly byte[] ZeroObject = new byte[16];

    public static Hash128 EdgeId(Hash128 subject, Hash128 type, Hash128 obj)
    {
        Span<byte> buf = stackalloc byte[48];
        subject.WriteBytes(buf[..16]);
        type.WriteBytes(buf.Slice(16, 16));
        obj.WriteBytes(buf.Slice(32, 16));
        return Hash128.Blake3(buf);
    }

    public static Hash128 EdgeId(Hash128 subject, Hash128 type, Hash128? obj)
    {
        if (obj is { } o) return EdgeId(subject, type, o);
        Span<byte> buf = stackalloc byte[48];
        subject.WriteBytes(buf[..16]);
        type.WriteBytes(buf.Slice(16, 16));
        ZeroObject.CopyTo(buf.Slice(32, 16));
        return Hash128.Blake3(buf);
    }
}

public interface IEdgeRatings
{
    Task<double[]> EffMuAsync(IReadOnlyList<Hash128> edgeIds, CancellationToken ct = default);
}

public interface IStateValuer
{
    Task<double[]> ValueStatesAsync(IReadOnlyList<string> stateSurfaces, CancellationToken ct = default);
}

public interface ITurnLearner
{
    Task LearnGameAsync(IReadOnlyList<RecordedEdge> edges, CancellationToken ct = default);
}

public static class GlickoPriors
{
    public const double NeutralMu = 1_500_000_000_000d;
    public const double InitialRd = 350_000_000_000d;
    public const double UnratedEffMu = NeutralMu - 2d * InitialRd;
}
