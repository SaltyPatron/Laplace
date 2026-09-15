using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Xunit;

namespace Laplace.Cli.Tests;

public sealed class VerifiedGitCorpusRuntimeTests
{
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
        using var mapped = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0,
            MemoryMappedFileAccess.ReadExecute);
        using var view = mapped.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadExecute);
        using var process = Process.GetCurrentProcess();
        Assert.True(process.Modules.Cast<ProcessModule>().Count(module => module.FileName == path) > 1,
            "The actual OS mapping counterexample must contain multiple entries for one core artifact.");
        Assert.Equal(path, IngestCommands.ObserveLoadedCorePath());
        Assert.Equal(before, HashFile(path));
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
