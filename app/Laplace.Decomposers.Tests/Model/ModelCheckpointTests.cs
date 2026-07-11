using System.Text;
using System.Text.Json;
using Xunit;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model.Tests;

/// <summary>
/// Ledger §6 step 1 — the checkpoint as content. A tensor entity's id is Blake3
/// of its LITERAL byte range in the stored file; the checkpoint root is a Merkle
/// over the ordered tensor ids; CONTAINS/PRECEDES carry membership and order
/// scoped by the checkpoint. These are content-identity laws: a nondeterministic
/// tensor id would silently break the cross-model merge (byte-identical tensors
/// must collide), so determinism and structure shape are pinned here. No DB, no
/// perfcache — raw Blake3 + native Merkle only.
/// </summary>
public class ModelCheckpointTests
{
    // Emit a minimal valid safetensors blob: [8-byte LE header len][JSON][data].
    // n little-endian F32[2] tensors named t0..t(n-1), 8 data bytes each; the
    // i-th tensor's 8 bytes are all byte value (seed + i) so contents are known.
    private static string WriteSafetensors(string dir, int n, byte seed)
    {
        var header = new Dictionary<string, object>();
        for (int i = 0; i < n; i++)
            header[$"t{i}"] = new Dictionary<string, object>
            {
                ["dtype"] = "F32",
                ["shape"] = new[] { 2 },
                ["data_offsets"] = new[] { i * 8, i * 8 + 8 },
            };
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(header);
        byte[] lenLe = BitConverter.GetBytes((long)json.Length);
        if (!BitConverter.IsLittleEndian) Array.Reverse(lenLe);

        string path = Path.Combine(dir, "model.safetensors");
        using var fs = File.Create(path);
        fs.Write(lenLe);
        fs.Write(json);
        for (int i = 0; i < n; i++)
        {
            var block = new byte[8];
            Array.Fill(block, (byte)(seed + i));
            fs.Write(block);
        }
        return dir;
    }

    private static string NewDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "laplace_ckpt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void TensorIds_AreDeterministic_AndContentAddressed()
    {
        string a = WriteSafetensors(NewDir(), 3, seed: 10);
        string b = WriteSafetensors(NewDir(), 3, seed: 10);   // byte-identical tensors, different dir
        try
        {
            var ta = SafetensorsContainerParser.ParseModel(a);
            var tb = SafetensorsContainerParser.ParseModel(b);

            var ida1 = ModelCheckpoint.TensorIds(ta);
            var ida2 = ModelCheckpoint.TensorIds(ta);   // same file, twice
            var idb = ModelCheckpoint.TensorIds(tb);     // identical content, other file

            Assert.Equal(ida1, ida2);                    // deterministic
            Assert.Equal(ida1, idb);                     // content-addressed: same bytes → same id
            Assert.Equal(3, ida1.Length);
            Assert.Equal(ida1.Length, ida1.Distinct().Count()); // distinct contents → distinct ids
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
    }

    [Fact]
    public void DifferentBytes_ProduceDifferentTensorIds_AndDifferentRoot()
    {
        string a = WriteSafetensors(NewDir(), 2, seed: 1);
        string b = WriteSafetensors(NewDir(), 2, seed: 99);
        try
        {
            var ta = SafetensorsContainerParser.ParseModel(a);
            var tb = SafetensorsContainerParser.ParseModel(b);
            var ida = ModelCheckpoint.TensorIds(ta);
            var idb = ModelCheckpoint.TensorIds(tb);

            Assert.NotEqual(ida[0], idb[0]);
            Assert.NotEqual(
                Hash128.Merkle(EntityTier.Document, ida),
                Hash128.Merkle(EntityTier.Document, idb));
        }
        finally { Directory.Delete(a, true); Directory.Delete(b, true); }
    }

    [Fact]
    public void StageCheckpoint_EmitsRootPlusTensors_WithContainsAndPrecedesStructure()
    {
        const int n = 4;
        string dir = WriteSafetensors(NewDir(), n, seed: 5);
        try
        {
            var tensors = SafetensorsContainerParser.ParseModel(dir);
            var source = Hash128.OfCanonical("substrate/source/test-model/v1");
            var b = new SubstrateChangeBuilder(source, "checkpoint/byte-ranges", null,
                entityCapacity: n + 1, physicalityCapacity: 0, attestationCapacity: 2 * n);

            var root = ModelCheckpoint.StageCheckpoint(b, tensors, source);
            var change = b.Build();

            // n tensor entities + 1 checkpoint root
            Assert.Equal(n + 1, change.Entities.Length);
            Assert.Contains(change.Entities, e => e.Id == root);

            int contains = change.Attestations.Count(x => x.TypeId == ModelCoordinates.ContainsTypeId);
            int precedes = change.Attestations.Count(x => x.TypeId == ModelCoordinates.PrecedesTypeId);
            Assert.Equal(n, contains);       // root CONTAINS each tensor
            Assert.Equal(n - 1, precedes);   // tensors ordered by PRECEDES chain

            // Every CONTAINS is rooted at the checkpoint (context = checkpoint law).
            Assert.All(change.Attestations.Where(x => x.TypeId == ModelCoordinates.ContainsTypeId),
                x => Assert.Equal(root, x.SubjectId));

            // Root is a stable function of the ordered tensor ids.
            Assert.Equal(Hash128.Merkle(EntityTier.Document, ModelCheckpoint.TensorIds(tensors)), root);
        }
        finally { Directory.Delete(dir, true); }
    }
}
