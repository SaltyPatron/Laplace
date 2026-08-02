using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.Modality.Chess;
using Laplace.SubstrateCRUD;
using Xunit;

namespace Laplace.Chess.Service.Tests;

[Trait("Tier", "fast")]
public sealed class ChessSyzygyTests
{
    // KQvK from a SetUp game: every position is 3 men (no captures, no promotion).
    private const string EndgameGame =
        "[Event \"T\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Date \"2024.01.01\"]\n"
        + "[Result \"1-0\"]\n[SetUp \"1\"]\n[FEN \"4k3/8/8/8/8/8/8/3QK3 w - - 0 1\"]\n\n"
        + "1. Qd5 Kf8 2. Ke2 1-0\n";

    // Scholar's mate: 32 men throughout, ends in checkmate, castle rights everywhere.
    private const string FullGame =
        "[Event \"T\"]\n[White \"Alice\"]\n[Black \"Bob\"]\n[Date \"2024.01.01\"]\n[Result \"1-0\"]\n\n"
        + "1. e4 e5 2. Qh5 Nc6 3. Bc4 Nf6 4. Qxf7# 1-0\n";

    /// <summary>Answers every probed board with a scripted verdict; records probed FENs.</summary>
    private sealed class FakeProber(int largest, SyzygyVerdict? verdict) : ISyzygyProber
    {
        public List<string> ProbedFens { get; } = [];
        public int Largest => largest;
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

    [Fact]
    public void ToBitboards_KQvK_PlacesEveryPiece()
    {
        var bb = ChessSyzygy.ToBitboards(Board.FromFen("4k3/8/8/8/8/8/8/3QK3 w - - 0 1"));
        Assert.Equal(1UL << 3 | 1UL << 4, bb.White);   // Qd1, Ke1
        Assert.Equal(1UL << 60, bb.Black);             // Ke8
        Assert.Equal(1UL << 4 | 1UL << 60, bb.Kings);
        Assert.Equal(1UL << 3, bb.Queens);
        Assert.Equal(0UL, bb.Rooks);
        Assert.Equal(0UL, bb.Bishops);
        Assert.Equal(0UL, bb.Knights);
        Assert.Equal(0UL, bb.Pawns);
        Assert.Equal(0u, bb.Ep);
    }

    [Fact]
    public void ToBitboards_Ep_OnlyWhenCapturable()
    {
        // Black just played c7-c5; the white b5 pawn can capture en passant -> c6 = 42.
        var capturable = ChessSyzygy.ToBitboards(
            Board.FromFen("4k3/8/8/1Pp5/8/8/8/4K3 w - c6 0 1"));
        Assert.Equal(42u, capturable.Ep);

        // Same double push with no adjacent white pawn: the raw ep square is not a
        // position fact (identity law parity) and never reaches the probe.
        var idle = ChessSyzygy.ToBitboards(
            Board.FromFen("4k3/8/8/2p5/8/8/8/4K3 w - c6 0 1"));
        Assert.Equal(0u, idle.Ep);
    }

    [Fact]
    public void MenCount_CountsBothSides()
    {
        Assert.Equal(3, ChessSyzygy.MenCount(Board.FromFen("4k3/8/8/8/8/8/8/3QK3 w - - 0 1")));
        Assert.Equal(32, ChessSyzygy.MenCount(Board.FromFen(ChessModality.StartFen)));
    }

    [Fact]
    public void DeriveGame_ProbesEveryCoveredPosition_AndDepositsBothFacts()
    {
        var prober = new FakeProber(3, new SyzygyVerdict(SyzygyNative.Win, 12));
        var change = Derive(prober, EndgameGame);

        // 4 positions in the 3-ply game, all 3-men, none terminal, no castle rights.
        Assert.Equal(4, prober.ProbedFens.Count);

        var wdl = change.Attestations.Where(a => a.TypeId == ChessVocabulary.HasWdlType).ToList();
        var dtz = change.Attestations.Where(a => a.TypeId == ChessVocabulary.HasDtzType).ToList();
        Assert.Equal(4, wdl.Count);
        Assert.Equal(4, dtz.Count);

        var parsed = ChessPgnDecomposer.TryParseGame(EndgameGame)!;
        Assert.All(wdl.Concat(dtz), a =>
        {
            Assert.Equal(ChessSyzygy.SourceId, a.SourceId);
            // ctx = the LINE (verdicts are line-grain testimony, stockfish-eval parity).
            Assert.Equal(parsed.LineId, a.ContextId);
        });
        Assert.All(wdl, a => Assert.Equal(ContentEmitter.RootId("win"), a.ObjectId));
        Assert.All(dtz, a => Assert.Equal(ContentEmitter.RootId("12"), a.ObjectId));
    }

