# ADR 0052: Ingest pipeline orchestration — `IngestRunner` composes the three stages

## Status

**Proposed** — 2026-05-24
**Authors:** Anthony Hart

## Context

The substrate's ingest pipeline factors into three distinct stages with three different traversal directions per the 2026-05-24 conversation:

1. **Decomposition** (trunk → leaf, client-side, zero DB) — done by per-source decomposers per [ADR 0051](0051-idecomposer-csharp-plugin-contract.md); text-specific path goes through [TextDecomposer / ADR 0047](0047-text-decomposer-pure-primitive.md).
2. **Hash / coord / Hilbert composition** (leaf → trunk, client-side, zero DB) — done by [HashComposer / ADR 0048](0048-hash-composer-leaf-to-trunk.md).
3. **Dedup + insert** (trunk → leaf, DB-interactive) — done by [SubstrateCRUD / ADR 0050](0050-substrate-crud-write-surface.md) consuming [`SubstrateChange` intents / ADR 0049](0049-substrate-change-intent-type.md).

Each stage is pure and bounded. But *who runs the loop*? Without a documented orchestrator:

- Each decomposer reimplements its own outer loop (read source → strip-markers → TextDecomposer → HashComposer → build attestations → call SubstrateCRUD → emit progress → checkpoint → handle cancellation → retry on transient error → emit metrics → next iteration). That's the per-source-decomposer duplication anti-pattern again.
- Cross-cutting concerns (progress bar, structured logging, Prometheus metrics, checkpoint write cadence, transient-error retry policy, parallel worker pool for embarrassingly-parallel decomposers like TreeSitterDecomposer across 303 grammars, layer-ordering enforcement per [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md)) have to be reimplemented N times.
- The CLI entry point (`just ingest <source>` → which calls the appropriate Laplace.Cli subcommand) has no shared substrate to dispatch into.

The conversation excerpt that surfaced this: *"the client can decompose text, structure all of the records, establish the proper sequencing, etc... all before ever talking to the database to see what real interactions need to happen of which should just be simple CRUD orchestration we can optimize the generalize the fuck out of across the repo without reinventing the wheel a trillion times."*

The orchestration loop IS that shared substrate. One implementation; every decomposer composes through it.

## Decision

**Introduce `IngestRunner` as the shared orchestration loop that composes `IDecomposer` + `SubstrateCRUD` into the canonical per-source-decomposer ingest recipe.**

### Public surface

```csharp
namespace Laplace.Ingestion;

public sealed class IngestRunner {
    public IngestRunner(
        ISubstrateWriter writer,
        ISubstrateReader reader,
        ILoggerFactory loggerFactory,
        IIngestObservability observability);

    /// <summary>
    /// Run a per-source decomposer end-to-end:
    ///   1. Verify layer-ordering prerequisites per ADR 0037 (this decomposer's
    ///      Layer N requires Layer 1..N-1 to have completed at least once).
    ///   2. Invoke decomposer.InitializeAsync (bootstrap source entity + type/kind
    ///      vocabulary per ADR 0042).
    ///   3. Estimate total unit count for progress reporting.
    ///   4. Open / resume the checkpoint journal at the configured path.
    ///   5. Iterate decomposer.DecomposeAsync; for each yielded SubstrateChange:
    ///        a. Skip if already applied per the checkpoint journal.
    ///        b. SubstrateCRUD.ApplyAsync(intent).
    ///        c. Append (intent.Id, applied_at) to checkpoint journal; fsync per
    ///           batched-commit boundary.
    ///        d. Emit per-intent metrics + progress event.
    ///        e. On transient error: exponential backoff + retry per policy.
    ///        f. On cancellation: graceful shutdown after current intent commits.
    ///        g. On fatal error: surface + abort + checkpoint stays at last good intent.
    ///   6. Final summary metrics on completion.
    /// </summary>
    public Task<IngestRunResult> RunAsync(
        IDecomposer decomposer,
        IngestRunOptions options,
        CancellationToken ct = default);
}

public sealed record IngestRunOptions(
    DecomposerOptions DecomposerOptions,
    int ParallelWorkers,                    // 1 for serial; >1 for embarrassingly-parallel decomposers
    TimeSpan CheckpointFlushInterval,       // default 30s
    TransientErrorRetryPolicy RetryPolicy,
    IProgress<IngestProgress>? Progress);

public sealed record IngestRunResult(
    Hash128 SourceId,
    long UnitsAttempted,
    long UnitsApplied,
    long UnitsSkippedFromCheckpoint,
    long UnitsFailed,
    long EntitiesInserted,                  // sum across all applied intents
    long PhysicalitiesInserted,
    long AttestationsInserted,
    long TotalRoundTrips,
    TimeSpan WallClock,
    IReadOnlyList<IngestFailure> Failures);
```

### Algorithm

