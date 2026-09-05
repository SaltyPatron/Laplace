using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Core.Tests.Core;

/// <summary>
/// AttestationStagedNative mirrors laplace_attestation_staged_t across the P/Invoke
/// boundary, and NOTHING enforced that until now. The C struct gained
/// opponent_rating_fp1e9 (GH #1321 — the fold had no opponent rating, only an RD);
/// had the managed mirror not gained it in the same position, every field after
/// opponent_rd_fp1e9 would have been read from the wrong offset. That is silent
/// memory corruption on the ingest hot path, not a test failure — sum_score and
/// observation_count would have come back as garbage and folded as if real.
///
/// The expected values are measured, not derived: compiled against
/// engine/core/include/laplace/core/attestation_engine.h on this toolchain,
/// sizeof = 160, opponent_rd at 128, opponent_rating at 136, sum_score at 144.
/// If the C struct changes, this test fails and names the field that moved.
/// </summary>
public sealed class AttestationStagedLayoutTests
{
    [Fact]
    public void ManagedMirror_MatchesNativeStagedStructLayout()
    {
        Assert.Equal(160, Marshal.SizeOf<AttestationStagedNative>());

        Assert.Equal(128, (int)Marshal.OffsetOf<AttestationStagedNative>(
            nameof(AttestationStagedNative.OpponentRdFp1e9)));
        Assert.Equal(136, (int)Marshal.OffsetOf<AttestationStagedNative>(
            nameof(AttestationStagedNative.OpponentRatingFp1e9)));
        Assert.Equal(144, (int)Marshal.OffsetOf<AttestationStagedNative>(
            nameof(AttestationStagedNative.SumScoreFp1e9)));
        Assert.Equal(155, (int)Marshal.OffsetOf<AttestationStagedNative>(
            nameof(AttestationStagedNative.FoldReplayable)));
    }
}
