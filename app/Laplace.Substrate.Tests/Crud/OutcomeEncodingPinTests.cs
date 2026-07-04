using Xunit;
using Laplace.Modality;
using Laplace.SubstrateCRUD;

namespace Laplace.SubstrateCRUD.Tests;

// AttestationOutcome (substrate) and PlyOutcome (modality) are two domain enums that share
// the {0,1,2} encoding ON PURPOSE — attestations.outcome's SQL CHECK and the chess ply
// encoding are the same wire values. Nothing structural enforces that; this pin does.
// The C side hardcodes the same values (LAPLACE_ATTESTATION_OUTCOME_* in
// attestation_engine.h) and PhysicalityType.Content must stay 1 because the C
// physicality_id_compute hardcodes physicality_type=1 into the id preimage.
public class OutcomeEncodingPinTests
{
    [Fact]
    public void AttestationOutcome_And_PlyOutcome_Share_The_Wire_Encoding()
    {
        Assert.Equal(0, (int)AttestationOutcome.Refute);
        Assert.Equal(1, (int)AttestationOutcome.Draw);
        Assert.Equal(2, (int)AttestationOutcome.Confirm);
        Assert.Equal((int)AttestationOutcome.Refute, (int)PlyOutcome.Loss);
        Assert.Equal((int)AttestationOutcome.Draw, (int)PlyOutcome.Draw);
        Assert.Equal((int)AttestationOutcome.Confirm, (int)PlyOutcome.Win);
    }

    [Fact]
    public void PhysicalityType_Content_Is_1_Matching_Native_Hardcode()
    {
        // engine/core/src/content_witness_batch.c physicality_id_compute() bakes
        // physicality_type=1 into every natively-minted physicality id. C# passes the type
        // as a parameter (PhysicalityId.Compute), so the two agree only while Content == 1.
        Assert.Equal(1, (int)PhysicalityType.Content);
    }
}