    [Fact]
    public void DeriveGame_SkipsPositions_TheTableSetDoesNotCover()
    {
        // 32 men everywhere (and castle rights): a 3-men table set covers nothing.
        var prober = new FakeProber(3, new SyzygyVerdict(SyzygyNative.Draw, 0));
        var change = Derive(prober, FullGame);
        Assert.Empty(prober.ProbedFens);
        Assert.DoesNotContain(change.Attestations, a => a.TypeId == ChessVocabulary.HasWdlType);
        Assert.DoesNotContain(change.Attestations, a => a.TypeId == ChessVocabulary.HasDtzType);
    }

    [Fact]
    public void DeriveGame_SkipsCastleRightPositions_EvenWhenSmallEnough()
    {
        // 4 men, but white retains K-side castle rights -> outside every tablebase's domain.
        const string castled =
            "[Event \"T\"]\n[White \"A\"]\n[Black \"B\"]\n[Date \"2024.01.01\"]\n"
            + "[Result \"1/2-1/2\"]\n[SetUp \"1\"]\n[FEN \"4k3/8/8/8/8/8/8/4K2R w K - 0 1\"]\n\n"
            + "1. O-O 1/2-1/2\n";
        var prober = new FakeProber(6, new SyzygyVerdict(SyzygyNative.Win, 4));
        Derive(prober, castled);
        // Only the post-castling position (rights spent) is probed.
        Assert.Single(prober.ProbedFens);
        Assert.Contains(" - ", prober.ProbedFens[0]);
    }

    [Fact]
    public void DeriveGame_SkipsTerminalPositions()
    {
        // Every position covered (largest=32), but the final checkmate needs no oracle.
        var prober = new FakeProber(32, null);
        Derive(prober, FullGame);
        // 8 positions; 7 probed (mate skipped); castle rights persist... on this line no
        // side ever castles or moves king/rook, so rights hold from ply 0 -> nothing
        // probed after all. Use the count of castle-right-free positions instead: none.
        // The start position carries KQkq, so the terminal skip is only observable on a
        // rights-free game — assert through the endgame line instead.
        Assert.Empty(prober.ProbedFens);

        const string mateNoRights =
            "[Event \"T\"]\n[White \"A\"]\n[Black \"B\"]\n[Date \"2024.01.01\"]\n"
            + "[Result \"1-0\"]\n[SetUp \"1\"]\n[FEN \"6k1/8/6K1/8/8/8/1Q6/8 w - - 0 1\"]\n\n"
            + "1. Qb8# 1-0\n";
        var prober2 = new FakeProber(32, null);
        Derive(prober2, mateNoRights);
        Assert.Single(prober2.ProbedFens); // start probed; the mate itself skipped
    }

    [Fact]
    public void DeriveGame_NullVerdicts_DepositNothing_ButStampTheMarker()
    {
        var prober = new FakeProber(3, null);
        var change = Derive(prober, EndgameGame);
        Assert.Equal(4, prober.ProbedFens.Count);
        Assert.DoesNotContain(change.Attestations, a => a.TypeId == ChessVocabulary.HasWdlType);
        Assert.DoesNotContain(change.Attestations, a => a.TypeId == ChessVocabulary.HasDtzType);

        var parsed = ChessPgnDecomposer.TryParseGame(EndgameGame)!;
        var marker = ChessSyzygy.MarkerId(parsed.LineId, ChessSyzygy.Version);
        Assert.Contains(change.Entities, e => e.Id == marker);
        Assert.Contains(change.Attestations, a =>
            a.TypeId == ChessVocabulary.AnalyzedAtType && a.SubjectId == parsed.LineId
            && a.SourceId == ChessSyzygy.SourceId);
    }

    [Fact]
    public void Record_TrunkRoot_IsTheVersionedLineMarker()
    {
        var parsed = ChessPgnDecomposer.TryParseGame(EndgameGame)!;
        var record = new ChessSyzygyRecord(ChessAnalyze.WitnessedFromParsed(parsed));
        Assert.Equal(ChessSyzygy.MarkerId(parsed.LineId, ChessSyzygy.Version), record.TrunkRootId);
    }

    [Fact]
    public void DeriveGame_EmitsOnlyDeclaredRelations()
    {
        // Same gate as ChessRelationGateTests, over the syzygy lane's emissions.
        var declared = ChessSeedManifest.Relations
            .Select(RelationTypeRegistry.RelationTypeId).ToHashSet();
        var change = Derive(new FakeProber(3, new SyzygyVerdict(SyzygyNative.Win, 8)), EndgameGame);
        var undeclared = change.Attestations
            .Select(a => a.TypeId).Distinct().Where(t => !declared.Contains(t)).ToList();
        Assert.Empty(undeclared);
    }

