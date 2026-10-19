using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.OpenSubtitles;

public sealed record AlignedSubtitleBlock(
    string PairLabel,
    long StartOrdinal,
    IReadOnlyList<byte[]> Left,
    IReadOnlyList<byte[]> Right,
    string LeftLanguageCode,
    string RightLanguageCode)
{
    public int Count => Left.Count;
}

internal sealed class OpenSubtitlesAlignedHandler
    : IIngestRecordHandler<AlignedSubtitleBlock>
{
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
            var leftTiers = new byte[_block.Count];
            var rightTiers = new byte[_block.Count];
            var leftCoords = new double[_block.Count * 4];
            var rightCoords = new double[_block.Count * 4];
            for (int i = 0; i < _block.Count; i++)
            {
                EmitTree(builder, i, perTreeBitmaps, out leftIds[i], out leftTiers[i], leftCoords.AsSpan(i * 4, 4));
                EmitTree(
                    builder, _block.Count + i, perTreeBitmaps,
                    out rightIds[i], out rightTiers[i], rightCoords.AsSpan(i * 4, 4));
            }

            // The zip name and the 512-line batch are how the file is read. One alignment
            // is one pair of utterances. An utterance is [language, sentence], with the
            // language tag spelled as the archive named it.
            Hash128 leftLanguage = LanguageReference.Emit(
                builder, _block.LeftLanguageCode, _source, _trust);
            Hash128 rightLanguage = LanguageReference.Emit(
                builder, _block.RightLanguageCode, _source, _trust);
            byte leftLanguageTier = LanguageTier(_block.LeftLanguageCode);
            byte rightLanguageTier = LanguageTier(_block.RightLanguageCode);

            Hash128 first = default;
            for (int i = 0; i < _block.Count; i++)
            {
                (Hash128 leftUtterance, double[] leftCoord, byte leftTier) = StageUtterance(
                    builder, leftLanguage, leftLanguageTier, leftIds[i], leftTiers[i], leftCoords.AsSpan(i * 4, 4));
                (Hash128 rightUtterance, double[] rightCoord, byte rightTier) = StageUtterance(
                    builder, rightLanguage, rightLanguageTier, rightIds[i], rightTiers[i], rightCoords.AsSpan(i * 4, 4));
                Hash128 alignmentId = StageAlignment(
                    builder, leftUtterance, leftTier, leftCoord, rightUtterance, rightTier, rightCoord);
                if (i == 0) first = alignmentId;
            }
            return first;
        }

        private static byte LanguageTier(string? code)
        {
            string? resolved = LanguageReference.ResolveCode(code) ?? "und";
            return CompositionLaw.TryFace(resolved, out CompositionLaw.Face face) ? face.Tier : (byte)0;
        }

        private (Hash128 Id, double[] Coord, byte Tier) StageUtterance(
            SubstrateChangeBuilder builder, Hash128 language, byte languageTier,
            Hash128 sentenceId, byte sentenceTier, ReadOnlySpan<double> sentenceCoord)
        {
            Hash128[] constituents = [language, sentenceId];
            byte floor = CompositionLaw.ParentFloor([languageTier, sentenceTier]);
            Hash128 id = Hash128.Merkle(floor, constituents);
            builder.AddEntity(
                id, floor, EntityTypeRegistry.OpenSubtitlesSequence);
            double[] coord = new double[4];
            sentenceCoord.CopyTo(coord);
            StagePhysicality(builder, id, constituents, coord, PhysicalityType.Content);
            return (id, coord, floor);
        }

        private Hash128 StageAlignment(
            SubstrateChangeBuilder builder,
            Hash128 leftUtterance, byte leftTier, double[] leftCoord,
            Hash128 rightUtterance, byte rightTier, double[] rightCoord)
        {
            Hash128[] constituents = [leftUtterance, rightUtterance];
            byte floor = CompositionLaw.ParentFloor([leftTier, rightTier]);
            Hash128 id = Hash128.Merkle(floor, constituents);
            builder.AddEntity(
                id, floor, EntityTypeRegistry.OpenSubtitlesAlignment);
            double[] pairCoords = new double[8];
            leftCoord.CopyTo(pairCoords, 0);
            rightCoord.CopyTo(pairCoords, 4);
            StagePhysicality(
                builder, id, constituents, Math4d.KarcherMean(pairCoords),
                PhysicalityType.ParseStructure);
            return id;
        }

        private void StagePhysicality(
            SubstrateChangeBuilder builder, Hash128 entityId,
            Hash128[] constituents, double[] coord, PhysicalityType type)
        {
            Hash128 physicalityId = PhysicalityId.Compute(entityId, type);
            builder.AddPhysicality(new PhysicalityRow(
                physicalityId, entityId, _source, type,
                coord[0], coord[1], coord[2], coord[3], Hilbert128.Encode(coord),
                Trajectory.Build(constituents), constituents.Length,
                null, null, 0));
        }

        private void EmitTree(
            SubstrateChangeBuilder builder, int index,
            ReadOnlySpan<byte[]?> bitmaps, out Hash128 rootId, out byte rootTier, Span<double> coord)
        {
            TierTree tree = _trees[index]
                ?? throw new InvalidDataException("subtitle sentence did not produce a content tree");
            byte[]? bitmap = index < bitmaps.Length ? bitmaps[index] : null;
            if (!ContentTierSpine.EmitTree(
                    builder, tree, _source, bitmap ?? ReadOnlySpan<byte>.Empty, out rootId))
                throw new InvalidDataException("subtitle sentence content emission failed");
            TierNodeView root = tree.GetNode(tree.NaturalUnitIndex());
            rootTier = root.Tier;
            unsafe
            {
                for (int axis = 0; axis < 4; axis++) coord[axis] = root.Coord[axis];
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (TierTree? tree in _trees) tree?.Dispose();
        }
    }
}
