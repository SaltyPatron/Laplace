using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Laplace.Chess.Service;

/// <summary>Cold, read-only verification of one PGN written by the official GUI.</summary>
public static class ChessGuiGameEvidence
{
    public const string Mode = "verify-gui-game";
    public const int MaximumPgnBytes = 16 * 1024 * 1024;
    public sealed record Evidence(
        string Schema, string Status, string Result, string Termination, int Plies,
        string White, string Black, string TimeControl, string LineId,
        string StartPositionId, string FinalPositionId, string[] MovesUci,
        bool CompleteSourceVerified, bool BoardTerminalVerified, bool DatabaseRecordingProven);

    internal static Evidence Verify(string text, string expectedWhite, string expectedBlack,
        string expectedTimeControl)
    {
        string termination = PgnGames.TagStr(text, "Termination");
        if (termination is not ("" or "time forfeit"))
            throw new InvalidDataException("GUI game ended by an unsupported or incomplete termination");
        if (PgnGames.TagStr(text, "White") != expectedWhite
            || PgnGames.TagStr(text, "Black") != expectedBlack
            || PgnGames.TagStr(text, "TimeControl") != expectedTimeControl
            || PgnGames.TagStr(text, "WhiteTimeControl").Length != 0
            || PgnGames.TagStr(text, "BlackTimeControl").Length != 0)
            throw new InvalidDataException("GUI PGN does not match the selected engine pair and clock");
        if (PgnGames.TagStr(text, "SetUp").Length != 0
            || PgnGames.TagStr(text, "FEN").Length != 0
            || PgnGames.TagStr(text, "Variant") is not ("" or "standard"))
            throw new InvalidDataException("GUI acceptance requires the orthodox initial board");

        // The established owner parses the complete native grammar and replays all
        // legal SAN. Clock losses are reported separately from board terminal proof.
        bool boardTerminal = termination.Length == 0;
        var game = ChessPgnDecomposer.TryParseGame(text,
            requireNormalCompletion: boardTerminal, requireCompleteSource: true)
            ?? throw new InvalidDataException("GUI PGN has no complete legal game");
        if (game.Moves.Count < 2 || !game.InitialWhiteToMove.GetValueOrDefault()
            || game.ResolvedMoves.Length != game.Moves.Count
            || game.PositionIds.Length != game.Moves.Count + 1)
            throw new InvalidDataException("both selected GUI engines must have played legal moves");
        return new("laplace.cutechess-gui-pgn/v1", "passed", game.Result.ResultToken,
            termination, game.Moves.Count, expectedWhite, expectedBlack, expectedTimeControl,
            ChessCorpusPreparation.Id(game.LineId),
            ChessCorpusPreparation.Id(game.PositionIds[0]),
            ChessCorpusPreparation.Id(game.PositionIds[^1]),
            game.ResolvedMoves.Select(move => move.ToUci()).ToArray(),
            game.CompleteSourceVerified, boardTerminal, false);
    }

    private static byte[] ReadBounded(string path)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long length = source.Length;
        if (length > MaximumPgnBytes) throw new InvalidDataException("PGN exceeds its byte envelope");
        byte[] bytes = new byte[checked((int)length)];
        source.ReadExactly(bytes);
        if (source.ReadByte() != -1) throw new InvalidDataException("PGN changed while reading");
        return bytes;
    }

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length != 5)
                throw new ArgumentException("verify-gui-game PGN WHITE BLACK TIME-CONTROL OUTPUT");
            string input = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[4]);
            if (File.Exists(output) || !Directory.Exists(Path.GetDirectoryName(output))
                || !File.Exists(input) || new FileInfo(input).Length > MaximumPgnBytes)
                throw new InvalidDataException("PGN/output path or byte envelope is invalid");
            byte[] bytes = ReadBounded(input);
            if (bytes.Length > MaximumPgnBytes)
                throw new InvalidDataException("PGN grew beyond its byte envelope");
            var evidence = Verify(new UTF8Encoding(false, true).GetString(bytes),
                args[1], args[2], args[3]);
            byte[] after = ReadBounded(input);
            if (!bytes.AsSpan().SequenceEqual(after))
                throw new InvalidDataException("GUI PGN changed during native verification");
            var value = new
            {
                evidence,
                input = new { path = input, bytes = bytes.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() },
                scope = "one whole GUI-written PGN; no database admission or recorded-rate claim"
            };
            using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(target, value, new JsonSerializerOptions { WriteIndented = true });
            target.Flush(true);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("GUI PGN verification failed: " + error.GetType().Name);
            return 1;
        }
    }
}
