using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Laplace.Ingestion;
using Xunit;

namespace Laplace.SubstrateCRUD.Tests;

public sealed class CanonicalWindowTests
{
    [Fact]
    public void NativeReferencesAndActualArityCrossTheManagedAbiUnchanged()
    {
        Assert.Equal(64, Marshal.SizeOf<StagedReference>());
        Assert.Equal(52, Marshal.OffsetOf<StagedReference>(nameof(StagedReference.Position)).ToInt32());
        using var window = new StagedSourceWriter(1000, 10000, 1 << 20);
        StagedReference[] values = [window.Text("alpha"u8), window.Text("beta"u8), window.Text("gamma"u8)];
        Assert.Equal(Hash128.Text("alpha"), values[0].Id);
        var binary = window.Compose(values.AsSpan(0, 2), 5);
        var ternary = window.Compose(values, 5);
        Assert.Equal(Hash128.Merkle(5, values.AsSpan(0, 2).ToArray().Select(v => v.Id).ToArray()), binary.Id);
        Assert.Equal(Hash128.Merkle(5, values.Select(v => v.Id).ToArray()), ternary.Id);
        Assert.NotEqual(binary.Id, ternary.Id);
    }

    [Fact]
    public void NativeBudgetFailureRequiresResetBeforeReuse()
    {
        using var window = new StagedSourceWriter(1, 100, 4096);
        Assert.Throws<InvalidDataException>(() => window.Text("several words exceed one node"u8));
        window.Reset();
        Assert.Equal(Hash128.Text("A"), window.Text("A"u8).Id);
    }

    [Fact]
    public void DisposedWindowRefusesBeforeNativeAccess()
    {
        var window = new StagedSourceWriter(100, 1000, 4096);
        window.Dispose();
        Assert.Throws<InvalidOperationException>(() => window.Text("A"u8));
        window.Dispose();
    }
}
