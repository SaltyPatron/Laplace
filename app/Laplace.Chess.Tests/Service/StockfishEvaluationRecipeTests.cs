using System.Collections.Concurrent;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class StockfishEvaluationRecipeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stockfish-recipe-" + Guid.NewGuid().ToString("N"));
    private string Engine => Path.Combine(_root, "stockfish");
    private string Cache => Path.Combine(_root, "evaluations.bin");
    private static readonly Hash128 Input = new(1, 2);

    public StockfishEvaluationRecipeTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Engine, "engine-one");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private StockfishEvaluationRecipe Capture(StockfishEvaluationOptions? settings = null,
        string name = "Stockfish test")
    {
        settings ??= new StockfishEvaluationOptions();
        return StockfishEvaluationRecipe.Capture(Engine, name, settings, new Dictionary<string, string>
        {
            ["Threads"] = settings.Threads.ToString(), ["Hash"] = settings.HashMb.ToString(),
            ["NumaPolicy"] = settings.NumaPolicy, ["EvalFile"] = settings.EvalFile ?? "nn-embedded.nnue",
            ["SyzygyPath"] = settings.SyzygyPath ?? "", ["UCI_LimitStrength"] = "false",
        });
    }

    private void Save(StockfishEvaluationRecipe recipe)
        => StockfishEvalCache.Save(Cache, recipe,
            new ConcurrentDictionary<Hash128, int?>([new KeyValuePair<Hash128, int?>(Input, 37)]));

    [Fact]
    public void ExactRecipeReusesCacheAndEngineReplacementCannotReuseOldEntries()
    {
        var before = Capture();
        Save(before);
        Assert.Equal(37, StockfishEvalCache.Load(Cache, Capture())[Input]);
        DateTime timestamp = File.GetLastWriteTimeUtc(Engine);
        File.WriteAllText(Engine, "engine-two"); // same length, same path, preserved timestamp
        File.SetLastWriteTimeUtc(Engine, timestamp);
        var after = Capture();
        Assert.NotEqual(before.Id, after.Id);
        Assert.Empty(StockfishEvalCache.Load(Cache, after));
        Assert.Equal(37, StockfishEvalCache.Load(Cache, before)[Input]);
        Assert.NotEqual(before.MarkerKey("line-id"), after.MarkerKey("line-id"));
    }

    [Fact]
    public void ChangedOptionsBudgetsOrEngineVersionCannotReuseCacheOrCompletionMarker()
    {
        var before = Capture();
        Save(before);
        StockfishEvaluationRecipe[] changed =
        [
            Capture(new() { Threads = 2 }), Capture(new() { HashMb = 64 }),
            Capture(new() { NumaPolicy = "none" }), Capture(new() { Depth = 12 }),
            Capture(new() { Nodes = 50_000 }), Capture(new() { TimeoutSeconds = 60 }),
            Capture(new() { Processes = 1 }),
            Capture(name: "Stockfish next"),
        ];
        foreach (var recipe in changed)
        {
            Assert.Empty(StockfishEvalCache.Load(Cache, recipe));
            Assert.NotEqual(before.MarkerKey("line-id"), recipe.MarkerKey("line-id"));
        }
    }

    [Fact]
    public void NetworkAndTablebaseContentChangesCreateNewRecipes()
    {
        string network = Path.Combine(_root, "network.nnue");
        string tables = Path.Combine(_root, "tables");
        Directory.CreateDirectory(tables);
        string wdl = Path.Combine(tables, "KQvK.rtbw");
        File.WriteAllText(network, "network-one");
        File.WriteAllText(wdl, "table-one");
        var settings = new StockfishEvaluationOptions { EvalFile = network, SyzygyPath = tables };
        var before = Capture(settings);
        Save(before);
        File.WriteAllText(network, "network-two");
        Assert.Empty(StockfishEvalCache.Load(Cache, Capture(settings)));
        File.WriteAllText(network, "network-one");
        File.WriteAllText(wdl, "table-two");
        Assert.Empty(StockfishEvalCache.Load(Cache, Capture(settings)));
    }

    [Fact]
    public void MovingAnIdenticalEngineDoesNotInventANewContentIdentity()
    {
        var before = Capture();
        string copy = Path.Combine(_root, "copy");
        File.Copy(Engine, copy);
        var after = StockfishEvaluationRecipe.Capture(copy, before.EngineName, before.Settings, before.EffectiveOptions);
        Assert.Equal(before.Id, after.Id);
    }

    [Fact]
    public void CopiedCacheAndJournalMustMatchRecipeHeaderAsWellAsFilename()
    {
        var before = Capture();
        var other = Capture(new() { Threads = 2 });
        Save(before);
        File.Copy(StockfishEvalCache.RecipePath(Cache, before), StockfishEvalCache.RecipePath(Cache, other));
        Assert.Empty(StockfishEvalCache.Load(Cache, other));
        File.Delete(StockfishEvalCache.RecipePath(Cache, other));
        StockfishEvalCache.Append(Cache, before, [new KeyValuePair<Hash128, int?>(Input, 73)]);
        Assert.Equal(73, StockfishEvalCache.Load(Cache, before)[Input]);
        File.Copy(StockfishEvalCache.RecipePath(Cache, before) + ".journal",
            StockfishEvalCache.RecipePath(Cache, other) + ".journal");
        Assert.Empty(StockfishEvalCache.Load(Cache, other));
    }

    [Fact]
    public void LegacyCacheIsPreservedAndNeverRelabeledAsCurrentEngineEvidence()
    {
        StockfishEvalCache.Save(Cache, 1, 10, 0,
            new ConcurrentDictionary<Hash128, int?>([new KeyValuePair<Hash128, int?>(Input, 99)]));
        byte[] legacy = File.ReadAllBytes(Cache);
        var recipe = Capture();
        Assert.Empty(StockfishEvalCache.Load(Cache, recipe));
        File.Copy(Cache, StockfishEvalCache.RecipePath(Cache, recipe));
        Assert.Empty(StockfishEvalCache.Load(Cache, recipe));
        Save(recipe);
        Assert.Equal(legacy, File.ReadAllBytes(Cache));
        Assert.Equal(99, StockfishEvalCache.Load(Cache, 1, 10, 0)[Input]);
        Assert.Equal(37, StockfishEvalCache.Load(Cache, recipe)[Input]);
    }

    [Fact]
    public void FullFenClockContextAndRecipeArePartOfPositionCacheIdentity()
    {
        var recipe = Capture();
        const string position = "4k3/8/8/8/8/8/8/3QK3 w - - ";
        Assert.NotEqual(recipe.InputKey(position + "0 1"), recipe.InputKey(position + "99 1"));
        Assert.NotEqual(recipe.InputKey(position + "0 1"), Capture(new() { Threads = 2 }).InputKey(position + "0 1"));
    }

    [Fact]
    public void ProcessBudgetHonorsCpuQuotaThreadsAndExplicitOverride()
    {
        var options = new StockfishEvaluationOptions { Threads = 2 };
        Assert.Equal(new StockfishEvaluationResources(6, 3, false, 6, false),
            StockfishEvaluationResources.Resolve(options, topologyWorkers: 20, processCpuQuota: 6));
        Assert.Equal(new StockfishEvaluationResources(4, 2, false, 4, false),
            StockfishEvaluationResources.Resolve(options, topologyWorkers: 4, processCpuQuota: 20));
        Assert.Equal(new StockfishEvaluationResources(1, 1, false, 2, true),
            StockfishEvaluationResources.Resolve(options, topologyWorkers: 20, processCpuQuota: 1));
        Assert.Equal(new StockfishEvaluationResources(6, 5, true, 10, true),
            StockfishEvaluationResources.Resolve(options with { Processes = 5 }, 20, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StockfishEvaluationResources.Resolve(options with { Processes = 0 }, 20, 6));
    }

    [Fact]
    public async Task EvaluatorPoolBoundsProcessesAndCancelledWaitDoesNotConsumeALease()
    {
        int created = 0;
        using var pool = new StockfishEvaluatorPool(() => { created++; return new StubEvaluator(); }, capacity: 2);
        var first = pool.Rent();
        var second = pool.Rent();
        using var cancelled = new CancellationTokenSource();
        var waiting = Task.Run(() => pool.Rent(cancelled.Token));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
        Assert.Equal(2, created);
        pool.Return(first);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Same(first, pool.Rent(deadline.Token));
        Assert.Equal(2, created);
        pool.Return(first);
        pool.Return(second);
        Assert.Throws<InvalidOperationException>(() => pool.Return(second));
    }

    [Fact]
    public async Task EvaluatorPoolDisposalCancelsWaitersAndOwnsActiveEngines()
    {
        var engine = new StubEvaluator();
        using var pool = new StockfishEvaluatorPool(() => engine);
        Assert.Same(engine, pool.Rent());
        var waiting = Task.Run(() => pool.Rent());
        pool.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
        Assert.True(engine.Disposed);
        pool.Return(engine); // outstanding leases may unwind after cancellation/disposal
    }

    [Fact]
    public void EvaluatorFactoryFailureReturnsItsCapacity()
    {
        int attempts = 0;
        using var pool = new StockfishEvaluatorPool(() => ++attempts == 1
            ? throw new InvalidDataException("startup failed") : new StubEvaluator());
        Assert.Throws<InvalidDataException>(() => pool.Rent());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var engine = pool.Rent(deadline.Token);
        pool.Return(engine);
        Assert.Equal(2, attempts);
    }

    private sealed class StubEvaluator : IPositionEvaluator, IDisposable
    {
        public bool Disposed { get; private set; }
        public int? EvaluateCp(string fen) => 0;
        public void Dispose() => Disposed = true;
    }
}
