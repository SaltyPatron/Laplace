using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Xunit;

namespace Laplace.Cli.Tests;

public sealed class VerifiedGitCorpusRuntimeTests
{
    [DllImport("libc", SetLastError = true)]
    private static extern nint mmap(nint address, nuint length, int protection, int flags, int descriptor, nint offset);

    [DllImport("libc", SetLastError = true)]
    private static extern int munmap(nint address, nuint length);

    private static byte[] HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    [Fact]
    public void RepeatedExecutableMappingsRetainOneExactNativeArtifact()
    {
        if (!OperatingSystem.IsLinux()) return;
        string path = IngestCommands.ObserveLoadedCorePath();
        byte[] before = HashFile(path);
        nuint page = (nuint)Environment.SystemPageSize;
        // Process.Modules combines adjacent mappings of the same file. Reserve guards on
        // both sides so the added RX page cannot merge with the loader's original mapping.
        nint reservation = mmap(0, 3 * page, 0, 0x02 | 0x20, -1, 0); // PRIVATE | ANONYMOUS
        Assert.True(reservation != -1, $"mmap reservation failed: errno {Marshal.GetLastPInvokeError()}");
        try
        {
            using var file = File.OpenRead(path);
            nint middle = reservation + (nint)page;
            nint mapped = mmap(middle, page, 0x01 | 0x04, 0x02 | 0x10,
                file.SafeFileHandle.DangerousGetHandle().ToInt32(), 0); // READ | EXEC, PRIVATE | FIXED
            Assert.True(mapped == middle, $"mmap file page failed: errno {Marshal.GetLastPInvokeError()}");
            using var process = Process.GetCurrentProcess();
            var entries = process.Modules.Cast<ProcessModule>()
                .Where(module => Path.GetFullPath(module.FileName) == path).ToArray();
            Assert.True(entries.Length > 1,
                $"Expected separate actual core mappings, observed {entries.Length}: "
                + string.Join(", ", entries.Select(module => $"{module.BaseAddress:x}:{module.ModuleMemorySize}")));
            Assert.Equal(path, IngestCommands.ObserveLoadedCorePath());
            Assert.Equal(before, HashFile(path));
        }
        finally
        {
            Assert.Equal(0, munmap(reservation, 3 * page));
        }
    }

    [Fact]
    public void DifferentLoadedCoreArtifactsRemainAmbiguous()
    {
        if (!OperatingSystem.IsLinux()) return;
        string path = IngestCommands.ObserveLoadedCorePath();
        string directory = Path.Combine(Path.GetTempPath(), "laplace-corpus-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string other = Path.Combine(directory, Path.GetFileName(path));
        nint handle = 0;
        try
        {
            File.Copy(path, other);
            handle = NativeLibrary.Load(other);
            var failure = Assert.Throws<InvalidDataException>(() => IngestCommands.ObserveLoadedCorePath());
            Assert.Contains(path, failure.Message);
            Assert.Contains(other, failure.Message);
        }
        finally
        {
            if (handle != 0) NativeLibrary.Free(handle);
            Directory.Delete(directory, recursive: true);
        }
        Assert.Equal(path, IngestCommands.ObserveLoadedCorePath());
    }
}
