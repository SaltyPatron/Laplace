using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessSyzygyTests
{
    private const string EndgameGame =
        "[Event \"T\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Date \"2024.01.01\"]\n"
        + "[Result \"1-0\"]\n[SetUp \"1\"]\n[FEN \"4k3/8/8/8/8/8/8/3QK3 w - - 0 1\"]\n\n"
        + "1. Qd5 Kf8 2. Ke2 1-0\n";

    private const string FullGame =
        "[Event \"T\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Date \"2024.01.01\"]\n[Result \"1-0\"]\n\n"
        + "1. e4 e5 2. Qh5 Nc6 3. Bc4 Nf6 4. Qxf7# 1-0\n";

    private sealed class FakeProber(int largest, SyzygyVerdict? verdict) : ISyzygyProber
    {
        public List<string> ProbedFens { get; } = [];
        public int Largest => largest;
        public int? ProbeWdl(Board board)
        {
            ProbedFens.Add(board.ToFen());
            return verdict?.Wdl;
        }
        public SyzygyVerdict? Probe(Board board)
        {
            ProbedFens.Add(board.ToFen());
            return verdict;
        }
    }

    private static SubstrateChange Derive(ISyzygyProber prober, string pgn)
    {
        var parsed = ChessPgnDecomposer.TryParseGame(pgn)!;
        var witnessed = ChessAnalyze.WitnessedFromParsed(parsed);
        var b = new SubstrateChangeBuilder(ChessSyzygy.SourceId, "test/syzygy");
        ChessSyzygy.DeriveGame(b, witnessed, prober);
        return b.SetInputUnitsConsumed(1).Build();
    }

    [Theory]
    [InlineData(SyzygyNative.Loss, "loss")]
    [InlineData(SyzygyNative.BlessedLoss, "blessed-loss")]
    [InlineData(SyzygyNative.Draw, "draw")]
    [InlineData(SyzygyNative.CursedWin, "cursed-win")]
    [InlineData(SyzygyNative.Win, "win")]
    public void WdlToken_MapsFathomOrder(int wdl, string expected)
        => Assert.Equal(expected, ChessSyzygy.WdlToken(wdl));

    [Theory]
    [InlineData("KQvK", 3)]
    [InlineData("KRvK", 3)]
    [InlineData("KBBvKN", 5)]
    public void TryParseMaterial_ReadsSyzygyFilenames(string name, int men)
    {
        Assert.True(SyzygyTableUnpack.TryParseMaterial(name, out var pieces));
        Assert.Equal(men, pieces.Length);
    }

    [Fact]
    public void ToBitboards_KQvK_PlacesEveryPiece()
    {
        var bb = ChessSyzygy.ToBitboards(Board.FromFen("4k3/8/8/8/8/8/8/3QK3 w - - 0 1"));
        Assert.Equal(1UL << 3 | 1UL << 4, bb.White);
        Assert.Equal(1UL << 60, bb.Black);
        Assert.Equal(1UL << 4 | 1UL << 60, bb.Kings);
        Assert.Equal(1UL << 3, bb.Queens);
        Assert.Equal(0u, bb.Ep);
    }

    [Fact]
    public void MenCount_CountsBothSides()
    {
        Assert.Equal(3, ChessSyzygy.MenCount(Board.FromFen("4k3/8/8/8/8/8/8/3QK3 w - - 0 1")));
        Assert.Equal(32, ChessSyzygy.MenCount(Board.FromFen(ChessModality.StartFen)));
    }

    [Fact]
    public void DeriveGame_DepositsPositionGrain_NullContext()
    {
        var prober = new FakeProber(3, new SyzygyVerdict(SyzygyNative.Win, 12));
        var change = Derive(prober, EndgameGame);

        Assert.Equal(4, prober.ProbedFens.Count);
        var wdl = change.Attestations.Where(a => a.TypeId == ChessVocabulary.HasWdlType).ToList();
        var dtz = change.Attestations.Where(a => a.TypeId == ChessVocabulary.HasDtzType).ToList();
        Assert.Equal(4, wdl.Count);
        Assert.Equal(4, dtz.Count);
        Assert.All(wdl.Concat(dtz), a =>
        {
            Assert.Equal(ChessSyzygy.SourceId, a.SourceId);
            Assert.Null(a.ContextId);
        });
        Assert.All(wdl, a => Assert.Equal(ContentEmitter.RootId("win"), a.ObjectId));
    }

    [Fact]
    public void DeriveProduct_StampsPositionMarker()
    {
        var m = new ChessModality();
        var board = Board.FromFen("4k3/8/8/8/8/8/8/3QK3 w - - 0 1");
        string surface = m.StateKey(new ChessState(board));
        var posId = ChessCompose.PositionId(surface);
        var b = new SubstrateChangeBuilder(ChessSyzygy.SourceId, "test/syzygy-product");
        ChessSyzygy.DeriveProduct(b, new SyzygyProduct(surface, posId, SyzygyNative.Win, 7));
        var change = b.SetInputUnitsConsumed(1).Build();

        Assert.Contains(change.Entities, e => e.Id == ChessSyzygy.MarkerId(posId, ChessSyzygy.Version));
        Assert.Contains(change.Attestations, a =>
            a.TypeId == ChessVocabulary.AnalyzedAtType && a.SubjectId == posId);
        Assert.Contains(change.Attestations, a =>
            a.TypeId == ChessVocabulary.HasWdlType && a.ContextId is null);
    }

    [Fact]
    public void DeriveGame_SkipsPositions_TheTableSetDoesNotCover()
    {
        var prober = new FakeProber(3, new SyzygyVerdict(SyzygyNative.Draw, 0));
        var change = Derive(prober, FullGame);
        Assert.Empty(prober.ProbedFens);
        Assert.DoesNotContain(change.Attestations, a => a.TypeId == ChessVocabulary.HasWdlType);
    }

    [Fact]
    public void Record_TrunkRoot_IsTheVersionedPositionMarker()
    {
        var m = new ChessModality();
        var board = Board.FromFen("4k3/8/8/8/8/8/8/3QK3 w - - 0 1");
        string surface = m.StateKey(new ChessState(board));
        var posId = ChessCompose.PositionId(surface);
        var record = new ChessSyzygyRecord(new SyzygyProduct(surface, posId, SyzygyNative.Win, 1));
        Assert.Equal(ChessSyzygy.MarkerId(posId, ChessSyzygy.Version), record.TrunkRootId);
    }

    [Fact]
    public void DeriveGame_EmitsOnlyDeclaredRelations()
    {
        var declared = ChessSeedManifest.Relations
            .Select(RelationTypeRegistry.RelationTypeId).ToHashSet();
        var change = Derive(new FakeProber(3, new SyzygyVerdict(SyzygyNative.Win, 8)), EndgameGame);
        var undeclared = change.Attestations
            .Select(a => a.TypeId).Distinct().Where(t => !declared.Contains(t)).ToList();
        Assert.Empty(undeclared);
    }

    [Fact]
    public void TryLoadProber_NoTablebasesAnywhere_IsACleanNoOp()
    {
        var priorEnv = Environment.GetEnvironmentVariable("LAPLACE_SYZYGY");
        var priorRoot = Environment.GetEnvironmentVariable("LAPLACE_DATA_ROOT");
        string emptyRoot = Path.Combine(Path.GetTempPath(), $"no-syzygy-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyRoot);
        Environment.SetEnvironmentVariable(
            "LAPLACE_SYZYGY", Path.Combine(Path.GetTempPath(), $"no-such-syzygy-{Guid.NewGuid():N}"));
        Environment.SetEnvironmentVariable("LAPLACE_DATA_ROOT", emptyRoot);
        try
        {
            var d = new ChessSyzygyDecomposer();
            Assert.False(d.TryLoadProber(out _));
            Assert.False(ChessLabPaths.SyzygyDir.Found);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_SYZYGY", priorEnv);
            Environment.SetEnvironmentVariable("LAPLACE_DATA_ROOT", priorRoot);
            try { Directory.Delete(emptyRoot, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void SyzygyDir_FallsBackToCorpusLayout_OnlyWhenTablesArePresent()
    {
        var priorEnv = Environment.GetEnvironmentVariable("LAPLACE_SYZYGY");
        var priorRoot = Environment.GetEnvironmentVariable("LAPLACE_DATA_ROOT");
        string root = Path.Combine(Path.GetTempPath(), $"syzygy-root-{Guid.NewGuid():N}");
        string tables = Path.Combine(root, "Games", "Chess", "syzygy", "3-4-5");
        Directory.CreateDirectory(tables);
        Environment.SetEnvironmentVariable("LAPLACE_SYZYGY", null);
        Environment.SetEnvironmentVariable("LAPLACE_DATA_ROOT", root);
        try
        {
            Assert.False(ChessLabPaths.SyzygyDir.Found);
            File.WriteAllBytes(Path.Combine(tables, "KQvK.rtbw"), [0]);
            var probe = ChessLabPaths.SyzygyDir;
            Assert.True(probe.Found);
            Assert.Equal(tables, probe.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_SYZYGY", priorEnv);
            Environment.SetEnvironmentVariable("LAPLACE_DATA_ROOT", priorRoot);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}

/// <summary>
/// ONE init/free for the whole class, not one per test method.
///
/// xUnit constructs a fresh instance of a test class for EVERY test method, so a ctor that
/// called SyzygyNative.Init and a Dispose that called SyzygyNative.Free cycled the tablebase
/// mapping once per [InlineData] case. That state is process-global by design — the
/// SyzygyNative doc says "one loaded table set at a time" — and the vendored Fathom prober
/// does not fully reset its statics in tb_free, so a subsequent tb_init leaves stale pointers
/// behind. The next probe walks them and the process takes a SIGSEGV inside gen_captures:
/// no managed exception, nothing catchable, and a crash position that moves between runs
/// because it depends on how many map/unmap cycles ran first.
///
/// IClassFixture gives exactly one instance for the class, which is what process-global state
/// requires. If another test class ever needs the tablebases, promote this to a collection
/// fixture rather than re-adding a per-test Init.
/// </summary>
public sealed class SyzygyTablebaseFixture : IDisposable
{
    public SyzygyTablebaseFixture()
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out var root), "repo root not resolvable");
        var dir = Path.Combine(root, "test-data", "syzygy");
        Assert.True(Directory.Exists(dir), $"fixture dir missing: {dir}");
        Assert.Equal(3, SyzygyNative.Init(dir));
        Assert.Equal(3, SyzygyNative.Largest());
    }

    public void Dispose() => SyzygyNative.Free();
}

[Trait("Tier", "fast")]
public sealed class SyzygyNativeFixtureTests : IClassFixture<SyzygyTablebaseFixture>
{
    public SyzygyNativeFixtureTests(SyzygyTablebaseFixture fixture) => _ = fixture;

    [Theory]
    [InlineData("4k3/8/8/8/8/8/8/3QK3 w - - 0 1", SyzygyNative.Win)]
    [InlineData("4k3/8/8/8/8/8/8/3QK3 b - - 0 1", SyzygyNative.Loss)]
    [InlineData("4k3/8/8/8/8/8/8/R3K3 w - - 0 1", SyzygyNative.Win)]
    [InlineData("8/8/8/8/1k6/1R6/8/K7 b - - 0 1", SyzygyNative.Draw)]
    public void NativeProber_HandPickedFens_MatchKnownWdl(string fen, int expectedWdl)
    {
        var verdict = new SyzygyNativeProber().Probe(Board.FromFen(fen));
        Assert.NotNull(verdict);
        Assert.Equal(expectedWdl, verdict.Value.Wdl);
    }

    [Fact]
    public void Unpack_KQvK_YieldsProbeableProducts_InParallel()
    {
        var prober = new SyzygyNativeProber();
        int n = 0;
        foreach (var product in SyzygyTableUnpack.ExtractMaterialAsync("KQvK", prober, workers: 4)
                     .ToBlockingEnumerable())
        {
            Assert.False(string.IsNullOrEmpty(product.Surface));
            Assert.InRange(product.Wdl, 0, 4);
            if (++n >= 50) break;
        }
        Assert.True(n >= 50, "KQvK unpack should yield many products");
    }

    [Fact]
    public void DeriveGame_NativeProber_DepositsExactVerdicts()
    {
        const string pgn =
            "[Event \"T\"]\n[White \"A\"]\n[Black \"B\"]\n[Date \"2024.01.01\"]\n"
            + "[Result \"1-0\"]\n[SetUp \"1\"]\n[FEN \"4k3/8/8/8/8/8/8/3QK3 w - - 0 1\"]\n\n"
            + "1. Qd5 1-0\n";
        var parsed = ChessPgnDecomposer.TryParseGame(pgn)!;
        var b = new SubstrateChangeBuilder(ChessSyzygy.SourceId, "test/syzygy-native");
        ChessSyzygy.DeriveGame(b, ChessAnalyze.WitnessedFromParsed(parsed), new SyzygyNativeProber());
        var change = b.SetInputUnitsConsumed(1).Build();

        var wdl = change.Attestations.Where(a => a.TypeId == ChessVocabulary.HasWdlType).ToList();
        Assert.Equal(2, wdl.Count);
        Assert.All(wdl, a => Assert.Null(a.ContextId));
        Assert.Contains(wdl, a => a.ObjectId == ContentEmitter.RootId("win"));
        Assert.Contains(wdl, a => a.ObjectId == ContentEmitter.RootId("loss"));
    }
}
