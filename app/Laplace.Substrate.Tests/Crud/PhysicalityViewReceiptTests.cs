using Laplace.Engine.Core;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public class PhysicalityViewReceiptTests
{
    private static byte[] Id(byte first, byte second = 0)
    {
        var id = new byte[16];
        id[0] = first;
        id[1] = second;
        return id;
    }

    [Fact]
    public void AvailableAndMissingViewsPreserveDescriptorsOrderAndSharedFrontier()
    {
        byte[][] descriptors = [Id(10), Id(20), Id(20)];
        byte[]?[] views = [Id(30), null, null];
        byte[][] missing = [Id(1, 255), Id(2)];
        long bytes = PhysicalityViewReceipts.RetainedPayloadBytes(3, 2);
        var result = PhysicalityViewReceipts.Decode(descriptors, views, [0, 1, 1], [0, 0, 0],
            [0, 2, 2], missing, bytes, 4);
        Assert.Equal(3, result.Forms.Length);
        Assert.Equal(Hash128.FromBytes(descriptors[0]), result.Forms[0].DescriptorId);
        Assert.Equal(Hash128.FromBytes(views[0]!), result.Forms[0].ViewId);
        Assert.Equal(PhysicalityViewState.Available, result.Forms[0].ViewState);
        Assert.Equal(result.Forms[1], result.Forms[2]);
        Assert.Null(result.Forms[1].ViewId);
        Assert.Equal(PhysicalityViewState.MissingReference, result.Forms[1].ViewState);
        Assert.Equal(2, result.Forms[1].MissingCount);
        Assert.Equal(missing.Select(id => Hash128.FromBytes(id)), result.Missing);

        descriptors[0][0] = 99;
        views[0]![0] = 99;
        missing[0][0] = 99;
        Assert.Equal(Hash128.FromBytes(Id(10)), result.Forms[0].DescriptorId);
        Assert.Equal(Hash128.FromBytes(Id(30)), result.Forms[0].ViewId);
        Assert.Equal(Hash128.FromBytes(Id(1, 255)), result.Missing[0]);
    }

    [Theory]
    [InlineData(0)] // Unknown state.
    [InlineData(1)] // Available without a view.
    [InlineData(2)] // Missing with a view.
    [InlineData(3)] // Missing with an empty frontier.
    [InlineData(4)] // Available with a frontier.
    [InlineData(5)] // Negative offset.
    [InlineData(6)] // Offset beyond the frontier.
    [InlineData(7)] // Negative count.
    [InlineData(8)] // Count overflows the slice.
    [InlineData(9)] // Descriptor width.
    [InlineData(10)] // View width.
    [InlineData(11)] // Missing ID width.
    [InlineData(12)] // Null missing ID.
    [InlineData(13)] // Duplicate missing IDs.
    [InlineData(14)] // Unsorted wire IDs.
    [InlineData(15)] // Parallel array mismatch.
    public void InvalidTransportNeverBecomesAnAvailableOrRetainedReceipt(int mutation)
    {
        byte[][] descriptors = [Id(10)];
        byte[]?[] views = [null];
        short[] states = [1];
        long[] first = [0];
        long[] count = [2];
        byte[][] missing = [Id(1, 255), Id(2)];
        switch (mutation)
        {
            case 0: states[0] = 2; break;
            case 1: states[0] = 0; count[0] = 0; break;
            case 2: views[0] = Id(30); break;
            case 3: count[0] = 0; break;
            case 4: states[0] = 0; views[0] = Id(30); break;
            case 5: first[0] = -1; break;
            case 6: first[0] = 3; break;
            case 7: count[0] = -1; break;
            case 8: count[0] = long.MaxValue; break;
            case 9: descriptors[0] = new byte[15]; break;
            case 10: states[0] = 0; count[0] = 0; views[0] = new byte[15]; break;
            case 11: missing[0] = new byte[15]; break;
            case 12: missing[0] = null!; break;
            case 13: missing[1] = missing[0]; break;
            case 14: Array.Reverse(missing); break;
            case 15: states = []; break;
        }
        Assert.Throws<InvalidOperationException>(() => PhysicalityViewReceipts.Decode(
            descriptors, views, states, first, count, missing, 4096, 1024));
    }

    [Fact]
    public void RetainedPayloadAndSharedRangeValidationHonorExactGrants()
    {
        byte[][] descriptors = [Id(10), Id(20)];
        byte[]?[] views = [null, null];
        byte[][] missing = [Id(1), Id(2)];
        long bytes = PhysicalityViewReceipts.RetainedPayloadBytes(2, 2);
        Assert.Throws<InvalidOperationException>(() => PhysicalityViewReceipts.Decode(
            descriptors, views, [1, 1], [0, 0], [2, 2], missing, bytes - 1, 4));
        Assert.Throws<InvalidOperationException>(() => PhysicalityViewReceipts.Decode(
            descriptors, views, [1, 1], [0, 0], [2, 2], missing, bytes, 3));
        Assert.Equal(2, PhysicalityViewReceipts.Decode(
            descriptors, views, [1, 1], [0, 0], [2, 2], missing, bytes, 4).Forms.Length);
    }

    [Fact]
    public void EmptyReceiptNeedsNoPayloadOrWork()
    {
        var receipt = PhysicalityViewReceipts.Decode([], [], [], [], [], [], 0, 0);
        Assert.Empty(receipt.Forms);
        Assert.Empty(receipt.Missing);
    }

    [Theory]
    [InlineData(0, 1)] // Unreferenced tail.
    [InlineData(1, 1)] // Gap before the first form's range.
    public void MissingReferenceArraysContainOnlyReferencesOwnedByForms(long first, long count)
    {
        Assert.Throws<InvalidOperationException>(() => PhysicalityViewReceipts.Decode(
            [Id(10)], [null], [1], [first], [count], [Id(1), Id(2)], 4096, 1024));
        Assert.Throws<InvalidOperationException>(() => PhysicalityViewReceipts.Decode(
            [], [], [], [], [], [Id(1)], 4096, 1024));
    }
}
