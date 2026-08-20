using System.Diagnostics;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;
using Xunit.Abstractions;

namespace Laplace.Chess.Service.Tests;

// Attribution probe for chess COMPOSE throughput. Not a gate — it prints, it does not assert
// wall-clock (a timing assertion on a shared box is a flaky test, not a measurement).
[Trait("Tier", "perf")]
public sealed class IngestThroughputProbe(ITestOutputHelper o)
{
    private const string Corpus = "/vault/Data/Games/Chess/Lumbras/otb/LumbrasGigaBase_OTB_1950-1969.pgn";

    private static List<string> Games(int n)
    {
        var games = new List<string>(n);
        var sb = new System.Text.StringBuilder();
        bool inG = false;
        foreach (var line in File.ReadLines(Corpus))
        {
            if (line.StartsWith("[Event ", StringComparison.Ordinal))
            {
                if (inG && sb.Length > 0) { games.Add(sb.ToString()); sb.Clear(); if (games.Count >= n) break; }
                inG = true;
            }
            if (inG) sb.AppendLine(line);
        }
        return games;
    }

    private double Run(IReadOnlyList<string> games, bool analyze, out long rows)
    {
        long r = 0;
        var sw = Stopwatch.StartNew();
        foreach (var g in games)
        {
            if (ChessPgnDecomposer.TryParseGame(g) is not { } parsed) continue;
            var b = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "probe");
            ChessPgnDecomposer.ComposeGame(parsed, b, analyze);
            var c = b.SetInputUnitsConsumed(1).Build();
            r += c.Entities.Length + c.Attestations.Length + c.Physicalities.Length;
        }
        sw.Stop();
        rows = r;
        return sw.Elapsed.TotalSeconds;
    }

    // What inside analyze actually costs. Position composition is the suspect: it is called
    // once per ply and does Merkle + centroid + hilbert + trajectory + physicality-id.
    [SkippableFact]
    public void WhereAnalyzeTimeGoes()
    {
        Skip.IfNot(File.Exists(Corpus), "corpus absent");
        CodepointPerfcache.LoadDefault();
        var games = Games(400);
        foreach (var g in games.Take(40)) { if (ChessPgnDecomposer.TryParseGame(g) is { } w) {
            var wb = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "warm");
            ChessPgnDecomposer.ComposeGame(w, wb, true); wb.SetInputUnitsConsumed(1).Build(); } }

        // Replay only — no compose, no rows. The pure chess cost.
        var swReplay = Stopwatch.StartNew();
        long plies = 0;
        foreach (var g in games)
        {
            if (ChessPgnDecomposer.TryParseGame(g) is not { } parsed) continue;
            var m = new Laplace.Modality.Chess.ChessModality();
            var st = m.Initial();
            foreach (var san in parsed.Moves)
            {
                var mv = Laplace.Modality.Chess.San.Resolve(st.Board, m.LegalActions(st), san);
                if (mv is null) break;
                st = m.Apply(st, mv.Value); plies++;
            }
        }
        swReplay.Stop();

        // Replay + position composition, still no attestations.
        var swCompose = Stopwatch.StartNew();
        foreach (var g in games)
        {
            if (ChessPgnDecomposer.TryParseGame(g) is not { } parsed) continue;
            var m = new Laplace.Modality.Chess.ChessModality();
            var st = m.Initial();
            lock (ChessCompose.Gate) { ChessCompose.Position(m.StateKey(st)); }
            foreach (var san in parsed.Moves)
            {
                var mv = Laplace.Modality.Chess.San.Resolve(st.Board, m.LegalActions(st), san);
                if (mv is null) break;
                st = m.Apply(st, mv.Value);
                lock (ChessCompose.Gate) { ChessCompose.Position(m.StateKey(st)); }
            }
        }
        swCompose.Stop();

        double tFull = Run(games, analyze: true, out _);
        o.WriteLine($"games {games.Count}, plies {plies}");
        o.WriteLine($"replay only          : {swReplay.Elapsed.TotalSeconds,6:F2}s  ({100*swReplay.Elapsed.TotalSeconds/tFull,5:F1}% of full)");
        o.WriteLine($"replay + compose pos : {swCompose.Elapsed.TotalSeconds,6:F2}s  ({100*swCompose.Elapsed.TotalSeconds/tFull,5:F1}% of full)");
        o.WriteLine($"full record+analyze  : {tFull,6:F2}s");
        o.WriteLine($"=> position compose  : {100*(swCompose.Elapsed.TotalSeconds-swReplay.Elapsed.TotalSeconds)/tFull,5:F1}% of full");
        o.WriteLine($"=> row building      : {100*(tFull-swCompose.Elapsed.TotalSeconds)/tFull,5:F1}% of full");
    }

    [SkippableFact]
    public void ComposeThroughput()
    {
        Skip.IfNot(File.Exists(Corpus), "corpus absent");
        CodepointPerfcache.LoadDefault();

        var warm = Games(60);
        Run(warm, true, out _);                      // warm the memos; measure steady state

        var games = Games(1200);
        double tFull = Run(games, analyze: true, out long rowsFull);
        double tRec  = Run(games, analyze: false, out long rowsRec);

        o.WriteLine($"games                : {games.Count}");
        o.WriteLine($"record+analyze       : {tFull,6:F2}s = {games.Count / tFull,7:F1} games/s  ({rowsFull / (double)games.Count,6:F0} rows/game)");
        o.WriteLine($"record only          : {tRec,6:F2}s = {games.Count / tRec,7:F1} games/s  ({rowsRec / (double)games.Count,6:F0} rows/game)");
        o.WriteLine($"analyze share of time: {100.0 * (tFull - tRec) / tFull,5:F1}%");
        o.WriteLine($"projected 190k games : {190705 / (games.Count / tFull) / 60,6:F1} min of pure compose");

        // Attribute the remaining row fanout by relation. A large population is not
        // presumed necessary merely because compose can produce it quickly.
        var byType = new Dictionary<Hash128, long>();
        long ents = 0, phys = 0;
        foreach (var g in games.Take(200))
        {
            if (ChessPgnDecomposer.TryParseGame(g) is not { } parsed) continue;
            var b = new SubstrateChangeBuilder(ChessVocabulary.PgnSourceId, "probe");
            ChessPgnDecomposer.ComposeGame(parsed, b, analyzeInline: true);
            var c = b.SetInputUnitsConsumed(1).Build();
            ents += c.Entities.Length; phys += c.Physicalities.Length;
            foreach (var a in c.Attestations)
                byType[a.TypeId] = byType.GetValueOrDefault(a.TypeId) + 1;
        }
        o.WriteLine("");
        o.WriteLine($"per game: entities {ents / 200.0:F0}, physicalities {phys / 200.0:F0}, attestations {byType.Values.Sum() / 200.0:F0}");
        foreach (var kv in byType.OrderByDescending(k => k.Value).Take(8))
        {
            string name =
                kv.Key == ChessVocabulary.OutcomeType ? "OUTCOME" :
                kv.Key == ChessVocabulary.MoveType ? "MOVE" :
                kv.Key == RelationTypeRegistry.RelationTypeId("HAS_SAN") ? "HAS_SAN" :
                kv.Key == RelationTypeRegistry.RelationTypeId("HAS_EVAL") ? "HAS_EVAL" :
                kv.Key == RelationTypeRegistry.RelationTypeId("PLAYED_BY") ? "PLAYED_BY" :
                kv.Key == RelationTypeRegistry.RelationTypeId("CONTAINS") ? "CONTAINS" :
                kv.Key.ToString();
            o.WriteLine($"  {name,-22} {kv.Value / 200.0,7:F0} /game");
        }
    }
}
