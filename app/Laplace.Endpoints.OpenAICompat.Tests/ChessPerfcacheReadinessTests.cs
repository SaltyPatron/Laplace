using System.Text.Json;
using Laplace.Api.Contracts;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Endpoints.OpenAICompat.Tests;

[CollectionDefinition("Chess perfcache observation", DisableParallelization = true)]
public sealed class ChessPerfcacheObservationCollection;

[Collection("Chess perfcache observation")]
public sealed class ChessPerfcacheReadinessTests
{
    [Fact]
    public void OptionalObservationPreservesExistingReadinessContract()
    {
        var response = new ReadinessResponse(true, true, 2, 3, true);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response));
        Assert.True(json.RootElement.GetProperty("ready").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("chess_perfcache", out _));
    }

    [Fact]
    public void EmptyLoadedTransitionMapDoesNotRedefineGeneralReady()
    {
        ChessTransitionFloor.Unload();
        string path = Path.Combine(Path.GetTempPath(), $"chess-readiness-{Guid.NewGuid():N}.bin");
        try
        {
            ChessTransitionFloor.WriteBlob(path, []);
            var before = SubstrateClient.ObserveChessPerfcache(() => { });
            Assert.False(before.Transition!.IsLoaded);
            int calls = 0;
            var observation = SubstrateClient.ObserveChessPerfcache(() =>
            {
                calls++;
                ChessTransitionFloor.Load(path);
            });
            Assert.Equal(1, calls);
            Assert.True(observation.InitializationCompleted);
            Assert.True(observation.Transition!.IsLoaded);
            Assert.Equal(0, observation.Transition.RecordCount);
            Assert.False(observation.Ready);
            Assert.Equal(Environment.ProcessId, observation.ProcessId);
            Assert.Contains("process-lifetime", observation.CounterScope, StringComparison.Ordinal);
            var response = new ReadinessResponse(true, true, 2, 3, true, ChessPerfcache: observation);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(response));
            Assert.True(json.RootElement.GetProperty("ready").GetBoolean());
            var chess = json.RootElement.GetProperty("chess_perfcache");
            Assert.True(chess.GetProperty("transition").GetProperty("is_loaded").GetBoolean());
            Assert.Equal(0, chess.GetProperty("transition").GetProperty("record_count").GetInt64());
        }
        finally { ChessTransitionFloor.Unload(); File.Delete(path); }
    }

    [Fact]
    public void ObservationReportsProcessLocalHitsWithoutClaimingAnInstalledMap()
    {
        ChessTransitionFloor.Unload();
        try
        {
            var key = new Hash128(1, 2);
            var before = SubstrateClient.ObserveChessPerfcache(() => { });
            ChessTransitionFloor.Remember(key, new(3, 4));
            Assert.True(ChessTransitionFloor.TryLookup(key, out _));
            Assert.False(ChessTransitionFloor.TryLookup(new(7, 8), out _));
            var after = SubstrateClient.ObserveChessPerfcache(() => { });
            Assert.False(after.Transition!.IsLoaded);
            Assert.Equal(0, after.Transition.RecordCount);
            Assert.Equal(1, after.Transition.NovelCount);
            Assert.Equal(before.Transition!.PersistentHits, after.Transition.PersistentHits);
            Assert.Equal(before.Transition.NovelHits + 1, after.Transition.NovelHits);
            Assert.Equal(before.Transition.LookupMisses + 1, after.Transition.LookupMisses);
        }
        finally { ChessTransitionFloor.Unload(); }
    }

    [Fact]
    public void InitializationFailureRetainsObservationAndRedactsExceptionDetails()
    {
        var observation = SubstrateClient.ObserveChessPerfcache(() =>
            throw new InvalidOperationException("Password=fixture-secret; source path"));
        Assert.False(observation.InitializationCompleted);
        Assert.False(observation.Ready);
        Assert.Equal(nameof(InvalidOperationException), observation.FailureType);
        Assert.NotNull(observation.Transition);
        string json = JsonSerializer.Serialize(observation);
        Assert.DoesNotContain("fixture-secret", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ChessReadyRequiresBothLoadedNonemptyMapsAndSuccessfulInitialization()
    {
        var value = new ChessPerfcacheObservation(1, DateTimeOffset.UtcNow, "fixture", true,
            new(true, 1, 0, 0), new(true, 1, 0, 0, 0, 0));
        Assert.True(value.Ready);
        Assert.False((value with { InitializationCompleted = false }).Ready);
        Assert.False((value with { FailureType = "IOException" }).Ready);
        Assert.False((value with { Position = null }).Ready);
        Assert.False((value with { Position = value.Position! with { IsLoaded = false } }).Ready);
        Assert.False((value with { Position = value.Position! with { RecordCount = 0 } }).Ready);
        Assert.False((value with { Transition = null }).Ready);
        Assert.False((value with { Transition = value.Transition! with { IsLoaded = false } }).Ready);
        Assert.False((value with { Transition = value.Transition! with { RecordCount = 0 } }).Ready);
    }
}
