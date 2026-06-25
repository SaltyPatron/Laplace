using Laplace.Decomposers.Abstractions;
using Laplace.Engine.Core;
using Laplace.SubstrateCRUD;
using Xunit;



namespace Laplace.Decomposers.Abstractions.Tests;



public class NativeAttestationParityTests

{

    private static Hash128 H(string s) => Hash128.OfCanonical(s);



    [Fact]
    public void RelationResolve_HasUposAliasMatchesHasPos()
    {
        var upos = RelationTypeRegistry.Resolve("HAS_UPOS");
        var pos  = RelationTypeRegistry.Resolve("HAS_POS");
        Assert.Equal(pos.Id, upos.Id);
        Assert.Equal("HAS_POS", upos.Canonical);
    }

    [Fact]
    public void RelationResolve_FlipHypernymMatchesIsA()
    {
        var hyper = RelationTypeRegistry.Resolve("HAS_HYPONYM");
        var isa   = RelationTypeRegistry.Resolve("IS_A");
        Assert.Equal(isa.Id, hyper.Id);
        Assert.True(hyper.Flip);
    }

    [Fact]
    public void PosAttest_MatchesCanonicalResolve_AndAliasCollapses()
    {
        Hash128 form = Hash128.OfCanonical("substrate/test/word/v1");
        Hash128 src  = Hash128.OfCanonical("substrate/test/src/v1");

        var b = new SubstrateChangeBuilder(src, "test/pos-attest", null);
        PosReference.Attest(b, form, "NOUN", PosReference.PosTagset.Upos, src, null, SourceTrust.AcademicCurated);
        var change = b.Build();

        
        
        Assert.Empty(change.Entities);
        var att = Assert.Single(change.Attestations);
        var expected = NativeAttestation.Categorical(
            form, "HAS_UPOS", PosReference.CanonicalId("NOUN"), src, null, SourceTrust.AcademicCurated);
        Assert.Equal(expected.Id, att.Id);
        Assert.Equal(expected.TypeId, att.TypeId);
    }

    [Fact]
    public void PosAttest_ProbationaryTag_EmitsThePosEntityInBatch()
    {
        Hash128 form = Hash128.OfCanonical("substrate/test/word/v1");
        Hash128 src  = Hash128.OfCanonical("substrate/test/src/v1");

        var b = new SubstrateChangeBuilder(src, "test/pos-probationary", null);
        var posId = PosReference.Attest(b, form, "IDIO", PosReference.PosTagset.FrameNet,
            src, null, SourceTrust.AcademicCurated);
        var change = b.Build();

        
        
        Assert.Equal(Hash128.OfCanonical("substrate/pos/probationary/framenet/IDIO/v1"), posId);
        // The probationary POS entity is emitted, plus its substrate-native HAS_NAME_ALIAS name (a
        // content-walk of the tag), so assert the POS entity + the form→HAS_POS edge are present rather
        // than that they are the only rows in the batch.
        Assert.Contains(change.Entities, e => e.Id == posId && e.TypeId == PosReference.PosTypeId);
        Assert.Contains(change.Attestations, att => att.ObjectId == posId && att.SubjectId == form);
    }



    [Fact]

    public void SymmetricTranslation_NativeParity()

    {

        var src = Hash128.OfCanonical("substrate/test/reg/source");

        var a = Hash128.OfCanonical("substrate/test/reg/a");

        var b = Hash128.OfCanonical("substrate/test/reg/b");

        var ab = NativeAttestation.Categorical(a, "IS_TRANSLATION_OF", b, src, null, SourceTrust.StructuredCorpus);

        var ba = NativeAttestation.Categorical(b, "IS_TRANSLATION_OF", a, src, null, SourceTrust.StructuredCorpus);

        Assert.Equal(ab.Id, ba.Id);

    }



    [Fact]

    public void ComputeId_DeterministicOnSameTuple()

    {

        var subj = H("subject/v1");

        var relType = H("type/v1");

        var obj  = H("object/v1");

        var src  = H("source/v1");

        var ctx  = H("context/v1");



        var id1 = NativeAttestation.ComputeId(subj, relType, obj, src, ctx);

        var id2 = NativeAttestation.ComputeId(subj, relType, obj, src, ctx);

        Assert.Equal(id1, id2);

    }



    [Fact]

    public void ComputeId_ChangesOnAnyTupleSlot()

    {

        var baseId = NativeAttestation.ComputeId(

            H("a"), H("b"), H("c"), H("d"), H("e"));

        Assert.NotEqual(baseId, NativeAttestation.ComputeId(H("X"), H("b"), H("c"), H("d"), H("e")));

        Assert.NotEqual(baseId, NativeAttestation.ComputeId(H("a"), H("X"), H("c"), H("d"), H("e")));

        Assert.NotEqual(baseId, NativeAttestation.ComputeId(H("a"), H("b"), H("X"), H("d"), H("e")));

        Assert.NotEqual(baseId, NativeAttestation.ComputeId(H("a"), H("b"), H("c"), H("X"), H("e")));

        Assert.NotEqual(baseId, NativeAttestation.ComputeId(H("a"), H("b"), H("c"), H("d"), H("X")));

    }



    [Fact]

    public void ComputeId_NullObjAndNullCtx_TreatedAsZeroHash()

    {

        var withNulls = NativeAttestation.ComputeId(H("a"), H("b"), null, H("d"), null);

        var withZeros = NativeAttestation.ComputeId(H("a"), H("b"), Hash128.Zero, H("d"), Hash128.Zero);

        Assert.Equal(withZeros, withNulls);

    }



