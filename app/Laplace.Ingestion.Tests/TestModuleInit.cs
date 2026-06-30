using System.Runtime.CompilerServices;
using Laplace.Engine.Core;

namespace Laplace.Ingestion.Tests;

/// <summary>
/// The T0 codepoint perfcache is a process-global required by any content-witness emission
/// (IntentStage / ContentWitnessBatch), which the synthetic-decomposer convergence tests exercise.
/// Production ingest loads it before any seeding; this assembly must too. Loading it once here,
/// before any test runs, removes per-test ordering fragility. LoadDefault is idempotent, so any
/// per-test call remains harmless. Mirrors Laplace.Decomposers.Abstractions.Tests.TestModuleInit.
/// </summary>
internal static class TestModuleInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        CodepointPerfcache.LoadDefault();
        HighwayPerfcache.LoadDefault();
    }
}
