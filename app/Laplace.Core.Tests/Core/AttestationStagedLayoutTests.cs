using System.Runtime.InteropServices;
using Laplace.Engine.Core;
using Xunit;

namespace Laplace.Core.Tests.Core;

/// <summary>
/// AttestationStagedNative mirrors laplace_attestation_staged_t
/// (engine/core/include/laplace/core/attestation_engine.h) across P/Invoke. A shifted
/// field would hand the consensus fold wrong rating/score values without any error, so
/// size and the offsets of the fold inputs are pinned to the C layout on this toolchain.
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
