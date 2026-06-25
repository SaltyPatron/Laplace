using System.Runtime.CompilerServices;
using Laplace.Engine.Core;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// The T0 codepoint perfcache is a process-global required by any content-witness emission
/// (ContentWitnessBatch.Emit, used by SeedCanonical, SeedDynamic, PosReference, the *Anchor
/// helpers). Production ingest loads it before any seeding; tests must too. Loading it once here,
/// before any test runs, removes the per-test ordering fragility (a test that doesn't itself call
/// LoadDefault would throw "requires the T0 perfcache" only when scheduled before one that does).
/// LoadDefault is idempotent, so per-test calls remain harmless.
/// </summary>
internal static class TestModuleInit
{
    [ModuleInitializer]
    internal static void Init() => CodepointPerfcache.LoadDefault();
}
