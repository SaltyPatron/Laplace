using Microsoft.Extensions.Logging;
using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model;


public readonly record struct CircuitPair(Hash128 Subject, Hash128 Object, long ScoreFp);


public sealed record CircuitDescriptor(int Layer, int Head, string Plane, string RelationName);









public sealed class HeadClassifier
{
    public static readonly Hash128 EncodesTypeId = RelationTypeRegistry.Resolve("ENCODES").Id;

    public static readonly Hash128 ModelCircuitTypeId = EntityTypeRegistry.Id("Model_Circuit");

    private readonly ISubstrateReader _reader;
    private readonly Hash128 _source;
    private readonly string _modelName;
    private readonly ILogger _log;

    public HeadClassifier(ISubstrateReader reader, Hash128 source, string modelName, ILogger? log = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _source = source;
        _modelName = modelName;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public Hash128 CircuitEntityId(CircuitDescriptor c)
    {
        string layer = c.Layer >= 0 ? $"L{c.Layer}" : "embed";
        string head = c.Head >= 0 ? $".H{c.Head}" : "";
        return Hash128.OfCanonical($"substrate/entity/{_modelName}/circuit/{layer}{head}.{c.Plane}/v1");
    }

    public readonly record struct CircuitClassifyRecord(
        CircuitDescriptor Descriptor, Hash128 CircuitId, Hash128 WinnerTypeId, double WitnessWeight);

    public async Task<CircuitClassifyRecord?> TryClassifyRecordAsync(
        CircuitDescriptor descriptor, IReadOnlyList<CircuitPair> topPairs,
        CancellationToken ct = default)
    {
        if (topPairs.Count == 0) return null;

        var query = new (Hash128, Hash128)[topPairs.Count];
        for (int i = 0; i < topPairs.Count; i++) query[i] = (topPairs[i].Subject, topPairs[i].Object);

        IReadOnlyList<CircuitRelation> hits;
        try
        {
            hits = await _reader.ClassifyCircuitAsync(query, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning("decoder-ring: classify_circuit failed for {Plane} L{L}H{H}: {Msg}",
                descriptor.Plane, descriptor.Layer, descriptor.Head, ex.Message);
            return null;
        }
        if (hits.Count == 0) return null;

        var votes = new Dictionary<Hash128, double>();
        double total = 0;
        foreach (var h in hits)
        {
            double w = h.EffMu > 0 ? h.EffMu : 0.0;
            if (w <= 0) continue;
            votes[h.TypeId] = votes.GetValueOrDefault(h.TypeId) + w;
            total += w;
        }
        if (votes.Count == 0 || total <= 0) return null;

        Hash128 winner = default; double best = 0;
        foreach (var (typeId, w) in votes)
            if (w > best) { best = w; winner = typeId; }

        double dominance = best / total;
        double witnessWeight = SourceTrust.AiModelProbe * dominance;
        var circuit = CircuitEntityId(descriptor);

        _log.LogInformation("decoder-ring: {Plane} L{L}H{H} ENCODES (dominance {Dom:P0}, {N} seed hits)",
            descriptor.Plane, descriptor.Layer, descriptor.Head, dominance, hits.Count);
        return new CircuitClassifyRecord(descriptor, circuit, winner, witnessWeight);
    }

    public static void StageClassifyRecord(
        SubstrateChangeBuilder b, CircuitClassifyRecord rec, Hash128 sourceId)
    {
        b.AddEntity(rec.CircuitId, EntityTier.Word, ModelCircuitTypeId, firstObservedBy: sourceId);
        b.AddAttestation(NativeAttestation.CategoricalResolved(
            rec.CircuitId, EncodesTypeId, rec.WinnerTypeId, sourceId, null, rec.WitnessWeight));
    }
}
