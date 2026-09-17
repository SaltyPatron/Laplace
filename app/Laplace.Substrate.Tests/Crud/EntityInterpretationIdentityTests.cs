using System.Collections.Immutable;
using System.Text;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Laplace.SubstrateCRUD.Npgsql;
using Xunit;

namespace Laplace.Substrate.Tests.Crud;

public sealed class EntityInterpretationIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoricalObservationBytesRemainUnchanged(bool populated)
    {
        var source = Hash128.OfCanonical("interpretation-identity/source");
        const string unit = "interpretation-identity/unit";
        var id = Hash128.OfCanonical("interpretation-identity/content");
        var type = Hash128.OfCanonical("interpretation-identity/type");
        var extra = Hash128.OfCanonical("interpretation-identity/extra-type");
        var aid = Hash128.OfCanonical("interpretation-identity/attestation");
        var pid = PhysicalityId.Compute(id, PhysicalityType.Content);
        using var builder = new SubstrateChangeBuilder(source, unit);
        if (populated)
        {
            builder.AddEntity(id, 3, type, source);
            builder.AddPhysicality(new PhysicalityRow(pid, id, source, PhysicalityType.Content,
                1, 0, 0, 0, default, null, 0, null, null, 1_000_000));
            builder.AddAttestation(new AttestationRow(aid, id, type, null, source, null,
                AttestationOutcome.Confirm, 1_000_000, 1, 1_000_000_000, 350_000_000_000));
        }
        var before = builder.Build();
        // Frozen pre-interpretation wire contract: source + UTF8 unit, followed
        // by the four E/P/A/ephemeral counts and only their established ID bodies.
        byte[] name = Encoding.UTF8.GetBytes(unit);
        var historical = new byte[16 + name.Length + 16 + (populated ? 48 : 0)];
        source.WriteBytes(historical);
        name.CopyTo(historical, 16);
        int offset = 16 + name.Length;
        foreach (var value in populated ? new[] { id, pid, aid } : Array.Empty<Hash128>())
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(historical.AsSpan(offset, 4), 1);
            offset += 4;
            value.WriteBytes(historical.AsSpan(offset, 16));
            offset += 16;
        }
        Assert.Equal(Hash128.Blake3(historical), before.Metadata.IntentId);
        if (populated)
        {
            builder.AddEntity(id, 7, extra, source);
            var after = builder.Build();
            Assert.Equal(before.Metadata.IntentId, after.Metadata.IntentId);
            Assert.Equal(before.Physicalities.ToArray(), after.Physicalities.ToArray());
            Assert.Equal(before.Attestations.ToArray(), after.Attestations.ToArray());
            Assert.Equal(2, after.EntityInterpretations.Length);
        }
    }

    [Fact]
    public void AdmissionReceiptBindsCanonicalFacetPayloadWithoutChangingObservationIdentity()
    {
        var semantic = Hash128.OfCanonical("interpretation-identity/semantic");
        var id = Hash128.OfCanonical("interpretation-identity/content");
        var a = Hash128.OfCanonical("interpretation-identity/type-a");
        var b = Hash128.OfCanonical("interpretation-identity/type-b");
        var source = Hash128.OfCanonical("interpretation-identity/source");
        EntityInterpretationRow first = new(id, 3, a, source);
        EntityInterpretationRow second = new(id, 7, b, null);
        var one = NpgsqlSubstrateWriter.InterpretationReplayToken(semantic, [first]);
        var both = NpgsqlSubstrateWriter.InterpretationReplayToken(semantic, [first, second]);
        Assert.NotEqual(semantic, NpgsqlSubstrateWriter.InterpretationReplayToken(semantic, []));
        Assert.NotEqual(one, both);
        Assert.Equal(both,
            NpgsqlSubstrateWriter.InterpretationReplayToken(semantic, [second, first, second]));
        Assert.NotEqual(both,
            NpgsqlSubstrateWriter.InterpretationReplayToken(semantic, [first, second with { FirstObservedBy = source }]));
    }
    [Fact]
    public void RepeatedFacetKeepsTheSameMinimumSourceMetadataInEitherArrivalOrder()
    {
        var owner = Hash128.OfCanonical("interpretation-identity/owner");
        var id = Hash128.OfCanonical("interpretation-identity/content");
        var type = Hash128.OfCanonical("interpretation-identity/type");
        var a = Hash128.OfCanonical("interpretation-identity/source-a");
        var b = Hash128.OfCanonical("interpretation-identity/source-b");
        using var left = new SubstrateChangeBuilder(owner, "same-unit");
        left.AddEntity(id, 3, type, a).AddEntity(id, 3, type, null).AddEntity(id, 3, type, b);
        using var right = new SubstrateChangeBuilder(owner, "same-unit");
        right.AddEntity(id, 3, type, b).AddEntity(id, 3, type, a).AddEntity(id, 3, type, null);
        var l = left.Build();
        var r = right.Build();
        var minimum = a.CompareToBytewise(b) < 0 ? a : b;
        Assert.Equal(minimum, Assert.Single(l.EntityInterpretations).FirstObservedBy);
        Assert.Equal(l.EntityInterpretations.ToArray(), r.EntityInterpretations.ToArray());
        Assert.Equal(l.Metadata.IntentId, r.Metadata.IntentId);
        Assert.Equal(NpgsqlSubstrateWriter.InterpretationReplayToken(owner, l.EntityInterpretations),
            NpgsqlSubstrateWriter.InterpretationReplayToken(owner, r.EntityInterpretations));
    }

    [Fact]
    public void CompleteNativeFacetsReplaceLegacyEntityProjectionInAdmissionAndReceipt()
    {
        var id = Hash128.OfCanonical("interpretation-native/content");
        var oldType = Hash128.OfCanonical("interpretation-native/old-type");
        var actualType = Hash128.OfCanonical("interpretation-native/actual-type");
        using var stage = IntentStage.New(1);
        stage.AddEntity(id,3,oldType,null);
        var digest = stage.SemanticDigest();
        var before = NpgsqlSubstrateWriter.CollectEntityInterpretations([stage],[]);
        using var actual = IntentStage.New(1);
        actual.AddEntity(id,7,actualType,null);
        stage.ImportEntityInterpretations(actual.EmitEntityInterpretationTuples());
        var observed = NpgsqlSubstrateWriter.CollectEntityInterpretations([stage],[]);
        var facet = Assert.Single(observed);
        Assert.Equal(new EntityInterpretationRow(id,7,actualType,null),facet);
        Assert.Equal(digest,stage.SemanticDigest());
        Assert.NotEqual(NpgsqlSubstrateWriter.InterpretationReplayToken(digest,before),
            NpgsqlSubstrateWriter.InterpretationReplayToken(digest,observed));

        byte[] stream = stage.EmitCopyBinary(IntentStageTable.Entities);
        using var legacy = IntentStage.FromTupleBytes(stream.AsSpan(19,stream.Length-21),[],[],1_048_576);
        Assert.False(legacy.EntityInterpretationsComplete);
        Assert.Equal(new EntityInterpretationRow(id,3,oldType,null),
            Assert.Single(NpgsqlSubstrateWriter.CollectEntityInterpretations([legacy],[])));
        legacy.ImportEntityInterpretations([]);
        var missing = Assert.Throws<InvalidOperationException>(() =>
            NpgsqlSubstrateWriter.CollectEntityInterpretations([legacy],[]));
        Assert.Contains("omits a staged canonical entity",missing.Message);
        Assert.Equal(digest,legacy.SemanticDigest());
    }

}
