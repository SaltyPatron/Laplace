using Xunit;

namespace Laplace.Decomposers.Abstractions.Tests;

/// <summary>
/// Serializes every test class that mutates or reads the process-global CPU topology.
///
/// <c>CpuTopology.TestOverride</c>, <c>TestPCoreIndicesOverride</c> and
/// <c>TestPoolsOverride</c> are static mutable fields, and xunit runs test classes in
/// parallel, so a class installing a synthetic topology would change the pool arithmetic
/// that PostgresResourcePlan.Current / IngestTopology.Current readers see. With
/// parallelization disabled this collection runs apart from all other collections;
/// ordinary tests stay parallel.
/// </summary>
[CollectionDefinition("cpu-topology-global", DisableParallelization = true)]
public sealed class CpuTopologyGlobalCollection { }