    [Fact]

    public void ComputeId_MatchesObservationRowId()

    {

        var subj = H("s"); var relType = H("k"); var obj = H("o");

        var src  = H("src"); var ctx = H("ctx");



        var standalone = NativeAttestation.ComputeId(subj, relType, obj, src, ctx);

        var fromObs = NativeAttestation.ResolvedScored(

            subj, relType, obj, src, ctx, signedMagnitude: 0.5, arenaScale: 0.1, witnessWeight: 0.5).Id;

        Assert.Equal(standalone, fromObs);

    }



    [Fact]

    public void Score_PositiveMagnitude_AboveHalf()

        => Assert.True(NativeAttestation.Score(1.0, 1.0) > 0.5);



    [Fact]

    public void Score_NegativeMagnitude_BelowHalf()

        => Assert.True(NativeAttestation.Score(-1.0, 1.0) < 0.5);



    [Fact]

    public void Score_ZeroMagnitude_IsExactlyHalf()

        => Assert.Equal(0.5, NativeAttestation.Score(0.0, 1.0), 12);



    [Fact]

    public void Score_IsSymmetricAroundHalf()

    {

        double up = NativeAttestation.Score(0.7, 0.3);

        double dn = NativeAttestation.Score(-0.7, 0.3);

        Assert.Equal(1.0, up + dn, 12);

    }



    [Fact]

    public void Score_StrongerMagnitude_HigherScore()

        => Assert.True(NativeAttestation.Score(2.0, 1.0) > NativeAttestation.Score(0.5, 1.0));



    [Fact]

    public void ResolvedScored_PositiveMagnitude_ScoreAboveHalf()

    {

        var r = NativeAttestation.ResolvedScored(

            H("s"), H("k"), H("o"), H("src"), null, signedMagnitude: 1.5, arenaScale: 1.0, witnessWeight: 0.5);

        Assert.True(r.ScoreFp1e9 > Glicko2.FpScale / 2);

    }



    [Fact]

    public void ResolvedScored_NegativeMagnitude_ScoreBelowHalf()

    {

        var r = NativeAttestation.ResolvedScored(

            H("s"), H("k"), H("o"), H("src"), null, signedMagnitude: -1.5, arenaScale: 1.0, witnessWeight: 0.5);

        Assert.True(r.ScoreFp1e9 < Glicko2.FpScale / 2);

    }



    [Fact]

    public void WitnessPhi_TrustedTighterThanCrank()

        => Assert.True(NativeAttestation.WitnessPhi(1.0) < NativeAttestation.WitnessPhi(0.1));



    [Fact]

    public void Trust_ChangesOpponentRd_NotScore()

    {

        var trusted = NativeAttestation.ResolvedScored(

            H("s"), H("k"), H("o"), H("src"), null, signedMagnitude: 1.0, arenaScale: 1.0, witnessWeight: 1.0);

        var crank = NativeAttestation.ResolvedScored(

            H("s"), H("k"), H("o"), H("src"), null, signedMagnitude: 1.0, arenaScale: 1.0, witnessWeight: 0.05);



        Assert.Equal(trusted.ScoreFp1e9, crank.ScoreFp1e9);

        Assert.True(trusted.OpponentRdFp1e9 < crank.OpponentRdFp1e9);

    }



    [Fact]

    public void CategoricalResolved_ConfirmIsWin_RefuteIsLoss()

    {

        var confirm = NativeAttestation.CategoricalResolved(

            H("s"), H("k"), H("o"), H("src"), null, confirm: true, witnessWeight: 1.0);

        var refute = NativeAttestation.CategoricalResolved(

            H("s"), H("k"), H("o2"), H("src"), null, confirm: false, witnessWeight: 1.0);



        Assert.Equal(Glicko2.FpScale, confirm.ScoreFp1e9);

        Assert.Equal(0L,              refute.ScoreFp1e9);

    }



    [Fact]

    public void Evidence_CarriesNoAccumulatedState()

    {

        var r = NativeAttestation.ResolvedScored(

            H("s"), H("k"), H("o"), H("src"), null, signedMagnitude: 1.0, arenaScale: 1.0, witnessWeight: 0.5);

        Assert.True(r.ScoreFp1e9 is >= 0 and <= Glicko2.FpScale);

        Assert.True(r.OpponentRdFp1e9 > 0);

    }



    [Fact]

    public void Aggregated_OutcomeFromNetScore()

    {

        var subj = H("s");

        var type = H("k");

        var obj  = H("o");

        var src  = H("src");



        var win = NativeAttestation.Aggregated(subj, type, obj, src, null, 2, 2 * Glicko2.FpScale, 1.0);

        var loss = NativeAttestation.Aggregated(subj, type, obj, src, null, 2, 0, 1.0);

        var draw = NativeAttestation.Aggregated(subj, type, obj, src, null, 2, Glicko2.FpScale, 1.0);



        Assert.Equal(AttestationOutcome.Confirm, win.Outcome);

        Assert.Equal(AttestationOutcome.Refute, loss.Outcome);

        Assert.Equal(AttestationOutcome.Draw, draw.Outcome);

        Assert.Equal(2 * Glicko2.FpScale, win.SumScoreFp1e9);

        Assert.Equal(Glicko2.FpScale, win.ScoreFp1e9);

        Assert.Equal(win.Id, loss.Id);

    }

}

