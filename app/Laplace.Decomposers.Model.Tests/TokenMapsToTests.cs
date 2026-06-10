using Xunit;
using Laplace.Decomposers.Abstractions;
using Laplace.Decomposers.Model;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;

namespace Laplace.Decomposers.Model.Tests;

public class TokenMapsToTests
{
    private static readonly Hash128 Source      = Hash128.OfCanonical("substrate/test/tmt/source");
    private static readonly Hash128 TokenizerId = Hash128.OfCanonical("substrate/test/tmt/tokenizer");

    private static long RegistryPhiFp() =>
        (long)(NativeAttestation.WitnessPhi(
            RelationTypeRegistry.Resolve("TOKEN_MAPS_TO").Rank * SourceTrust.AiModelProbe) * Glicko2.FpScale);

    private static LlamaTokenizerParser.TokenRecord Rec(
        int id, string raw, bool anchored, double x = 0, double y = 0, double z = 0, double m = 1)
        => new()
        {
            TokenId = id, RawToken = raw,
            CanonicalBytes = System.Text.Encoding.UTF8.GetBytes(raw),
            EntityId = Hash128.OfCanonical($"substrate/test/tmt/token/{id}"),
            Tier = 0, IsByteLevel = false, Role = TokenRole.None,
            ContentX = anchored ? x : double.NaN, ContentY = anchored ? y : double.NaN,
            ContentZ = anchored ? z : double.NaN, ContentM = anchored ? m : double.NaN,
            HasContentCoord = anchored,
        };

    [Fact]
    public void Morph_Emits_ResidualScored_ForAnchored_And_Categorical_ForUnanchored()
    {
        var rng = new Random(42);
        int n = 14, d = 8;
        var recs = new List<LlamaTokenizerParser.TokenRecord>();
        for (int i = 0; i < 12; i++)
        {
            double ax = Math.Sin(i * 0.7), ay = Math.Cos(i * 0.7),
                   az = Math.Sin(i * 0.3 + 1), am = Math.Cos(i * 0.3 + 1);
            double nrm = Math.Sqrt(ax * ax + ay * ay + az * az + am * am);
            recs.Add(Rec(i, $"t{i}", anchored: true, ax / nrm, ay / nrm, az / nrm, am / nrm));
        }
        recs.Add(Rec(12, "<s>", anchored: false));
        {
            var bc = Laplace.Engine.Core.ByteAtoms.Coord(0xFF);
            recs.Add(new LlamaTokenizerParser.TokenRecord
            {
                TokenId = 13, RawToken = "<0xFF>",
                CanonicalBytes = new byte[] { 0xFF },
                EntityId = Laplace.Engine.Core.ByteAtoms.Id(0xFF),
                Tier = 0, IsByteLevel = true, Role = TokenRole.None,
                ContentX = bc[0], ContentY = bc[1], ContentZ = bc[2], ContentM = bc[3],
                HasContentCoord = true,
            });
        }

        var embed = new float[n * d];
        for (int i = 0; i < embed.Length; i++) embed[i] = (float)(rng.NextDouble() * 2 - 1);

        var morph = new TokenS3Morph(embed, n, d, recs, Source, TokenizerId,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var changes = morph.Emit().ToList();
        Assert.NotEmpty(changes);

        var atts = changes.SelectMany(c => c.Attestations)
            .Where(a => a.TypeId == RelationTypeRegistry.RelationTypeId("TOKEN_MAPS_TO")).ToList();
        Assert.Equal(n, atts.Count);

        long phi = RegistryPhiFp();
        Assert.All(atts, a => Assert.Equal(phi, a.OpponentRdFp1e9));

        var byObj = atts.ToDictionary(a => a.ObjectId!.Value);
        var anchoredScores = recs.Where(r => r.HasContentCoord)
            .Select(r => byObj[r.EntityId].ScoreFp1e9).ToList();
        Assert.All(anchoredScores, sc => Assert.True(sc < Glicko2.FpScale));
        Assert.True(anchoredScores.Distinct().Count() > 1,
            "anchored scores must vary with the residual");

        foreach (var r in recs.Where(r => !r.HasContentCoord))
            Assert.Equal(Glicko2.FpScale, byObj[r.EntityId].ScoreFp1e9);
    }

    [Fact]
    public void CategoricalFallback_OneRowPerToken_AtRegistryRank()
    {
        var recs = new List<LlamaTokenizerParser.TokenRecord>
        {
            Rec(0, "a", anchored: true, 1, 0, 0, 0),
            Rec(1, "b", anchored: false),
            Rec(2, "c", anchored: true, 0, 1, 0, 0),
        };
        var batches = LlamaTokenizerParser.BuildTokenMapsToCategorical(
            recs, Source, TokenizerId, batchSize: 2).ToList();
        var atts = batches.SelectMany(b => b.Attestations).ToList();

        Assert.Equal(3, atts.Count);
        long phi = RegistryPhiFp();
        Assert.All(atts, a =>
        {
            Assert.Equal(RelationTypeRegistry.RelationTypeId("TOKEN_MAPS_TO"), a.TypeId);
            Assert.Equal(TokenizerId, a.SubjectId);
            Assert.Equal(Glicko2.FpScale, a.ScoreFp1e9);
            Assert.Equal(phi, a.OpponentRdFp1e9);
        });
    }

    [Fact]
    public void VocabBatches_EmitEntitiesOnly_NoAttestations()
    {
        var recs = new List<LlamaTokenizerParser.TokenRecord>
        {
            new()
            {
                TokenId = 0, RawToken = "<s>",
                CanonicalBytes = System.Text.Encoding.UTF8.GetBytes("<s>"),
                EntityId = Hash128.OfCanonical("substrate/token/special/<s>/v1"),
                Tier = 0, IsByteLevel = false, Role = TokenRole.Special,
                ContentX = double.NaN, ContentY = double.NaN,
                ContentZ = double.NaN, ContentM = double.NaN,
                HasContentCoord = false,
            },
        };
        var batches = LlamaTokenizerParser.BuildBatches(
            recs, Source, Hash128.OfCanonical("substrate/type/Text/v1")).ToList();
        Assert.All(batches, b => Assert.Empty(b.Attestations));
        Assert.Contains(batches, b => b.Entities.Length > 0);
    }
}
