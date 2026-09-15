using System.Text;
using System.Text.Json;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Chess.Service;

/// <summary>Recorded experiment metadata, distinct from PGN testimony and calculated chess evaluations.</summary>
internal sealed record ChessExperimentEvidence(string ExperimentId, string PgnEvent, string ReceiptJson)
{
    public const string SourceName = "ChessGauntlet";
    public static Hash128 SourceId => Canonicals.SourceId;
    public static Hash128 TrustClassId => Canonicals.TrustClassId;
    internal static Hash128 ReceiptMetaTypeId => Canonicals.ReceiptMetaTypeId;

    // Keep pure receipt validation independent from native canonical-id initialization.
    private static class Canonicals
    {
        internal static readonly Hash128 SourceId = SubstrateCanonicalIds.Source(SourceName);
        internal static readonly Hash128 TrustClassId = SubstrateCanonicalIds.TrustClass("AppDerived");
        internal static readonly Hash128 ReceiptMetaTypeId = SubstrateCanonicalIds.OfVersioned("type", "HasExperimentReceipt");
    }

    public static ChessExperimentEvidence Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("formatVersion", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int format) || format != 1
            || !root.TryGetProperty("experimentId", out var id) || id.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(id.GetString())
            || !root.TryGetProperty("pgnEvent", out var pgnEvent) || pgnEvent.ValueKind != JsonValueKind.String
            || pgnEvent.GetString() != "chess-lab/cutechess/" + id.GetString()
            || !root.TryGetProperty("requestedOptions", out var options) || options.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid chess experiment receipt: expected its format, experiment id, PGN event, options, and artifact identities.");

        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                // Ingestion status is transport bookkeeping. Excluding it keeps the recorded
                // experiment identical on a retry after the downloadable receipt is marked ingested.
                if (property.NameEquals("ingested")) continue;
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return new(id.GetString()!, pgnEvent.GetString()!, Encoding.UTF8.GetString(output.ToArray()));
    }

    public void ValidateGame(string pgn)
    {
        if (PgnGames.TagStr(pgn, "Event") != PgnEvent)
            throw new InvalidDataException("Chess experiment receipt does not match the PGN Event; refusing to attach unrelated configuration.");
    }

    public async Task<SubstrateChange> BuildChangeAsync(IReadOnlyList<ChessGameRecord> games, CancellationToken ct)
    {
        var source = SourceId;
        var builder = new SubstrateChangeBuilder(source, PgnEvent + "/receipt");
        var receiptId = ContentEmitter.Emit(builder, ReceiptJson, source)
            ?? throw new InvalidOperationException("Could not admit chess experiment receipt content.");
        var contextId = ContentEmitter.Emit(builder, PgnEvent, source)
            ?? throw new InvalidOperationException("Could not admit chess experiment context.");
        var type = ReceiptMetaTypeId;
        builder.AddEntity(type, EntityTier.Word, BootstrapIntentBuilder.RelationTypeMetaTypeId, source);
        foreach (var playing in games.Select(game => game.PlayingId).Distinct())
            builder.AddAttestation(NativeAttestation.CategoricalResolved(
                playing, type, receiptId, source, contextId, SourceTrust.AppDerived));
        return (await builder.BuildAsync(ct)) with { CountsAsUnit = false };
    }
}
