using System.Text;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Core.Tests.Core;

public sealed class NativeCompositionParityTests
{
    [Theory]
    [InlineData("aa")]
    [InlineData("Laplace deployment validation.")]
    [InlineData("Ω𐀀")]
    [InlineData("A\u030a 👍🏽 mississippi\n\nSecond paragraph.")]
    public unsafe void EveryNativeCompositionRecomposesExactlyThroughThePublicCoreAbi(string text)
    {
        using var tree = TextDecomposer.Run(Encoding.UTF8.GetBytes(text));
        Span<double> coordinate = stackalloc double[4];
        int checkedNodes = 0;
        for (uint i = 0; i < tree.NodeCount; i++)
        {
            var node = tree.GetNode(i);
            if (node.ChildCount == 0) continue;
            var ids = new Hash128[node.ChildCount];
            var points = new double[node.ChildCount * 4];
            for (uint j = 0; j < node.ChildCount; j++)
            {
                var child = tree.GetNode(node.FirstChildIdx + j);
                ids[j] = child.Id;
                for (int axis = 0; axis < 4; axis++) points[j * 4 + axis] = child.Coord[axis];
            }
            var rebuilt = HashComposer.ComposeNode(node.Tier, ids, points, coordinate);
            Assert.Equal(node.Id, rebuilt.Id);
            for (int axis = 0; axis < 4; axis++)
                Assert.True(node.Coord[axis] == coordinate[axis],
                    $"node {i}, axis {axis}: Native {node.Coord[axis]:R}, recomposed {coordinate[axis]:R}");
            Assert.Equal(node.Hilbert.ToByteArray(), rebuilt.Hilbert.ToByteArray());
            checkedNodes++;
        }
        Assert.True(checkedNodes > 0);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(1.01, 0)]
    [InlineData(0.8, 0.8)]
    public void InvalidCoordinatesAreRejectedWithoutOverwritingTheOutput(double x, double y)
    {
        double[] output = [7, 8, 9, 10];
        Assert.Throws<ArgumentException>(() => HashComposer.ComposeNode(
            1, [Hash128.Zero], [x, y, 0, 0], output));
        Assert.Equal(new double[] { 7, 8, 9, 10 }, output);
    }
}