    /// <summary>
    /// No tablebases ANYWHERE is the clean no-op. Both lookups have to be neutralised:
    /// <c>SyzygyDir</c> now falls back to the corpus layout under LAPLACE_DATA_ROOT,
    /// because the tables were downloaded to
    /// <c>/vault/Data/Games/Chess/syzygy/3-4-5</c> and nobody exported LAPLACE_SYZYGY,
    /// so this lane no-op'd on a host that had them. Pointing only the env at a missing
    /// directory no longer proves "no tablebases" — it proves the env is unset, which is
    /// a different statement and the one that used to silently pass here.
    /// </summary>
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

    /// <summary>
    /// The corpus-layout fallback: tables under
    /// <c>&lt;LAPLACE_DATA_ROOT&gt;/Games/Chess/syzygy/3-4-5</c> are found with no env set.
    /// A directory with no <c>.rtbw</c> is NOT a tablebase directory — reporting it as one
    /// would surface as "0 tables discovered" and read like operator misconfiguration.
    /// </summary>
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
            Assert.False(ChessLabPaths.SyzygyDir.Found); // directory exists, holds no tables

            File.WriteAllBytes(Path.Combine(tables, "KQvK.rtbw"), [0]);
            var probe = ChessLabPaths.SyzygyDir;
            Assert.True(probe.Found);
            Assert.Equal(tables, probe.Path);
            Assert.Equal("data-root", probe.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_SYZYGY", priorEnv);
            Environment.SetEnvironmentVariable("LAPLACE_DATA_ROOT", priorRoot);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void SyzygyDir_ReadsEnv()
    {
        var prior = Environment.GetEnvironmentVariable("LAPLACE_SYZYGY");
        var dir = Path.Combine(Path.GetTempPath(), $"syzygy-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("LAPLACE_SYZYGY", dir);
        try
        {
            var probe = ChessLabPaths.SyzygyDir;
            Assert.True(probe.Found);
            Assert.Equal(dir, probe.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAPLACE_SYZYGY", prior);
            Directory.Delete(dir);
        }
    }
}

/// <summary>
/// Native-kernel integration: the P/Invoke layer against the committed KQvK/KRvK
/// fixture tables (test-data/syzygy — see its README for origin, checksums, license
/// note). One class = one xunit collection, so init/free never race the probes.
/// The native ctest (engine/core/tests/test_syzygy.cpp) covers the kernel itself;
/// this covers the managed boundary and the real prober.
/// </summary>
[Trait("Tier", "fast")]
public sealed class SyzygyNativeFixtureTests : IDisposable
{
    private static string FixtureDir()
    {
        Assert.True(LaplaceInstall.TryRepoRoot(out var root), "repo root not resolvable");
        var dir = Path.Combine(root, "test-data", "syzygy");
        Assert.True(Directory.Exists(dir), $"fixture dir missing: {dir}");
        return dir;
    }

    public SyzygyNativeFixtureTests()
    {
        Assert.Equal(3, SyzygyNative.Init(FixtureDir()));
        Assert.Equal(3, SyzygyNative.Largest());
    }

    public void Dispose() => SyzygyNative.Free();

    [Theory]
    [InlineData("4k3/8/8/8/8/8/8/3QK3 w - - 0 1", SyzygyNative.Win)]   // KQvK, stm mates
    [InlineData("4k3/8/8/8/8/8/8/3QK3 b - - 0 1", SyzygyNative.Loss)]  // same table, black POV
    [InlineData("4k3/8/8/8/8/8/8/R3K3 w - - 0 1", SyzygyNative.Win)]   // KRvK
    [InlineData("8/8/8/8/1k6/1R6/8/K7 b - - 0 1", SyzygyNative.Draw)]  // hanging rook falls
    public void NativeProber_HandPickedFens_MatchKnownWdl(string fen, int expectedWdl)
    {
        var board = Board.FromFen(fen);
        var verdict = new SyzygyNativeProber().Probe(board);
        Assert.NotNull(verdict);
        Assert.Equal(expectedWdl, verdict.Value.Wdl);
        Assert.InRange(verdict.Value.Dtz, 0, 50);
    }

    [Fact]
    public void NativeProber_MoreMenThanTables_YieldsNoVerdict()
    {
        // KQvKR is 4 men; only 3-men fixtures are loaded.
        var verdict = new SyzygyNativeProber().Probe(
            Board.FromFen("4k3/4r3/8/8/8/8/8/3QK3 w - - 0 1"));
        Assert.Null(verdict);
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
        Assert.Equal(2, wdl.Count); // both positions white-winning (KQvK, either POV)
        // Ply 0: white to move, WIN; ply 1: black to move, LOSS — both STM POV.
        Assert.Contains(wdl, a => a.ObjectId == ContentEmitter.RootId("win"));
        Assert.Contains(wdl, a => a.ObjectId == ContentEmitter.RootId("loss"));
        Assert.Equal(2, change.Attestations.Count(a => a.TypeId == ChessVocabulary.HasDtzType));
    }
}
