using Xunit;

// Every test in this assembly drives the content witness, which reads the process-global
// T0 perfcache and mutates the process-global record-once emit bank (g_emit_bank in
// content_witness_batch.c). Those singletons are not safe across xUnit's default parallel
// collections: a sibling test banking a content root mid-run makes another test's
// attestations reference an entity row that was never written into its (separate, fresh)
// database — the 1286-ghost-reference failure. Serialize the whole assembly so each ingest
// owns the globals for its duration. Same disease, and same remedy, as the GrammarPerfcache
// collection in Laplace.Decomposers.Abstractions.Tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