```text
RunAsync(decomposer, options, ct):
    # 1. Layer-ordering prerequisite check
    for layer in 1..(decomposer.LayerOrder - 1):
        if not reader.HasSourceEverCompleted(layer):
            throw new LayerOrderingViolation(
                "Layer N requires Layer 1..N-1 completion per ADR 0037")

    # 2. Bootstrap (idempotent)
    await decomposer.InitializeAsync(context, ct)

    # 3. Progress estimation
    estimated_units = await decomposer.EstimateUnitCountAsync(context, ct)
    progress.Report(IngestProgress { Total = estimated_units, Completed = 0 })

    # 4. Checkpoint open
    checkpoint = await CheckpointJournal.OpenOrCreate(options.CheckpointPath)
    applied_intent_ids = checkpoint.AppliedIntentIds()

    # 5. Iterate, apply, checkpoint, observe
    await foreach (var intent in decomposer.DecomposeAsync(context, options.DecomposerOptions, ct)):
        ct.ThrowIfCancellationRequested()

        if intent.Metadata.IntentId in applied_intent_ids:
            metrics.IncrementSkipped()
            continue

        try:
            result = await RetryWithBackoff(
                () => writer.ApplyAsync(intent, ct),
                options.RetryPolicy)

            await checkpoint.AppendAsync(intent.Metadata.IntentId, DateTimeOffset.UtcNow)
            if (units_applied % options.CheckpointFlushBatchSize) == 0:
                await checkpoint.FlushAsync()

            metrics.RecordApplied(result)
            progress.Report(IngestProgress { Completed = units_applied++ })

        catch (FatalIngestError ex):
            failures.Add(IngestFailure { Intent = intent, Exception = ex })
            await checkpoint.FlushAsync()  // ensure last-good-state is durable
            throw  # abort the run; let CLI/operator decide whether to retry

        catch (TransientError ex):
            # Already retried by RetryWithBackoff; if we got here, retries exhausted
            failures.Add(IngestFailure { Intent = intent, Exception = ex })
            if options.AbortOnTransientExhaustion:
                throw
            # else continue with next intent; failure is recorded

    # 6. Final flush + summary
    await checkpoint.FlushAsync()
    return IngestRunResult { ... }
```

### Parallel-worker variant for embarrassingly-parallel decomposers

`TreeSitterDecomposer` (303 grammars), `ConceptNetDecomposer` (10+ sub-sources via composite per ADR 0043 pattern), large `WiktionaryDecomposer` runs (per-language partitioning), etc. benefit from worker-pool parallelism: 4-16 workers each consuming from `decomposer.DecomposeAsync` independently.

