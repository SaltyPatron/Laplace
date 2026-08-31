using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.OpenSubtitles;

public sealed record AlignedSubtitleBlock(
    string PairLabel,
    long StartOrdinal,
    IReadOnlyList<byte[]> Left,
    IReadOnlyList<byte[]> Right,
    Hash128 LeftLanguage,
    Hash128 RightLanguage)
{
    public int Count => Left.Count;
}

internal sealed class OpenSubtitlesAlignedHandler
    : IIngestRecordHandler<AlignedSubtitleBlock>
{
    private static readonly Hash128 HasLanguageTypeId =
        RelationTypeRegistry.Resolve(EtlSource.LanguageScopeRelation).Id;

    private readonly Hash128 _source;
    private readonly double _trust;

    public OpenSubtitlesAlignedHandler(Hash128 source, double trust)
    {
        _source = source;
        _trust = trust;
    }

    public long UnitsPerRecord(AlignedSubtitleBlock record) => record.Count;

    public IIngestDeferredUnit CreateDeferredUnit(AlignedSubtitleBlock record) =>
        new AlignedBlockUnit(record, _source, _trust);

    public void WalkWitness(
        AlignedSubtitleBlock record, Hash128 root,
        SubstrateChangeBuilder builder, IIngestDeferredUnit unit)
    {
    }

    private sealed class AlignedBlockUnit : IMultiTreeIngestDeferredUnit
    {
        private static readonly Hash128 AlignmentSchema =
            Hash128.OfCanonical("opensubtitles/alignment-block512/schema/v1");

        private readonly AlignedSubtitleBlock _block;
        private readonly Hash128 _source;
        private readonly double _trust;
        private readonly TierTree?[] _trees;
        private bool _disposed;

        public AlignedBlockUnit(AlignedSubtitleBlock block, Hash128 source, double trust)
        {
            if (block.Left.Count == 0 || block.Left.Count != block.Right.Count)
                throw new InvalidDataException("aligned subtitle block must contain equal non-empty sides");
            _block = block;
            _source = source;
            _trust = trust;
            _trees = new TierTree?[block.Count * 2];
            for (int i = 0; i < block.Count; i++)
            {
                _trees[i] = ContentTierSpine.BuildTree(block.Left[i]);
                _trees[block.Count + i] = ContentTierSpine.BuildTree(block.Right[i]);
            }
        }

        public TierTree? TreeForBatchProbe => _trees[0];
        public IReadOnlyList<TierTree?> AllProbeTrees => _trees;

        public Task<byte[]?> ProbeDescentAsync(ISubstrateReader reader, CancellationToken ct) =>
            _trees[0] is { } first
                ? ContentTierSpine.ExistenceEmitBitmapAsync(first, reader, ct)
                : Task.FromResult<byte[]?>(null);

        public Hash128 DrainInto(
            SubstrateChangeBuilder builder, double witnessWeight, byte[]? descentBitmap) =>
            DrainInto(builder, witnessWeight, new byte[]?[] { descentBitmap });

        public Hash128 DrainInto(
            SubstrateChangeBuilder builder, double witnessWeight,
            ReadOnlySpan<byte[]?> perTreeBitmaps)
        {
            var leftIds = new Hash128[_block.Count];
            var rightIds = new Hash128[_block.Count];
            var leftCoords = new double[_block.Count * 4];
            var rightCoords = new double[_block.Count * 4];
            for (int i = 0; i < _block.Count; i++)
            {
                EmitTree(builder, i, perTreeBitmaps, out leftIds[i], leftCoords.AsSpan(i * 4, 4));
                EmitTree(
                    builder, _block.Count + i, perTreeBitmaps,
                    out rightIds[i], rightCoords.AsSpan(i * 4, 4));
            }

            builder.AddEntity(
                AlignmentSchema, EntityTier.Word, EntityTypeRegistry.SourceReference, _source);
            Hash128 pairReference = Hash128.OfCanonical(
                $"opensubtitles/language-pair/{_block.PairLabel}/v1");
            builder.AddEntity(
                pairReference, EntityTier.Word, EntityTypeRegistry.SourceReference, _source);
            builder.AddEntity(
                _block.LeftLanguage, EntityTier.Word, EntityTypeRegistry.Language, _source);
            builder.AddEntity(
                _block.RightLanguage, EntityTier.Word, EntityTypeRegistry.Language, _source);

            (Hash128 leftSequence, double[] leftSequenceCoord) =
                StageSequence(builder, leftIds, leftCoords);
            (Hash128 rightSequence, double[] rightSequenceCoord) =
                StageSequence(builder, rightIds, rightCoords);

            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                leftSequence, HasLanguageTypeId, _block.LeftLanguage,
                _source, null, _trust));
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                rightSequence, HasLanguageTypeId, _block.RightLanguage,
                _source, null, _trust));

            Hash128 start = OrdinalId(_block.StartOrdinal);
            Hash128 end = OrdinalId(_block.StartOrdinal + _block.Count - 1);
            builder.AddEntity(start, EntityTier.Word, EntityTypeRegistry.Ordinal, _source);
            builder.AddEntity(end, EntityTier.Word, EntityTypeRegistry.Ordinal, _source);
            Hash128[] alignmentConstituents =
            [
                AlignmentSchema,
                pairReference,
                _block.LeftLanguage,
                leftSequence,
                _block.RightLanguage,
                rightSequence,
                start,
                end,
            ];
            Hash128 alignmentId = Hash128.Merkle(EntityTier.Document, alignmentConstituents);
            builder.AddEntity(
                alignmentId, EntityTier.Document,
                EntityTypeRegistry.OpenSubtitlesAlignment, _source);

            double[] pairCoords = new double[8];
            leftSequenceCoord.CopyTo(pairCoords, 0);
            rightSequenceCoord.CopyTo(pairCoords, 4);
            double[] alignmentCoord = Math4d.KarcherMean(pairCoords);
            StagePhysicality(builder, alignmentId, alignmentConstituents, alignmentCoord);
            return alignmentId;
        }

        private (Hash128 Id, double[] Coord) StageSequence(
            SubstrateChangeBuilder builder, Hash128[] sentenceIds, double[] sentenceCoords)
        {
            // Sequence identity is content-only. OpenSubtitles is testimony about
            // this ordered composition, not an identity namespace for it: another
            // corpus admitting the same sentence ids in the same order must reach
            // the same Merkle id and trajectory. Source/language metadata stays on
            // the entity rows and attestations rather than entering this preimage.
            Hash128[] constituents = sentenceIds;
            Hash128 id = Hash128.Merkle(EntityTier.Document, constituents);
            builder.AddEntity(
                id, EntityTier.Document, EntityTypeRegistry.OpenSubtitlesSequence, _source);
            double[] coord = Math4d.KarcherMean(sentenceCoords);
            StagePhysicality(builder, id, constituents, coord);
            return (id, coord);
        }

        private void StagePhysicality(
            SubstrateChangeBuilder builder, Hash128 entityId,
            Hash128[] constituents, double[] coord)
        {
            Hash128 physicalityId = PhysicalityId.Compute(entityId, PhysicalityType.Content);
            if (!builder.TrySeePhysicality(physicalityId)) return;
            builder.AddPhysicalityPreSeen(new PhysicalityRow(
                physicalityId, entityId, _source, PhysicalityType.Content,
                coord[0], coord[1], coord[2], coord[3], Hilbert128.Encode(coord),
                Trajectory.Build(constituents), constituents.Length,
                null, null, 0));
        }

        private void EmitTree(
            SubstrateChangeBuilder builder, int index,
            ReadOnlySpan<byte[]?> bitmaps, out Hash128 rootId, Span<double> coord)
        {
            TierTree tree = _trees[index]
                ?? throw new InvalidDataException("subtitle sentence did not produce a content tree");
            byte[]? bitmap = index < bitmaps.Length ? bitmaps[index] : null;
            if (!ContentTierSpine.EmitTree(
                    builder, tree, _source, bitmap ?? ReadOnlySpan<byte>.Empty, out rootId))
                throw new InvalidDataException("subtitle sentence content emission failed");
            TierNodeView root = tree.GetNode(tree.NaturalUnitIndex());
            unsafe
            {
                for (int axis = 0; axis < 4; axis++) coord[axis] = root.Coord[axis];
            }
        }

        internal static Hash128 OrdinalId(long ordinal) =>
            Hash128.OfCanonical($"opensubtitles/source-ordinal/{ordinal}/v1");

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (TierTree? tree in _trees) tree?.Dispose();
        }
    }
}
