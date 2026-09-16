using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Npgsql;
using Xunit;

namespace Laplace.Substrate.Tests.Crud;

// Exercise the real reader and its shared batcher with controlled transport.
// These controls do not claim PostgreSQL execution; the chess migration's
// context-substitution case covers the real backend eviction/rebuild path.
public sealed class ReaderPresenceLifetimeTests
{
    private static readonly Hash128 Marker = new(0x93d7aa3167c901e2UL, 0x5428f318e84aa629UL);
    private static readonly Hash128 Source = new(0x491ce885ca31274dUL, 0xaa8831793c604021UL);
    private static readonly Hash128 MarkerType = new(0x172e3b8659aa340cUL, 0xdad248131c5ba27aUL);
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task CompletedEvictionReopensPreviouslyProvenMarkers()
    {
        await using var dataSource = UnusedDataSource();
        int stored = 1, probes = 0;
        var reader = new NpgsqlSubstrateReader(dataSource,
            (ids, _) =>
            {
                Interlocked.Increment(ref probes);
                return Task.FromResult(Bitmap(ids, Volatile.Read(ref stored) != 0));
            },
            (source, relations, markers, _) =>
            {
                Assert.Equal(Source, source);
                Assert.Null(relations);
                Assert.Equal(new[] { MarkerType }, markers);
                Volatile.Write(ref stored, 0);
                return Task.CompletedTask;
            });

        Assert.True(BitmapBits.IsSet(await reader.EntitiesExistBitmapAsync([Marker, Marker]), 0));
        Assert.True(reader.IsProvenPresent(Marker));
        Assert.True(BitmapBits.IsSet(await reader.EntitiesExistBitmapAsync([Marker]), 0));
        Assert.Equal(1, probes);

        await reader.EvictSourceAsync(Source, null, [MarkerType]);

        Assert.False(reader.IsProvenPresent(Marker));
        Assert.Equal(new byte[] { 0 }, await reader.EntitiesExistBitmapAsync([Marker, Marker]));
        Assert.Equal(2, probes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialEvictionFailureOrCancellationInvalidatesPresence(bool cancel)
    {
        await using var dataSource = UnusedDataSource();
        int stored = 1, probes = 0;
        var reader = new NpgsqlSubstrateReader(dataSource,
            (ids, _) =>
            {
                Interlocked.Increment(ref probes);
                return Task.FromResult(Bitmap(ids, Volatile.Read(ref stored) != 0));
            },
            (_, _, _, _) =>
            {
                // Model the procedure committing a deletion before its later
                // batch fails. The cache must not depend on successful return.
                Volatile.Write(ref stored, 0);
                return Task.FromException(cancel
                    ? new OperationCanceledException("after committed deletion")
                    : new IOException("after committed deletion"));
            });
        Assert.True(BitmapBits.IsSet(await reader.EntitiesExistBitmapAsync([Marker]), 0));

        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => reader.EvictSourceAsync(Source, null, [MarkerType]));
        else
            await Assert.ThrowsAsync<IOException>(
                () => reader.EvictSourceAsync(Source, null, [MarkerType]));

        Assert.False(reader.IsProvenPresent(Marker));
        Assert.False(BitmapBits.IsSet(await reader.EntitiesExistBitmapAsync([Marker]), 0));
        Assert.Equal(2, probes);
    }

    [Fact]
    public async Task DelayedProbeCannotRepopulateThePostEvictionCache()
    {
        await using var dataSource = UnusedDataSource();
        var entered = Signal();
        var release = Signal();
        int stored = 1, probes = 0;
        var reader = new NpgsqlSubstrateReader(dataSource,
            async (ids, _) =>
            {
                bool snapshot = Volatile.Read(ref stored) != 0;
                if (Interlocked.Increment(ref probes) == 1)
                {
                    entered.SetResult();
                    await release.Task;
                }
                return Bitmap(ids, snapshot);
            },
            (_, _, _, _) =>
            {
                Volatile.Write(ref stored, 0);
                return Task.CompletedTask;
            });

        var presenceScope = reader.CapturePresenceScope();
        Task<byte[]> pending = reader.EntitiesExistBitmapAsync([Marker]);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            await reader.EvictSourceAsync(Source, null, [MarkerType]);
            release.SetResult();
            // The in-flight query returns its old snapshot, but that snapshot
            // must not become a proof of presence for any subsequent request.
            Assert.True(BitmapBits.IsSet(await pending.WaitAsync(Deadline), 0));
            reader.MarkProven([Marker], presenceScope);
            ((ISubstrateReader)reader).MarkProven([Marker]);
            Assert.False(reader.IsProvenPresent(Marker));
            Assert.False(BitmapBits.IsSet(await reader.EntitiesExistBitmapAsync([Marker]), 0));
            Assert.Equal(2, probes);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task OverlappingEvictionsDetachAnIntermediateProbeGeneration()
    {
        await using var dataSource = UnusedDataSource();
        var firstEntered = Signal();
        var secondEntered = Signal();
        var firstRelease = Signal();
        var secondRelease = Signal();
        var probeEntered = Signal();
        var probeRelease = Signal();
        int stored = 1, probes = 0, evictions = 0;
        var reader = new NpgsqlSubstrateReader(dataSource,
            async (ids, _) =>
            {
                bool snapshot = Volatile.Read(ref stored) != 0;
                if (Interlocked.Increment(ref probes) == 1)
                {
                    probeEntered.SetResult();
                    await probeRelease.Task;
                }
                return Bitmap(ids, snapshot);
            },
            async (_, _, _, _) =>
            {
                if (Interlocked.Increment(ref evictions) == 1)
                {
                    firstEntered.SetResult();
                    await firstRelease.Task;
                }
                else
                {
                    secondEntered.SetResult();
                    await secondRelease.Task;
                    Volatile.Write(ref stored, 0);
                }
            });
        reader.MarkProven([Marker], reader.CapturePresenceScope());
        Assert.True(reader.IsProvenPresent(Marker));
        Task first = reader.EvictSourceAsync(Source, null, [MarkerType]);
        Task second = reader.EvictSourceAsync(Source, null, [MarkerType]);
        try
        {
            await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(Deadline);
            firstRelease.SetResult();
            await first.WaitAsync(Deadline);
            Assert.False(reader.IsProvenPresent(Marker));
            Task<byte[]> intermediate = reader.EntitiesExistBitmapAsync([Marker]);
            await probeEntered.Task.WaitAsync(Deadline);
            secondRelease.SetResult();
            await second.WaitAsync(Deadline);
            probeRelease.SetResult();
            Assert.True(BitmapBits.IsSet(await intermediate.WaitAsync(Deadline), 0));
            Assert.False(reader.IsProvenPresent(Marker));
            Assert.False(BitmapBits.IsSet(await reader.EntitiesExistBitmapAsync([Marker]), 0));
            Assert.Equal(2, probes);
            Assert.Equal(2, evictions);
        }
        finally
        {
            firstRelease.TrySetResult();
            secondRelease.TrySetResult();
            probeRelease.TrySetResult();
        }
    }

    [Fact]
    public async Task UnscopedHintsCannotClaimStoredPresence()
    {
        await using var dataSource = UnusedDataSource();
        int probes = 0;
        var reader = new NpgsqlSubstrateReader(dataSource,
            (ids, _) =>
            {
                Interlocked.Increment(ref probes);
                return Task.FromResult(Bitmap(ids, false));
            }, (_, _, _, _) => Task.CompletedTask);
        ISubstrateReader caller = reader;

        caller.MarkProven([Marker]);

        Assert.False(reader.IsProvenPresent(Marker));
        Assert.False(BitmapBits.IsSet(await reader.EntitiesExistBitmapAsync([Marker]), 0));
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task ForeignReaderScopeCannotPromotePresence()
    {
        await using var dataSource = UnusedDataSource();
        var first = new NpgsqlSubstrateReader(dataSource);
        var second = new NpgsqlSubstrateReader(dataSource,
            (ids, _) => Task.FromResult(Bitmap(ids, false)),
            (_, _, _, _) => Task.CompletedTask);
        var foreign = first.CapturePresenceScope();

        second.MarkProven([Marker], foreign);
        second.MarkProven([Marker], default);

        Assert.False(second.IsProvenPresent(Marker));
        Assert.False(BitmapBits.IsSet(await second.EntitiesExistBitmapAsync([Marker]), 0));
    }

    [Fact]
    public async Task AcknowledgedWriteCanPromoteItsStillCurrentScope()
    {
        await using var dataSource = UnusedDataSource();
        int stored = 0, probes = 0;
        var acknowledgement = Signal();
        var reader = new NpgsqlSubstrateReader(dataSource,
            (ids, _) =>
            {
                Interlocked.Increment(ref probes);
                return Task.FromResult(Bitmap(ids, Volatile.Read(ref stored) != 0));
            }, (_, _, _, _) => Task.CompletedTask);
        var presenceScope = reader.CapturePresenceScope();
        async Task AcknowledgeWrite()
        {
            await acknowledgement.Task;
            Volatile.Write(ref stored, 1);
        }
        Task write = AcknowledgeWrite();
        Assert.False(reader.IsProvenPresent(Marker));
        acknowledgement.SetResult();
        await write.WaitAsync(Deadline);

        reader.MarkProven([Marker], presenceScope);

        Assert.True(reader.IsProvenPresent(Marker));
        Assert.True(BitmapBits.IsSet(await reader.EntitiesExistBitmapAsync([Marker]), 0));
        Assert.Equal(0, probes);
    }

    private static NpgsqlDataSource UnusedDataSource() =>
        NpgsqlDataSource.Create("Host=127.0.0.1;Database=unused;Username=unused");

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static byte[] Bitmap(IReadOnlyList<Hash128> ids, bool present)
    {
        var result = new byte[BitmapBits.ByteLength(ids.Count)];
        for (int i = 0; i < ids.Count; i++)
        {
            Assert.Equal(Marker, ids[i]);
            if (present) BitmapBits.Set(result, i);
        }
        return result;
    }
}
