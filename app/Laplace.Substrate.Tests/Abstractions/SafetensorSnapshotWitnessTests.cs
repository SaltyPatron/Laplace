using Laplace.Decomposers.Abstractions;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class SafetensorSnapshotWitnessTests
{
    [Fact]
    public void Validate_rejects_missing_config()
    {
        var dir = Path.Combine(Path.GetTempPath(), "laplace-st-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "tokenizer.json"), "{}");
            var r = SafetensorSnapshotWitness.Validate(dir);
            Assert.False(r.Ok);
            Assert.Contains("config.json", r.Error, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Validate_accepts_minimal_bundle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "laplace-st-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "tokenizer.json"), "{}");
            File.WriteAllBytes(Path.Combine(dir, "weights.safetensors"), [0]);
            Assert.True(SafetensorSnapshotWitness.IsComplete(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ResolveCompleteDir_walks_hf_snapshots_layout()
    {
        var root = Path.Combine(Path.GetTempPath(), "laplace-hf-" + Guid.NewGuid().ToString("N"));
        var fam = Path.Combine(root, "models--org--toy");
        var snap = Path.Combine(fam, "snapshots", "revdeadbeef");
        Directory.CreateDirectory(snap);
        try
        {
            File.WriteAllText(Path.Combine(snap, "config.json"), "{}");
            File.WriteAllText(Path.Combine(snap, "tokenizer.json"), "{}");
            File.WriteAllBytes(Path.Combine(snap, "weights.safetensors"), [0]);

            Assert.Equal(snap, SafetensorSnapshotWitness.ResolveCompleteDir(root));
            Assert.Equal(snap, SafetensorSnapshotWitness.ResolveCompleteDir(fam));
            Assert.Equal(snap, SafetensorSnapshotWitness.ResolveCompleteDir(snap));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
