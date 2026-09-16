using System.Text.Json;
using System.Text.Json.Serialization;
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
        const string invalid = "Invalid chess experiment receipt: expected its format, experiment id, PGN event, options, and artifact identities.";
        ReceiptEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ReceiptEnvelope>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(invalid, ex);
        }
        if (envelope is null || envelope.FormatVersion != 1
            || string.IsNullOrWhiteSpace(envelope.ExperimentId)
            || envelope.PgnEvent != "chess-lab/cutechess/" + envelope.ExperimentId
            || envelope.RequestedOptions is null || envelope.Artifacts is null)
            throw new InvalidDataException(invalid);

        // Ingestion status is transport bookkeeping. Excluding it keeps the recorded
        // experiment identical on a retry after the downloadable receipt is marked ingested.
        envelope.AdditionalProperties?.Remove("ingested");
        return new(envelope.ExperimentId, envelope.PgnEvent, JsonSerializer.Serialize(envelope));
    }

    // This is the application-owned receipt emitted by CutechessExperimentReceipt,
    // using the same typed serialization boundary as ChessPlayerModelExport. The PGN
    // corpus still goes through its grammar; no receipt fields are parsed as chess data.
    // Extension values remain opaque so recording an older envelope contract cannot
    // silently discard new execution observations or round their numeric values.
    private sealed record ReceiptEnvelope(
        [property: JsonPropertyName("formatVersion")] int FormatVersion,
        [property: JsonPropertyName("experimentId")] string? ExperimentId,
        [property: JsonPropertyName("pgnEvent")] string? PgnEvent,
        [property: JsonPropertyName("requestedOptions")] Dictionary<string, JsonElement>? RequestedOptions,
        [property: JsonPropertyName("artifacts")] Dictionary<string, JsonElement>? Artifacts)
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
    }

    public void ValidateGame(string pgn)
    {
        if (PgnGames.TagStr(pgn, "Event") != PgnEvent)
            throw new InvalidDataException("Chess experiment receipt does not match the PGN Event; refusing to attach unrelated configuration.");
    }

    public async Task<SubstrateChange> BuildChangeAsync(IReadOnlyList<ChessGameRecord> games, CancellationToken ct)
    {
        var source = SourceId;
        using var builder = new SubstrateChangeBuilder(source, PgnEvent + "/receipt")
            .DeclareSourcePrior(SourceTrust.AppDerived);
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
