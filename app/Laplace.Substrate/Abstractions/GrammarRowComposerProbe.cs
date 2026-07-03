using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Abstractions;

internal static class GrammarRowComposerProbe
{
    public static async Task<byte[]?> ProbeDescentBitmapAsync(
        GrammarRowComposer composer, ISubstrateReader reader, CancellationToken ct)
    {
        composer.EnsureProbed();
        return await ProbeDescentBitmapAsync(composer.BorrowedTierTree(), reader, ct).ConfigureAwait(false);
    }

    public static async Task<byte[]?> ProbeDescentBitmapAsync(
        IntPtr treePtr, ISubstrateReader reader, CancellationToken ct)
    {
        if (treePtr == IntPtr.Zero) return null;
        using var tree = TierTree.FromBorrowedHandle(treePtr);
        return await ProbeDescentBitmapAsync(tree, reader, ct).ConfigureAwait(false);
    }

    public static Task<byte[]?> ProbeDescentBitmapAsync(
        TierTree tree, ISubstrateReader reader, CancellationToken ct)
        => TierTreeContainmentProbe.ProbeNodeEmitBitmapAsync(tree, reader, ct);
}
