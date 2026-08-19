using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

public sealed class FrameVideoTrajectoryTests
{
    [Fact]
    public async Task GenericPipeline_DrainsFramesBeforeTerminalVideoTrajectory()
    {
        Hash128 source = Hash128.OfCanonical("test/video/pipeline-source");
        var records = new VideoIngestRecord[]
        {
            new VideoIngestRecord.Frame(new VideoFrameIngestRecord(
                [255, 0, 0, 255], 1, 1, FrameIndex: 0)),
            new VideoIngestRecord.Frame(new VideoFrameIngestRecord(
                [0, 255, 0, 255], 1, 1, FrameIndex: 1)),
            new VideoIngestRecord.SequenceEnd(),
        };
        var config = new IngestBatchConfig
        {
            SourceId = source,
            BatchLabelPrefix = "test/video-pipeline",
            BatchSize = 8,
            ProbeChunkSize = 8,
            ContainmentReader = IngestBatchPipeline.AllAbsentSubstrateReader.Instance,
            WorkingSet = true,
            WorkingSetProbeInterval = 8,
            WorkingSetRecordCap = 8,
        };
        var changes = new List<SubstrateChange>();

        await foreach (var change in IngestBatchPipeline.RunAsync(
                           new AsyncEnumerableRecordStream<VideoIngestRecord>(Stream(records)),
                           new VideoFrameIngestHandler(source, layerOrder: 2),
                           config))
            changes.Add(change);

        var videoPhysicalities = changes
            .SelectMany(static c => c.Physicalities)
            .Where(p => changes.SelectMany(static c => c.Entities)
                .Any(e => e.Id == p.EntityId && e.TypeId == EntityTypeRegistry.Video))
            .ToList();
        var video = Assert.Single(videoPhysicalities);
        Assert.Equal(2, video.NConstituents);
        Assert.Equal(2, Trajectory.Constituents(video.TrajectoryXyzm!).Length);

        var relationTypes = changes.SelectMany(static c => c.Attestations)
            .Select(static a => a.TypeId).ToHashSet();
        Assert.DoesNotContain(RelationTypeRegistry.RelationTypeId("HAS_FRAME"), relationTypes);
        Assert.DoesNotContain(RelationTypeRegistry.RelationTypeId("PRECEDES_IN_TIME"), relationTypes);
        Assert.Equal(2, changes.Sum(static c => c.Metadata.InputUnitsConsumed));
    }

    [Fact]
    public void VideoSequence_IsOneOrderedPhysicality_NotStructuralTestimony()
    {
        Hash128 source = Hash128.OfCanonical("test/video/source");
        var roots = new[]
        {
            Hash128.OfCanonical("frame/0"),
            Hash128.OfCanonical("frame/1"),
            Hash128.OfCanonical("frame/2"),
        };
        var frames = new SortedDictionary<int, VideoFrameIngestHandler.FramePlacement>
        {
            [0] = new(roots[0], 1, 0, 0, 0),
            [1] = new(roots[1], 0, 1, 0, 0),
            [2] = new(roots[2], 0, 0, 1, 0),
        };
        var builder = new SubstrateChangeBuilder(source, "test/video");

        Hash128 video = VideoFrameIngestHandler.StageVideoTrajectory(builder, frames, source);
        var change = builder.Build();

        var entity = Assert.Single(change.Entities);
        Assert.Equal(video, entity.Id);
        Assert.Equal(EntityTypeRegistry.Video, entity.TypeId);
        var physicality = Assert.Single(change.Physicalities);
        Assert.Equal(video, physicality.EntityId);
        Assert.Equal(roots, Trajectory.Constituents(physicality.TrajectoryXyzm!));
        Assert.Equal(roots.Length, physicality.NConstituents);
        Assert.Empty(change.Attestations);
    }

    [Fact]
    public void VideoSequence_RejectsMissingOrdinals()
    {
        Hash128 source = Hash128.OfCanonical("test/video/source");
        var frames = new SortedDictionary<int, VideoFrameIngestHandler.FramePlacement>
        {
            [0] = new(Hash128.OfCanonical("frame/0"), 1, 0, 0, 0),
            [2] = new(Hash128.OfCanonical("frame/2"), 0, 1, 0, 0),
        };

        Assert.Throws<InvalidOperationException>(() =>
            VideoFrameIngestHandler.StageVideoTrajectory(
                new SubstrateChangeBuilder(source, "test/video-gap"), frames, source));
    }

    private static async IAsyncEnumerable<VideoIngestRecord> Stream(
        IEnumerable<VideoIngestRecord> records)
    {
        foreach (var record in records)
        {
            yield return record;
            await Task.Yield();
        }
    }
}