The orchestration shape: one producer (the decomposer's `DecomposeAsync` enumerable) into a bounded `Channel<SubstrateChange>`; N worker tasks dequeue + apply via `SubstrateCRUD`; checkpoint journal coordinates per-intent applied-set across workers (PG `LISTEN`/`NOTIFY` or shared concurrent set).

Race-tolerance per [RULES.md R5](../../RULES.md) + [ADR 0050](0050-substrate-crud-write-surface.md): two workers concurrently applying intents that both contain the same shared entity ID converge via `ON CONFLICT DO NOTHING`. No explicit coordination needed at the application layer.

### Cross-cutting concerns owned by `IngestRunner`

| Concern | Implementation |
|---|---|
| **Layer-ordering enforcement** | `reader.HasSourceEverCompleted(LayerOrder)` check per ADR 0037; refuses Layer-N decomposer without Layer 1..N-1 complete |
| **Progress reporting** | `IProgress<IngestProgress>` events at per-intent or per-batch cadence; structured for CLI progress bar + log aggregation |
| **Structured logging** | `ILogger<IngestRunner>` with per-source labels; intent-level + run-level scopes |
| **Prometheus metrics** | `laplace_ingest_*` family — units_attempted_total, units_applied_total, units_skipped_total, units_failed_total, wall_clock_seconds, current_progress_ratio (labeled by source) |
| **Checkpoint journal** | Append-only `<source-data-path>/checkpoint.bin`; fsync per `CheckpointFlushInterval` or per-N-intents batched-commit boundary; resume skips applied set |
| **Transient retry** | Exponential backoff + jitter; default policy: 3 retries with 100ms / 1s / 10s; configurable per source |
| **Cancellation** | `CancellationToken` propagation; graceful shutdown after current intent commits to journal |
| **Parallel worker pool** | Bounded `Channel<SubstrateChange>` between decomposer enumerable + N worker tasks; race-tolerance via content-addressing + ON CONFLICT |
| **Per-intent observability events** | Hooks for tracing / OpenTelemetry / custom metrics emitters |

### Placement

- `Laplace.Ingestion` project under `app/Laplace.Ingestion/` per [ADR 0026](0026-csharp-project-structure.md)
- References `Laplace.Decomposers.Abstractions` ([ADR 0051](0051-idecomposer-csharp-plugin-contract.md)) for `IDecomposer` + supporting types
- References `Laplace.SubstrateCRUD` ([ADR 0050](0050-substrate-crud-write-surface.md)) for `ISubstrateWriter`
- Test project `Laplace.Ingestion.Tests` with `Testcontainers.PostgreSql` per [STANDARDS.md Testing](../../STANDARDS.md)

### CLI integration

`just ingest <source> [path]` per the Justfile resolves `<source>` → concrete `IDecomposer` instance → `IngestRunner.RunAsync`. `Laplace.Cli` subcommand `ingest` wires plugin discovery + `IngestRunner` invocation.

### What `IngestRunner` does NOT do

- Decompose anything (decomposer's job)
- Compute hashes (HashComposer's job, called by decomposer)
- Write to PG (SubstrateCRUD's job, called by IngestRunner)
- Define the substrate schema (extensions' job per [ADR 0023](0023-extension-owns-schema-dbup-orchestrates.md))
- Define per-source attestation kinds (decomposer's bootstrap responsibility per [ADR 0042](0042-bootstrap-order-and-substrate-canonical-seeding.md))
- Make ingest decisions (decomposer decides what to emit; orchestrator just applies)
- Implement cascade reads ([cascade SRF per ADR 0035](0035-prompt-ingestion-and-compiled-cascade.md) + future `CascadeRunner` ADR is the read-symmetry partner)

## Consequences

- **One orchestration loop**, used by every per-source decomposer. Bug fix in IngestRunner applies uniformly.
- **Per-source decomposers focus on per-source concerns** (parsing the source's format, stripping its markers, building its attestations). Everything else — checkpointing, retry, parallelism, progress, observability — is shared infrastructure.
- **`just ingest <X>` becomes uniform**. Every source ingests through the same code path with the same logging / metrics / progress shape.
- **Layer-ordering enforced operationally**, not by convention. Attempting to ingest WordNet (Layer 3) before UnicodeDecomposer (Layer 1) completes is a hard error with a clear message pointing at ADR 0037.
- **Parallel ingest available as a switch**, not as per-decomposer reinvention.
- **Multi-hour ingest crash-resume is a substrate capability**, not per-decomposer effort.

## Alternatives considered

- **Each decomposer owns its outer loop.** Rejected — duplication anti-pattern. Cross-cutting concerns reimplemented N times poorly.
- **Combine `IngestRunner` with `SubstrateCRUD`.** Rejected — SubstrateCRUD's scope is per-intent (one apply). IngestRunner's scope is per-run (many intents + cross-cutting concerns). Different concerns; cleaner separation.
- **Single-threaded only (no parallel-worker variant).** Rejected — TreeSitterDecomposer at 303 grammars, ConceptNetDecomposer across sub-sources, model-vocab ingest at 150K+ tokens benefit enough from parallelism to be worth the complexity.
- **Implement IngestRunner in C++ (engine-side).** Rejected — per [RULES.md R16](../../RULES.md) + [ADR 0027](0027-separation-of-concerns-invariants.md) orchestration is C# work. Engine does math; C# does pipeline + plugin host.

## References

- [RULES.md R5](../../RULES.md) — attestation idempotency (informs retry-tolerance + race-tolerance)
- [RULES.md R10](../../RULES.md) — polymorphic plugin architecture
- [RULES.md R16](../../RULES.md) — separation of concerns (orchestration in C#)
- [STANDARDS.md "Reusable helpers — DRY at every layer"](../../STANDARDS.md)
- [STANDARDS.md Testing](../../STANDARDS.md)
- [STANDARDS.md Logging](../../STANDARDS.md)
- [DESIGN.md II.B — Module map](../../DESIGN.md)
- [ADR 0011](0011-polymorphic-plugin-architecture.md)
- [ADR 0016](0016-reusable-helpers-discipline.md) — reusable helpers
- [ADR 0026](0026-csharp-project-structure.md) — `Laplace.Ingestion` placement
- [ADR 0027](0027-separation-of-concerns-invariants.md) — orchestration in C#
- [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md) — layer ordering
- [ADR 0042](0042-bootstrap-order-and-substrate-canonical-seeding.md) — bootstrap responsibility (called via decomposer.InitializeAsync)
- [ADR 0047 TextDecomposer](0047-text-decomposer-pure-primitive.md)
- [ADR 0048 HashComposer](0048-hash-composer-leaf-to-trunk.md)
- [ADR 0049 SubstrateChange](0049-substrate-change-intent-type.md)
- [ADR 0050 SubstrateCRUD](0050-substrate-crud-write-surface.md)
- [ADR 0051 IDecomposer C# plugin contract](0051-idecomposer-csharp-plugin-contract.md)
- Conversation 2026-05-24: client-does-all-work + CRUD-as-shared-primitive; three-direction ingest pipeline
