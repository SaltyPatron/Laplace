using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public static unsafe partial class NativeInterop
{
    [LibraryImport(Library, EntryPoint = "laplace_ud_witness_build")]
    internal static partial int UdWitnessBuild(
        Hash128* flat, nuint flatCount,
        Hash128* source, Hash128* parseOccurrence, Hash128* languageMiscKey,
        double witnessWeight, long nowUnixUs,
        AttestationStagedNative* output, nuint outputCapacity, nuint* outputCount);
}
