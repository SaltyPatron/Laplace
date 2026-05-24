using Xunit;
using Laplace.Engine.Dynamics;

namespace Laplace.Engine.Dynamics.Tests;

public class SparsityStreamingTests
{
    [Fact]
    public void PerTensor_RejectsInvalidArgs()
    {
        var v = new double[] { 1, 2, 3 };
        var m = new byte[3];
        Assert.Throws<ArgumentException>(() => SparsityStreaming.PerTensor(Array.Empty<double>(), 0.5, m));
        Assert.Throws<ArgumentException>(() => SparsityStreaming.PerTensor(v, 0.5, new byte[2]));
        Assert.Throws<ArgumentOutOfRangeException>(() => SparsityStreaming.PerTensor(v, 0.0, m));
        Assert.Throws<ArgumentOutOfRangeException>(() => SparsityStreaming.PerTensor(v, 1.5, m));
    }

    [Fact]
    public void PerTensor_Top100PctRetainsAll()
    {
        var v = new double[] { -3, 1, -4, 1, 5 };
        var m = new byte[5];
        SparsityStreaming.PerTensor(v, 1.0, m);
        Assert.All(m, b => Assert.Equal(1, b));
    }

    [Fact]
    public void PerTensor_Top50PctRetainsTopHalfByAbs()
    {
        // |v| = {3, 1, 4, 1, 5}; k = ceil(5*0.5) = 3 → threshold = 3
        var v = new double[] { -3, 1, -4, 1, 5 };
        var m = new byte[5];
        SparsityStreaming.PerTensor(v, 0.5, m);
        Assert.Equal(new byte[] { 1, 0, 1, 0, 1 }, m);
    }

    [Fact]
    public void PerTensor_DeterministicAcrossRuns()
    {
        var rng = new Random(42);
        var v = new double[10_000];
        for (int i = 0; i < v.Length; i++) v[i] = rng.NextDouble() * 2.0 - 1.0;
        var a = new byte[v.Length];
        var b = new byte[v.Length];
        SparsityStreaming.PerTensor(v, 0.1, a);
        SparsityStreaming.PerTensor(v, 0.1, b);
        Assert.Equal(a, b);
    }

    [Fact]
    public void PerRow_RejectsInvalidArgs()
    {
        var r = new double[6];
        var m = new byte[6];
        Assert.Throws<ArgumentException>(() => SparsityStreaming.PerRow(r, 0, 3, 1, m));
        Assert.Throws<ArgumentException>(() => SparsityStreaming.PerRow(r, 2, 0, 1, m));
        Assert.Throws<ArgumentException>(() => SparsityStreaming.PerRow(new double[5], 2, 3, 1, m));
    }

    [Fact]
    public void PerRow_ZeroKPrunesAll()
    {
        var r = new double[] { 1, 2, 3, 4, 5, 6 };
        var m = new byte[6];
        for (int i = 0; i < 6; i++) m[i] = 1;
        SparsityStreaming.PerRow(r, 2, 3, 0, m);
        Assert.All(m, b => Assert.Equal(0, b));
    }

    [Fact]
    public void PerRow_KGEQRowSizeRetainsAll()
    {
        var r = new double[] { 1, 2, 3, 4, 5, 6 };
        var m = new byte[6];
        SparsityStreaming.PerRow(r, 2, 3, 5, m);
        Assert.All(m, b => Assert.Equal(1, b));
    }

    [Fact]
    public void PerRow_Top2Of4()
    {
        // row 0: |{-1,4,-2,3}| → top-2 = {4,3} → mask {0,1,0,1}
        // row 1: |{ 5,-1,0,2}|  → top-2 = {5,2} → mask {1,0,0,1}
        var r = new double[]
        {
            -1, 4, -2, 3,
             5,-1,  0, 2,
        };
        var m = new byte[8];
        SparsityStreaming.PerRow(r, 2, 4, 2, m);
        Assert.Equal(new byte[] { 0, 1, 0, 1, 1, 0, 0, 1 }, m);
    }

    [Fact]
    public void PerRow_DeterministicAcrossRuns()
    {
        var rng = new Random(99);
        const int R = 200, C = 64;
        var r = new double[R * C];
        for (int i = 0; i < r.Length; i++) r[i] = rng.NextDouble() * 2.0 - 1.0;
        var a = new byte[R * C];
        var b = new byte[R * C];
        SparsityStreaming.PerRow(r, R, C, 8, a);
        SparsityStreaming.PerRow(r, R, C, 8, b);
        Assert.Equal(a, b);
    }
}
