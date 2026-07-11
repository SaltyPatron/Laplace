using System.IO.MemoryMappedFiles;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;

// Ledger §6 step 1 — the checkpoint as CONTENT. A recorder deposit like every
// other: WordNet reads XML rows and emits attestations; here we read the stored
// weight file's LITERAL BYTE RANGES and emit the checkpoint's identity + structure.
//
// A tensor entity's id is Blake3 of its exact byte range in the safetensors blob —
// the SAME law the tokenizer.json entity already uses (file content, never the
// generated floats interpreted as numbers). The checkpoint root is a Merkle over
// the ordered tensor ids; membership and order ride CONTAINS/PRECEDES scoped by
// context = the checkpoint, verbatim text-lane law (mirrors ModelCoordinates).
//
// The model NEVER enters an id — it is the attestation source. Two checkpoints
// that share a byte-identical tensor collide on that tensor's content id (a merge,
// never an entity-resolution pass); their provenance stays per-model via source.
public static class ModelCheckpoint
{
    public static readonly Hash128 TensorTypeId = EntityTypeRegistry.Id("Model_Tensor");
    public static readonly Hash128 CheckpointTypeId = EntityTypeRegistry.Id("Model_Checkpoint");

    // ReadOnlySpan addresses at most int.MaxValue bytes; no single tensor in the
    // supported models approaches 2 GiB, but guard rather than silently truncate.
    private const long MaxTensorBytes = int.MaxValue;

    // Content id of one tensor = Blake3 over [AbsoluteDataStart, AbsoluteDataEnd)
    // of its file, read through a caller-owned memory map (one map per file, all
    // that file's tensors hashed against it — never one map per tensor).
    private static unsafe Hash128 HashRange(MemoryMappedViewAccessor view, long start, long length)
    {
        byte* basePtr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
        try
        {
            var span = new ReadOnlySpan<byte>(basePtr + start, checked((int)length));
            return Hash128.Blake3(span);
        }
        finally
        {
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
    }

    // Compute every tensor's content id in the parser's canonical order (sorted by
    // FilePath then DataStart in SafetensorsContainerParser.ParseModel), grouping
    // by file so each weight blob is mapped exactly once.
    public static Hash128[] TensorIds(IReadOnlyList<SafetensorsContainerParser.TensorReference> tensors)
    {
        var ids = new Hash128[tensors.Count];
        int i = 0;
        while (i < tensors.Count)
        {
            string file = tensors[i].FilePath;
            using var mmf = MemoryMappedFile.CreateFromFile(
                file, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
            using var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            for (; i < tensors.Count && tensors[i].FilePath == file; i++)
            {
                var t = tensors[i];
                long len = t.AbsoluteDataEnd - t.AbsoluteDataStart;
                if (len < 0 || len > MaxTensorBytes)
                    throw new InvalidDataException(
                        $"tensor '{t.Name}' byte range {len} out of [0,{MaxTensorBytes}] — cannot content-address");
                ids[i] = HashRange(view, t.AbsoluteDataStart, len);
            }
        }
        return ids;
    }

    // Deposit the checkpoint's identity and structure. Insert-if-absent everywhere,
    // so re-ingesting the same checkpoint re-witnesses identical rows (0 novel).
    // Returns the checkpoint root id for the caller to hang the model root off.
    public static Hash128 StageCheckpoint(
        SubstrateChangeBuilder b,
        IReadOnlyList<SafetensorsContainerParser.TensorReference> tensors,
        Hash128 sourceId)
    {
        var ids = TensorIds(tensors);
        var checkpoint = Hash128.Merkle(EntityTier.Document, ids);

        b.AddEntity(checkpoint, EntityTier.Document, CheckpointTypeId, firstObservedBy: sourceId);
        for (int i = 0; i < ids.Length; i++)
            b.AddEntity(ids[i], EntityTier.Word, TensorTypeId, firstObservedBy: sourceId);

        foreach (var id in ids)
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                checkpoint, ModelCoordinates.ContainsTypeId, id, sourceId, checkpoint, 1.0));
        for (int i = 1; i < ids.Length; i++)
            b.AddAttestation(NativeAttestation.CategoricalResolved(
                ids[i - 1], ModelCoordinates.PrecedesTypeId, ids[i], sourceId, checkpoint, 1.0));

        return checkpoint;
    }
}
