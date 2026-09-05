using System.Text.Json;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Model.Tests;

/// <summary>
/// A checkpoint header is source provenance, not model knowledge. Its tensor
/// names, dtypes, dimensions and order compose into native physical trajectories;
/// literal tensor values neither enter identities nor emit attestations.
/// </summary>
public sealed class ModelCheckpointTests
{
    private static string WriteSafetensors(string dir, int n, byte seed, int width = 2)
    {
        var header = new Dictionary<string, object>();
        for (int i = 0; i < n; i++)
            header[$"encoder.layer.{i}.attention.weight"] = new Dictionary<string, object>
            {
                ["dtype"] = "F32",
                ["shape"] = new[] { width },
                ["data_offsets"] = new[] { i * width * 4, (i + 1) * width * 4 },
            };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(header);
        byte[] lenLe = BitConverter.GetBytes((long)json.Length);
        if (!BitConverter.IsLittleEndian) Array.Reverse(lenLe);

        string path = Path.Combine(dir, "model.safetensors");
        using var fs = File.Create(path);
        fs.Write(lenLe);
        fs.Write(json);
        for (int i = 0; i < n * width * 4; i++) fs.WriteByte((byte)(seed + i));
        return dir;
    }

    private static string NewDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "laplace_ckpt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static (Hash128 Root, SubstrateChange Change) Stage(string dir)
    {
        CodepointPerfcache.LoadDefault();
        var tensors = SafetensorsContainerParser.ParseModel(dir);
        var source = SubstrateCanonicalIds.Source("test-model");
        var builder = new SubstrateChangeBuilder(source, "checkpoint/header", null);
        Hash128 root = ModelCheckpoint.StageCheckpoint(builder, tensors, source);
        return (root, builder.Build());
    }

    [Fact]
    public void StageCheckpoint_UsesHeaderStructure_NotOpaqueTensorBytes()
    {
        string a = WriteSafetensors(NewDir(), n: 3, seed: 1);
        string b = WriteSafetensors(NewDir(), n: 3, seed: 99);
        try
        {
            var left = Stage(a);
            var right = Stage(b);

            // Same ordered header, different literal weight bytes: one physical
            // source structure. Values can only become evidence after calibrated
            // token-to-token contraction, never checkpoint entities.
            Assert.Equal(left.Root, right.Root);
            Assert.Empty(left.Change.Attestations);
            Assert.Empty(right.Change.Attestations);

            var leftStage = Assert.Single(left.Change.IntentStages);
            Assert.True(leftStage.EntityCount >= 7, "tensor paths and checkpoint must be native-staged entities");
            Assert.True(leftStage.PhysicalityCount >= 7, "each multi-part header must carry an ordered trajectory");
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
    }

    [Fact]
    public void StageCheckpoint_HeaderShapeChangesMerkleStructure()
    {
        string a = WriteSafetensors(NewDir(), n: 3, seed: 1, width: 2);
        string b = WriteSafetensors(NewDir(), n: 3, seed: 1, width: 3);
        try
        {
            var left = Stage(a);
            var right = Stage(b);
            Assert.NotEqual(left.Root, right.Root);
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
    }

    [SkippableFact]
    public void MiniLmHeader_StagesOrderedProvenanceWithoutNumericPayload()
    {
        const string model = "/vault/models/models--sentence-transformers--all-MiniLM-L6-v2";
        string snapshots = Path.Combine(model, "snapshots");
        string? snapshot = Directory.Exists(snapshots)
            ? Directory.GetDirectories(snapshots).FirstOrDefault(
                path => File.Exists(Path.Combine(path, "model.safetensors")))
            : null;
        if (snapshot is null) throw new SkipException("MiniLM safetensors snapshot is not available");

        CodepointPerfcache.LoadDefault();
        var tensors = SafetensorsContainerParser.ParseModel(snapshot);
        Assert.True(tensors.Count > 0);
        Hash128 source = SubstrateCanonicalIds.Source("test-minilm-header");
        var builder = new SubstrateChangeBuilder(source, "checkpoint/header/minilm", null);
        Hash128 root = ModelCheckpoint.StageCheckpoint(builder, tensors, source);
        SubstrateChange change = builder.Build();

        Assert.NotEqual(default, root);
        Assert.Empty(change.Attestations);
        IntentStage stage = Assert.Single(change.IntentStages);
        Assert.True(stage.EntityCount >= tensors.Count + 1);
        Assert.True(stage.PhysicalityCount >= tensors.Count + 1);
    }

    [Fact]
    public void StageCheckpoint_RootTrajectoryCarriesOrderedTensorHeaders()
    {
        string dir = WriteSafetensors(NewDir(), n: 4, seed: 5);
        try
        {
            var (root, change) = Stage(dir);
            var stage = Assert.Single(change.IntentStages);

            // Header structure is carried by staged entity/physicality tuples, not
            // by the old structural-attestation scaffold. Four tensor headers plus
            // their checkpoint parent require at least five native compositions.
            Assert.NotEqual(default, root);
            Assert.Equal(0, stage.AttestationCount);
            Assert.True(stage.PhysicalityCount >= 5);
            Assert.True(stage.EntityCount >= 5);
        }
        finally { Directory.Delete(dir, true); }
    }
}
