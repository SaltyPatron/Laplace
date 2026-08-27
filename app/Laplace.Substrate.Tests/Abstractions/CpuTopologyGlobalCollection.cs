using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Serializes every test class that MUTATES or READS the process-global CPU topology.
///
/// `CpuTopology.TestOverride`, `TestPCoreIndicesOverride` and `TestPoolsOverride` are static
/// mutable fields, and xunit runs distinct test CLASSES in parallel by default. So while
/// IngestParallelismTests had a fabricated 8-core hybrid topology installed, sibling classes
/// were reading PostgresResourcePlan.Current / IngestTopology.Current and doing pool
/// arithmetic against it.
///
/// That is the documented flake in docs/evidence-flattening-2026-08-23.md
/// (`IngestPool_CoversItsFansAndItsObservabilityOwners`, recorded there as deriving from
/// "ambient machine resources" and not isolated). It is not ambient. Measured 2026-08-24:
/// three consecutive runs of the unchanged suite failed three DIFFERENT tests --
/// ApplyConnectionBudgetTests.CopyBudget_Plus_FoldFan_..., then
/// LaplaceDataSourceTests.Ingest_LeavesTimeoutUnbounded_..., then
/// PgTuningParityTests.IngestPool_CoversItsFansAndItsObservabilityOwners -- which is the
/// signature of a shared mutable global under parallel execution, not of a marginal machine.
///
/// A serial collection alone excludes only its own members, not all the other readers.
/// CI run 33105290406 caught inventory sizing reading a synthetic topology between
/// creating its files and checking their size against the I/O budget. Run this global
/// override collection without other collections; ordinary tests remain parallel.
/// </summary>
[CollectionDefinition("cpu-topology-global", DisableParallelization = true)]
public sealed class CpuTopologyGlobalCollection { }
