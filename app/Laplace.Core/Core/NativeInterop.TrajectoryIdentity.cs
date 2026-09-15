using System.Runtime.InteropServices;

namespace Laplace.Engine.Core;

public static unsafe partial class NativeInterop
{
    [LibraryImport(Library, EntryPoint = "trajectory_content_identity")]
    internal static partial int TrajectoryContentIdentity(
        double* trajectoryXyzm, nuint nPoints, Hash128* outId, nuint* outCount);
}
