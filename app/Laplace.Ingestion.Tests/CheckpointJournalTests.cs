using Xunit;
using Laplace.Engine.Core;
using Laplace.Ingestion;

namespace Laplace.Ingestion.Tests;

public class CheckpointJournalTests
{
    [Fact]
    public async Task OpenOrCreate_FreshFileWritesMagic()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laplace-ckpt-{Guid.NewGuid():N}.bin");
        try
        {
            await using (var j = await CheckpointJournal.OpenOrCreateAsync(path))
            {
                Assert.Equal(0, j.AppliedCount);
                Assert.Equal(path, j.Path);
            }
            // Magic should be present at byte 0
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.True(bytes.Length >= 8);
            Assert.Equal((byte)'L', bytes[0]);
            Assert.Equal((byte)'P', bytes[1]);
        }
        finally { try { File.Delete(path); } catch {} }
    }

    [Fact]
    public async Task AppendAndReopenPreservesAppliedSet()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laplace-ckpt-{Guid.NewGuid():N}.bin");
        try
        {
            var idA = new Hash128(1, 2);
            var idB = new Hash128(3, 4);
            await using (var j = await CheckpointJournal.OpenOrCreateAsync(path))
            {
                await j.AppendAsync(idA, 1000);
                await j.AppendAsync(idB, 2000);
                Assert.Equal(2, j.AppliedCount);
                Assert.True(j.WasApplied(idA));
                Assert.True(j.WasApplied(idB));
                await j.FlushAsync();
            }
            await using (var j2 = await CheckpointJournal.OpenOrCreateAsync(path))
            {
                Assert.Equal(2, j2.AppliedCount);
                Assert.True(j2.WasApplied(idA));
                Assert.True(j2.WasApplied(idB));
                Assert.False(j2.WasApplied(new Hash128(99, 99)));
            }
        }
        finally { try { File.Delete(path); } catch {} }
    }

    [Fact]
    public async Task AppendIsIdempotentOnDuplicateIntentId()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laplace-ckpt-{Guid.NewGuid():N}.bin");
        try
        {
            var id = new Hash128(7, 8);
            await using var j = await CheckpointJournal.OpenOrCreateAsync(path);
            await j.AppendAsync(id, 1000);
            await j.AppendAsync(id, 2000); // duplicate — no-op
            Assert.Equal(1, j.AppliedCount);
            await j.FlushAsync();
            var len = new FileInfo(path).Length;
            // Magic(8) + 1 record(24) = 32 bytes total
            Assert.Equal(32, len);
        }
        finally { try { File.Delete(path); } catch {} }
    }

    [Fact]
    public async Task OpenWithInvalidMagicThrows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"laplace-ckpt-{Guid.NewGuid():N}.bin");
        try
        {
            await File.WriteAllBytesAsync(path, new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 });
            await Assert.ThrowsAsync<InvalidDataException>(
                () => CheckpointJournal.OpenOrCreateAsync(path));
        }
        finally { try { File.Delete(path); } catch {} }
    }
}
