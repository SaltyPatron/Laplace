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
            if (verdict is not { } value) return null;
            var move = MoveGen.Legal(board).FirstOrDefault();
            if (move == default) return null;
            return value with
            {
                From = Board.RankOf(move.From) * 8 + Board.FileOf(move.From),
                To = Board.RankOf(move.To) * 8 + Board.FileOf(move.To),
                Promotes = move.IsPromotion ? 1 : 0,
            };
        }
    }

    private sealed class DelayedRootProber : ISyzygyProber
    {
        public int Largest => 3;
        public int WdlCalls;
        public int RootCalls;

        public int? ProbeWdl(Board board)
        {
            Interlocked.Increment(ref WdlCalls);
            throw new InvalidOperationException("material extraction must not decode WDL twice");
        }

        public SyzygyVerdict? Probe(Board board)
        {
            Interlocked.Increment(ref RootCalls);
            // Vary completion time from board content so a completion-order stream is
            // observably different from the canonical placement walk.
            int delay = 1 + (int)(ChessCompose.PositionId(board).Lo % 5);
            Thread.Sleep(delay);
            var move = MoveGen.Legal(board).FirstOrDefault();
            if (move == default) return null;
            return new SyzygyVerdict(
                SyzygyNative.Draw, 0,
                Board.RankOf(move.From) * 8 + Board.FileOf(move.From),
                Board.RankOf(move.To) * 8 + Board.FileOf(move.To),
                move.IsPromotion ? 1 : 0);
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

    [Theory]
    [InlineData("KQvK", 3)]
    [InlineData("KQvKR", 4)]
    [InlineData("KBBvKN", 5)]
    public void ParseMen_CountsBothSides(string name, int men)
        => Assert.Equal(men, SyzygyTableUnpack.ParseMen(name));

    [Fact]
    public void ParseMen_Unparseable_SitsAboveEveryCeiling()
        => Assert.Equal(int.MaxValue, SyzygyTableUnpack.ParseMen("not-a-material"));

    [Fact]
    public void ResolveMaxMen_DefaultsTo3_AndReadsTheEnvKnob()
    {
        var prior = Environment.GetEnvironmentVariable("LAPLACE_SYZYGY_MAX_MEN");
        try
        {
            Environment.SetEnvironmentVariable("LAPLACE_SYZYGY_MAX_MEN", null);
            Assert.Equal(SyzygyTableUnpack.DefaultMaxMen, SyzygyTableUnpack.ResolveMaxMen());
            Environment.SetEnvironmentVariable("LAPLACE_SYZYGY_MAX_MEN", "5");
            Assert.Equal(5, SyzygyTableUnpack.ResolveMaxMen());
            // Below two kings / non-numeric: fall back, never a zero-wide walk.
            Environment.SetEnvironmentVariable("LAPLACE_SYZYGY_MAX_MEN", "0");
            Assert.Equal(SyzygyTableUnpack.DefaultMaxMen, SyzygyTableUnpack.ResolveMaxMen());
            Environment.SetEnvironmentVariable("LAPLACE_SYZYGY_MAX_MEN", "nope");
            Assert.Equal(SyzygyTableUnpack.DefaultMaxMen, SyzygyTableUnpack.ResolveMaxMen());
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_SYZYGY_MAX_MEN", prior);
        }
    }

    [Fact]
    public void FilterByMenCeiling_KeepsOnlyTablesAtOrUnderTheCeiling()
    {
        string[] paths =
            ["/t/KQvK.rtbw", "/t/KRvK.rtbw", "/t/KQvKR.rtbw", "/t/KBBvKN.rtbw", "/t/junk.rtbw"];
        var kept = ChessSyzygyDecomposer.FilterByMenCeiling(paths, 3);
        Assert.Equal(["/t/KQvK.rtbw", "/t/KRvK.rtbw"], kept);
        var five = ChessSyzygyDecomposer.FilterByMenCeiling(paths, 5);
        Assert.Equal(4, five.Count);                    // junk (unparseable) never passes
        Assert.DoesNotContain("/t/junk.rtbw", five);
    }

    [Fact]
    public void ExplainEmptyDirectory_TriagesNoTables_CeilingScoped_AndRealAnomaly()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"syzygy-explain-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // No .rtbw at all — the documented dependency no-op.
            var none = ChessSyzygyDecomposer.ExplainEmptyDirectory(dir, 3);
            Assert.NotNull(none);
            Assert.Equal("dependency-unset", none.Value.Status);

            // Only tables above the ceiling — deliberate scoping, knob named.
            File.WriteAllBytes(Path.Combine(dir, "KQvKR.rtbw"), [0]);
            File.WriteAllBytes(Path.Combine(dir, "KBBvKN.rtbw"), [0]);
            var scoped = ChessSyzygyDecomposer.ExplainEmptyDirectory(dir, 3);
            Assert.NotNull(scoped);
            Assert.Equal("scoped-out", scoped.Value.Status);
            Assert.Contains("LAPLACE_SYZYGY_MAX_MEN", scoped.Value.Detail);

            // A table under the ceiling with zero records applied stays unexplained.
            File.WriteAllBytes(Path.Combine(dir, "KQvK.rtbw"), [0]);
            Assert.Null(ChessSyzygyDecomposer.ExplainEmptyDirectory(dir, 3));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
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
    public async Task MaterialExtraction_ParallelProbePreservesCanonicalPlacementOrder()
    {
        var expected = new List<string>();
        Assert.True(SyzygyTableUnpack.TryParseMaterial("KQvK", out var pieces));
        var modality = new ChessModality();
        await foreach (var board in SyzygyTableUnpack.EnumerateBoardsAsync(pieces, CancellationToken.None))
        {
            if (MoveGen.Legal(board).Count == 0) continue;
            expected.Add(modality.StateKey(new ChessState(board)));
            if (expected.Count == 64) break;
        }

        var prober = new DelayedRootProber();
        var actual = new List<string>();
        await foreach (var product in SyzygyTableUnpack.ExtractMaterialAsync(
                           "KQvK", prober, workers: 4,
                           ct: CancellationToken.None))
        {
            actual.Add(product.Surface);
            if (actual.Count == expected.Count) break;
        }

        Assert.Equal(expected, actual);
        Assert.Equal(0, prober.WdlCalls);
        Assert.True(prober.RootCalls >= actual.Count);
    }

    [Fact]
    public void DeriveGame_DepositsDeduplicatedTransitionGraph()
    {
        var prober = new FakeProber(3, new SyzygyVerdict(SyzygyNative.Win, 12));
        var change = Derive(prober, EndgameGame);

        Assert.Equal(4, prober.ProbedFens.Count);
        var chunks = change.Physicalities.Where(static p => p.NConstituents == 3).ToList();
        Assert.Equal(4, chunks.Count);
        Assert.Contains(change.Physicalities,
            p => p.EntityId == ChessSyzygy.EndgameLineId(
                ChessAnalyze.WitnessedFromParsed(ChessPgnDecomposer.TryParseGame(EndgameGame)!).LineId));
        Assert.Empty(change.Attestations);
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
    public void MaterialGraph_StoresExactPositionMovePosition_AsResolvableLaplaceObjects()
    {
        var modality = new ChessModality();
        var board = Board.FromFen("4k3/8/8/8/8/8/8/3QK3 w - - 0 1");
        var move = Assert.Single(MoveGen.Legal(board), static m => m.ToUci() == "d1d5");
        string surface = modality.StateKey(new ChessState(board));
        var product = new SyzygyProduct(
            surface, ChessCompose.PositionId(surface), SyzygyNative.Win, 9,
            Board.RankOf(move.From) * 8 + Board.FileOf(move.From),
            Board.RankOf(move.To) * 8 + Board.FileOf(move.To));
        var record = ChessSyzygyRecord.CreateChunk("KQvK", [product]);
        var b = new SubstrateChangeBuilder(ChessSyzygy.SourceId, "test/syzygy-material");

        Assert.NotNull(record.Chunk);
        Assert.NotNull(record.PreparedChunk);
        ChessSyzygy.DeriveTransitionChunk(b, record.PreparedChunk!);
        var change = b.SetInputUnitsConsumed(1).Build();

        var trajectory = Assert.Single(
            change.Physicalities, static p => p.Type == PhysicalityType.Projection);
        var ids = Trajectory.Constituents(trajectory.TrajectoryXyzm!);
        var next = board.Clone();
        MoveApply.Make(next, move);
        Assert.Equal(
        [
            ChessCompose.PositionId(board),
            ChessCompose.MoveId(board.Squares[move.From], move),
            ChessCompose.PositionId(next),
        ], ids);
        Assert.All(ids, id => Assert.Contains(change.Entities, e => e.Id == id));
        Assert.All(ids, id => Assert.Contains(change.Physicalities,
            p => p.EntityId == id && p.Type == PhysicalityType.Content));
        Assert.Contains(change.Entities,
            e => e.Id == ids[0] && e.TypeId == ChessVocabulary.PositionType);
        Assert.Contains(change.Entities,
            e => e.Id == ids[1] && e.TypeId == ChessVocabulary.MoveType);
        Assert.Contains(change.Entities,
            e => e.Id == ids[2] && e.TypeId == ChessVocabulary.PositionType);
        Assert.Empty(change.Attestations);
    }

    [Theory]
    [InlineData(0, SyzygyNative.Loss, -17)]
    [InlineData(1, SyzygyNative.Draw, 0)]
    [InlineData(2, SyzygyNative.Win, 42)]
    public void MaterialGraph_FlagsRoundTripRoleWdlAndDtz(int role, int wdl, int dtz)
    {
        ulong packed = ChessSyzygy.PackTransitionFlags(role, wdl, dtz);
        Assert.Equal((role, wdl, dtz), ChessSyzygy.UnpackTransitionFlags(packed));
    }

    [Fact]
    public void MaterialRoot_ContainsChunkIdentities_NotDecodedBoards()
    {
        var chunks = new[]
        {
            new SyzygyChunkRef(Hash128.OfCanonical("chunk/a"), [1d, 0d, 0d, 0d]),
            new SyzygyChunkRef(Hash128.OfCanonical("chunk/b"), [0d, 1d, 0d, 0d]),
        };
        var b = new SubstrateChangeBuilder(ChessSyzygy.SourceId, "test/syzygy-root");
        ChessSyzygy.DeriveMaterialRoot(b, chunks, ChessSyzygy.MaterialId("KQvK"));
        var change = b.SetInputUnitsConsumed(0).Build();

        var root = Assert.Single(change.Physicalities);
        Assert.Equal(chunks.Select(static c => c.Id), Trajectory.Constituents(root.TrajectoryXyzm!));
        Assert.Equal(2, root.NConstituents);
        Assert.Empty(change.Attestations);
    }

    [Fact]
    public void DeriveGame_SkipsPositions_TheTableSetDoesNotCover()
    {
        var prober = new FakeProber(3, new SyzygyVerdict(SyzygyNative.Draw, 0));
        var change = Derive(prober, FullGame);
        Assert.Empty(prober.ProbedFens);
        Assert.Empty(change.Physicalities);
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
/// ONE process-wide tablebase authority, shared with every real engine test.
///
/// The UCI tests execute full production search and therefore initialize
/// ChessTablebaseRuntime. TestModuleInit points that same authority at the repository's
/// deterministic fixture before any test runs. This fixture deliberately does not call
/// SyzygyNative.Init or Free: initializing a second directory, or freeing the mapping while
/// the runtime's Lazy still claims it is loaded, violates Fathom's process-global contract.
///
/// Production resolution remains unchanged and continues to use the configured or /vault
/// table set. Only the Laplace.Chess.Tests process receives the fixture path.
/// </summary>
public sealed class SyzygyTablebaseFixture
{
    public SyzygyTablebaseFixture()
    {
        Assert.Equal(3, ChessTablebaseRuntime.Largest);
        Assert.Equal(
            Path.GetFullPath(TestModuleInit.SyzygyFixtureDirectory),
            ChessTablebaseRuntime.LoadedTableSetForTest);
        Assert.Equal(3, SyzygyNative.Largest());
    }
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
    public void DeriveGame_NativeProber_DepositsExactTransitions()
    {
        const string pgn =
            "[Event \"T\"]\n[White \"A\"]\n[Black \"B\"]\n[Date \"2024.01.01\"]\n"
            + "[Result \"1-0\"]\n[SetUp \"1\"]\n[FEN \"4k3/8/8/8/8/8/8/3QK3 w - - 0 1\"]\n\n"
            + "1. Qd5 1-0\n";
        var parsed = ChessPgnDecomposer.TryParseGame(pgn)!;
        var b = new SubstrateChangeBuilder(ChessSyzygy.SourceId, "test/syzygy-native");
        ChessSyzygy.DeriveGame(b, ChessAnalyze.WitnessedFromParsed(parsed), new SyzygyNativeProber());
        var change = b.SetInputUnitsConsumed(1).Build();

        var chunks = change.Physicalities.Where(static p => p.NConstituents == 3).ToList();
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, p => Assert.Equal(ChessSyzygy.SourceId, p.SourceId));
    }

    /// <summary>
    /// The tripartite identity that makes tablebase verdicts reachable from games at all:
    /// for every ply N of one witnessed game,
    ///
    ///   projection[N] == ChessCompose.PositionId(StateKey(board at ply N)) == verdict subject
    ///
    /// Leg A is the Projection trajectory the trajectory lane deposits on the LINE, recovered
    /// through the packed geometry (Trajectory.Constituents) — the same bytes chess.missed_finish
    /// unpacks server-side. Leg B is an INDEPENDENT replay whose ids go through the interchange
    /// surface (StateKey → TryFenFromSurface → Board.FromFen → atoms), so the surface round-trip
    /// is part of what is being proven, not assumed. Leg C is the real Fathom probe over the
    /// repo's 3-men fixture set. Each leg is produced by the code path production uses.
    /// </summary>
    [Fact]
    public void TripartiteIdentity_ProjectionReplayAndVerdictSubjectsAgree()
    {
        const string pgn =
            "[Event \"T\"]\n[White \"A\"]\n[Black \"B\"]\n[Date \"2024.01.01\"]\n"
            + "[Result \"1-0\"]\n[SetUp \"1\"]\n[FEN \"4k3/8/8/8/8/8/8/3QK3 w - - 0 1\"]\n\n"
            + "1. Qd5 Kf8 2. Ke2 1-0\n";
        var parsed = ChessPgnDecomposer.TryParseGame(pgn)!;
        var witnessed = ChessAnalyze.WitnessedFromParsed(parsed);

        // Leg A — projection trajectory on the line, ids back out of the geometry.
        var tb = new SubstrateChangeBuilder(ChessVocabulary.TrajectorySourceId, "test/tripartite-traj");
        ChessTrajectoryDecomposer.Deposit(tb, witnessed, ChessVocabulary.TrajectorySourceId);
        var traj = Assert.Single(
            tb.SetInputUnitsConsumed(1).Build().Physicalities,
            p => p.EntityId == parsed.LineId && p.Type == PhysicalityType.Projection);
        var projectionIds = Trajectory.Constituents(traj.TrajectoryXyzm!);

        // Leg B — independent replay; every id crosses the interchange surface round-trip.
        var m = new ChessModality();
        var (state, _) = ChessAnalyze.InitialState(witnessed.StartFen, m)!.Value;
        var replayIds = new List<Hash128> { ChessCompose.PositionId(m.StateKey(state)) };
        var scratch = new List<ChessMove>(16);
        foreach (var san in witnessed.Moves)
        {
            var mv = San.Resolve(state.Board, san, scratch);
            Assert.NotNull(mv);
            state = m.Apply(state, mv!.Value);
            replayIds.Add(ChessCompose.PositionId(m.StateKey(state)));
        }

        // Leg C — pre-state vertices from exact tablebase transitions over the same game.
        var sb = new SubstrateChangeBuilder(ChessSyzygy.SourceId, "test/tripartite-syzygy");
        ChessSyzygy.DeriveGame(sb, witnessed, new SyzygyNativeProber());
        var change = sb.SetInputUnitsConsumed(1).Build();
        var verdictSubjects = change.Physicalities
            .Where(static p => p.NConstituents == 3)
            .Select(p => Trajectory.Constituents(p.TrajectoryXyzm!)[0])
            .ToArray();

        // Non-vacuity: 3 plies -> 4 positions, all 3-men and non-terminal, so every leg
        // must carry exactly 4 ids — an empty==empty pass would prove nothing.
        var expected = replayIds.ToArray();
        Assert.Equal(4, expected.Length);
        Assert.Equal(expected, projectionIds);
        Assert.Equal(expected, verdictSubjects);
    }
}
