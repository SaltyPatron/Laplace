# ADR 0051: IDecomposer C# plugin contract — the per-source decomposer interface

## Status

**Accepted** — 2026-05-24 (status confirmed 2026-05-28: `IDecomposer` shipped at `app/Laplace.Decomposers.Abstractions/IDecomposer.cs`; `UnicodeDecomposer` implements it at `app/Laplace.Decomposers.Unicode/UnicodeDecomposer.cs`; nine further per-source `Laplace.Decomposers.<Source>` projects pending per ADR 0037 layer ladder.)
**Authors:** Anthony Hart

## Context

[ADR 0011 (polymorphic plugin architecture)](0011-polymorphic-plugin-architecture.md) defines six plugin interfaces — `ISource`, `IDecomposer`, `IArchitectureTemplate`, `IFormatWriter`, `IFeatureExtractor`, `IProtocolEndpoint` — and [DESIGN.md VI](../../DESIGN.md) sketches their C++ shape (`virtual TierTree decompose(const Bytes& content) = 0; virtual std::vector<EntityRef> chunk_at_tier(const Entity& parent, Tier t) = 0;`). [ADR 0026 (C# project structure)](0026-csharp-project-structure.md) lists `Laplace.Decomposers.*` as the C# host project family. [ADR 0041 (decomposer scope = full domain ecosystem)](0041-decomposer-scope-full-domain-ecosystem.md) locks scope. [ADR 0043 (composite decomposer architecture)](0043-composite-decomposer-architecture.md) shows the ModelDecomposer's composition pattern.

But the actual **C# `IDecomposer` interface contract** — the seam every per-source decomposer plugin (`UnicodeDecomposer`, `WordNetDecomposer`, `OMWDecomposer`, `UDDecomposer`, `WiktionaryDecomposer`, `TatoebaDecomposer`, `ConceptNetDecomposer`, `Atomic2020Decomposer`, `TreeSitterDecomposer`, the composite `ModelDecomposer` per ADR 0043) implements — has never been written. The DESIGN.md C++ sketch is too thin to ship against; it doesn't cover:

- Lifecycle (init / decompose / cleanup / dispose)
- Source location + ecosystem discovery (where on disk does the decomposer find its content?)
- Streaming vs batched output (37 GB Unicode, 34 GB Wiktionary, 125 GB DeepSeek-Coder-33B all need streaming; smaller sources can batch)
- Error propagation (exceptions, return codes, partial-failure reporting)
- Progress reporting (a 10-hour Wiktionary ingest needs progress events for the user/log)
- Cancellation (Ctrl+C must abort cleanly)
- Idempotency (re-ingesting the same source must converge via SubstrateCRUD's ON CONFLICT semantics)
- Cross-decomposer entity sharing (per the 2026-05-24 conversation — UnicodeDecomposer emits `Latn` Script entity at BLAKE3-of-canonical-name; ISODecomposer attaches ISO 15924 attestations to *the same row*; convergence happens automatically through content-addressing + ON CONFLICT, but the contract has to make this discoverable to a contributor writing a new decomposer)
- Composition with the shared primitives ([TextDecomposer / ADR 0047](0047-text-decomposer-pure-primitive.md), [HashComposer / ADR 0048](0048-hash-composer-leaf-to-trunk.md), [SubstrateChange / ADR 0049](0049-substrate-change-intent-type.md), [SubstrateCRUD / ADR 0050](0050-substrate-crud-write-surface.md))
- Bootstrap responsibility (per [ADR 0042](0042-bootstrap-order-and-substrate-canonical-seeding.md), each decomposer bootstraps its own type vocabulary + attestation kind vocabulary + arena-semantics meta-attestations + source-trust-class meta-attestation on first run)
- Test surface (how does `Laplace.Decomposers.WordNet.Tests` exercise WordNetDecomposer end-to-end against a Testcontainers PG?)
- Plugin discovery / loading (how does the CLI/endpoint find a decomposer to invoke for `just ingest wordnet`?)

Without this contract, every per-source decomposer (10+ planned) either invents its own shape — exactly the duplication anti-pattern [STANDARDS.md "Reusable helpers"](../../STANDARDS.md) + [ADR 0016](0016-reusable-helpers-discipline.md) forbid — or the planned decomposers stay un-implemented because no one knows where to start.

## Decision

**Introduce `IDecomposer` as the C# canonical plugin interface for per-source decomposers, in a shared `Laplace.Decomposers.Abstractions` project, used by every concrete per-source `Laplace.Decomposers.<Source>` project.**

### Interface

```csharp
namespace Laplace.Decomposers.Abstractions;

public interface IDecomposer : IAsyncDisposable {

    /// <summary>
    /// Identity of this decomposer — the source entity ID it emits as
    /// source_id on every entity/physicality/attestation it produces.
    /// Content-addressed: BLAKE3-128 of canonical name (e.g.,
    /// BLAKE3("substrate/source/UnicodeDecomposer/v1")).
    /// </summary>
    Hash128 SourceId { get; }

    /// <summary>
    /// Human-readable name for logs/observability/CLI.
    /// </summary>
    string SourceName { get; }

    /// <summary>
    /// Per ADR 0037 layer order (1 = UnicodeDecomposer; 10 = ModelDecomposer).
    /// Used by orchestration to enforce layer ordering for fresh-substrate
    /// bootstrap: Layer N's decomposer requires Layer 1..N-1 to have completed.
    /// </summary>
    int LayerOrder { get; }

    /// <summary>
    /// Trust class assigned to this source per ADR 0044. Recorded as a
    /// HAS_TRUST_CLASS meta-attestation on the source entity on first run.
    /// </summary>
    Hash128 TrustClassId { get; }

    /// <summary>
    /// Initialize: verify ecosystem path exists, register source entity if
    /// not already in substrate, bootstrap the decomposer's own type
    /// vocabulary + attestation kind vocabulary + arena-semantics meta-
    /// attestations per ADR 0042. Idempotent — re-init on an already-
    /// bootstrapped substrate is a no-op.
    /// </summary>
    Task InitializeAsync(IDecomposerContext context, CancellationToken ct = default);

    /// <summary>
    /// Decompose the source's full domain ecosystem into a STREAM of
    /// SubstrateChange intents per ADR 0049. Each yielded intent is one
    /// source-content-unit (one WordNet synset; one Wiktionary entry; one
    /// model tokenizer vocab batch; etc.). The caller (typically
    /// IngestRunner — see ADR 0052) hands each intent to
    /// SubstrateCRUD.ApplyAsync per ADR 0050.
    ///
    /// Streaming is mandatory — decomposers handling frontier-scale inputs
    /// (37 GB Unicode, 125 GB DeepSeek-Coder, 34 GB Wiktionary) cannot
    /// buffer everything in RAM. Per-unit yield with backpressure.
    /// </summary>
    IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext context,
        DecomposerOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// Estimate the total number of source-content-units this decomposer
    /// will yield, for progress reporting. Cheap to compute (typically a
    /// file-count or index-lookup). May return null if estimation is
    /// expensive or impossible.
    /// </summary>
    Task<long?> EstimateUnitCountAsync(IDecomposerContext context, CancellationToken ct = default);
}

public interface IDecomposerContext {
    /// <summary>Path to the source ecosystem (e.g., /vault/Data/Wordnet/).</summary>
    string EcosystemPath { get; }

    /// <summary>Substrate write surface — every decomposer routes intents
    /// through this. Per ADR 0050.</summary>
    ISubstrateWriter Writer { get; }

    /// <summary>Read access for verifying existing substrate state during
    /// bootstrap (e.g., "does the SubstrateCanonical source entity exist
    /// yet?"). Read-only.</summary>
    ISubstrateReader Reader { get; }

    /// <summary>Per-decomposer structured logger.</summary>
    ILogger Logger { get; }

    /// <summary>Substrate-version tag (informs deterministic content-addressed
    /// IDs for bootstrap entities like type/kind vocabulary entries — name
    /// includes substrate-version per ADR 0042).</summary>
    string SubstrateVersion { get; }
}

public sealed record DecomposerOptions(
    int BatchSize,                          // unit count per intent batch (default per-decomposer)
    bool DryRun,                            // build intents but don't call SubstrateCRUD
    bool ResumeFromCheckpoint,              // skip already-applied intents per journal
    string? CheckpointPath,                 // override default checkpoint location
    IReadOnlySet<string>? IncludeFilter,    // per-decomposer source-content-unit filter (e.g., "only synsets in lexname 'noun.animal'")
    IReadOnlySet<string>? ExcludeFilter
);
```

### What a concrete decomposer looks like

```csharp
// Laplace.Decomposers.WordNet/WordNetDecomposer.cs
public sealed class WordNetDecomposer : IDecomposer {

    public Hash128 SourceId => Hash128.OfCanonical("substrate/source/WordNetDecomposer/v1");
    public string SourceName => "WordNet";
    public int LayerOrder => 3;  // per ADR 0037
    public Hash128 TrustClassId => Hash128.OfCanonical("substrate/trust/AcademicCurated/v1");  // per ADR 0044

    public async Task InitializeAsync(IDecomposerContext ctx, CancellationToken ct) {
        // Register source entity + bootstrap WordNet's type vocabulary
        // (WordNet_Synset, WordNet_Sense) + WordNet's attestation kinds
        // (IS_LEMMA_OF, HAS_POS, IS_HYPERNYM_OF, IS_HYPONYM_OF, IS_MERONYM_OF,
        //  IS_HOLONYM_OF, IS_ANTONYM_OF, HAS_GLOSS, HAS_EXAMPLE, ...)
        // via a single bootstrap SubstrateChange. Per ADR 0042.
        var bootstrap = BuildBootstrapIntent(ctx);
        await ctx.Writer.ApplyAsync(bootstrap, ct);
    }

    public async IAsyncEnumerable<SubstrateChange> DecomposeAsync(
        IDecomposerContext ctx, DecomposerOptions options,
        [EnumeratorCancellation] CancellationToken ct = default) {

        var synsetReader = WordNetReader.Open(ctx.EcosystemPath);

        await foreach (var synset in synsetReader.ReadSynsetsAsync(ct)) {
            ct.ThrowIfCancellationRequested();

            // Per-synset:
            //   1. Strip WordNet's source-specific markers (underscore-to-space
            //      for multi-word lemmas; lexicographer-file annotations; etc.)
            //   2. Call TextDecomposer for the gloss + example texts + lemma surface forms
            //   3. Call HashComposer to populate IDs/coords/Hilbert
            //   4. Build WordNet-specific attestations (HAS_GLOSS, IS_LEMMA_OF, IS_HYPERNYM_OF, ...)
            //   5. Yield one SubstrateChange intent per synset

            yield return BuildSynsetIntent(ctx, synset);
        }
    }

    public Task<long?> EstimateUnitCountAsync(IDecomposerContext ctx, CancellationToken ct)
        => Task.FromResult<long?>(117_659);  // WordNet 3.0 synset count

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

The same shape applies to every per-source decomposer. The IngestRunner ([per ADR 0052](0052-ingest-pipeline-orchestration.md)) calls `DecomposeAsync` and pipes each yielded intent to `SubstrateCRUD.ApplyAsync` per [ADR 0050](0050-substrate-crud-write-surface.md).

### Per-source layer-ordering and bootstrap discipline

- `LayerOrder` enforces the [ADR 0037 layered seed ingestion order](0037-layered-seed-ingestion-and-model-codec-fidelity.md) — orchestration may refuse to invoke a Layer-N decomposer if any Layer<N decomposer hasn't completed at least once.
- `TrustClassId` references the source-trust-class entity per [ADR 0044](0044-attestation-kind-priors-and-source-trust-taxonomy.md) bootstrapped at install per ADR 0042 Stage 3.5.
- `InitializeAsync` is responsible for emitting the decomposer's own *type vocabulary* + *attestation kind vocabulary* + *arena semantics meta-attestations* + *source registration* per [ADR 0042](0042-bootstrap-order-and-substrate-canonical-seeding.md) Stage 6+. Idempotent — re-invoking against an already-bootstrapped substrate is a no-op via `SubstrateCRUD`'s ON CONFLICT semantics.

### Cross-decomposer entity sharing (automatic via content-addressing)

When `WordNetDecomposer` emits a `walk` text entity at `BLAKE3-of("walk")` AND `ModelDecomposer.TextModality` emits the same `walk` text entity at the same BLAKE3, both intents reference the same row. `SubstrateCRUD`'s `ON CONFLICT DO NOTHING` per [RULES.md R5](../../RULES.md) handles the convergence transparently. No coordination logic in either decomposer. Per the 2026-05-24 conversation: *"cross-decomposer shared-entity coordination — when UnicodeDecomposer creates `Latn` Script entity and ISODecomposer attaches ISO 15924 attestations to the same id, the CRUD layer's ON CONFLICT DO NOTHING + content-addressing handle the convergence transparently."*

### Plugin discovery + loading

Concrete decomposers register via `[Export(typeof(IDecomposer))]` (MEF) or assembly-scan discovery in `Laplace.Cli`. `just ingest wordnet` resolves `wordnet` → `WordNetDecomposer` instance. Plugin discovery mechanism deferred to implementation Story; the *interface* contract is stable independent of discovery mechanism.

### Test surface

Each concrete decomposer ships a `Laplace.Decomposers.<Source>.Tests` xUnit project with `Testcontainers.PostgreSql` per [STANDARDS.md Testing](../../STANDARDS.md). Test pattern:

```csharp
[Fact]
public async Task WordNet_Bootstrap_IsIdempotent() {
    await using var pg = await TestcontainersPostgres.StartAsync();
    var ctx = new TestDecomposerContext(pg, "/vault/Data/Wordnet/WordNet-3.0/");

    var decomposer = new WordNetDecomposer();
    await decomposer.InitializeAsync(ctx);

    // Re-init must be no-op
    await decomposer.InitializeAsync(ctx);

    var typeVocabCount = await ctx.Reader.CountEntitiesByType(
        Hash128.OfCanonical("substrate/type/Type/v1"));
    Assert.Equal(/* expected type-vocab size for WordNet */, typeVocabCount);
}

[Fact]
public async Task WordNet_Ingest_FullDecomposeProducesExpectedSynsetCount() {
    // ... full ingest of WordNet 3.0; assert 117_659 synset entities + their
    // attestation clouds via Reader queries.
}
```

The `IDecomposerContext` abstraction lets the test harness inject test-Reader + test-Writer + ephemeral test ecosystem path.

### Placement

- `Laplace.Decomposers.Abstractions` (interface + supporting types)
- `Laplace.Decomposers.Unicode` (concrete UnicodeDecomposer)
- `Laplace.Decomposers.ISO`
- `Laplace.Decomposers.WordNet`
- `Laplace.Decomposers.OMW`
- `Laplace.Decomposers.UD`
- `Laplace.Decomposers.Wiktionary`
- `Laplace.Decomposers.Tatoeba`
- `Laplace.Decomposers.ConceptNet`
- `Laplace.Decomposers.Atomic2020`
- `Laplace.Decomposers.TreeSitter`
- `Laplace.Decomposers.Model` (composite per ADR 0043)
- ... one project per source

All under `app/Laplace.Decomposers.*/` per [ADR 0026](0026-csharp-project-structure.md).

## Consequences

- **One contract for every decomposer**, instantiated 10+ times. New source = new `Laplace.Decomposers.<Source>` project implementing `IDecomposer`. No bespoke shapes.
- **Layer ordering enforceable in orchestration**, not relied on by convention.
- **Bootstrap is per-decomposer responsibility**, expressed via `InitializeAsync` + the first SubstrateChange. ADR 0042's staged bootstrap maps naturally onto per-decomposer init sequences.
- **Streaming-first API** handles frontier-scale ingest without buffering. Smaller sources can yield a single large intent + return; large sources yield many small intents.
- **Cross-decomposer convergence is free** via content-addressing + ON CONFLICT. No coordination ADR needed beyond ADR 0050.
- **Testability built in**: `IDecomposerContext` abstraction → straightforward unit + integration testing.
- **The C++ `IDecomposer` sketch in DESIGN.md VI is superseded by this C# contract.** That sketch was sketch-level; the substantive interface lives C#-side because every per-source decomposer is an orchestration plugin (math-via-engine-P/Invoke, source-format-parsing-in-C#) per [ADR 0027](0027-separation-of-concerns-invariants.md) + [RULES.md R16](../../RULES.md).

## Alternatives considered

- **Keep the C++ `IDecomposer` interface and write decomposers C++-side.** Rejected — per ADR 0027 + RULES.md R16, source-format parsing + ingest orchestration is C# work. Math is engine. Decomposers compose C# parsing with engine math via P/Invoke. C++ decomposers would force everything into engine, violating the layer-responsibility invariant.
- **Don't define `IDecomposer` at all; let each per-source project invent its own shape.** Rejected — duplication anti-pattern per STANDARDS.md + ADR 0016. Ten incompatible shapes, ten partial implementations, ten different bug surfaces.
- **Batched-only API (return `IReadOnlyList<SubstrateChange>` from `DecomposeAsync`).** Rejected — frontier-scale sources (37 GB Unicode, 34 GB Wiktionary, 125 GB DeepSeek) cannot buffer the whole intent set in RAM. Streaming `IAsyncEnumerable` is mandatory.
- **Decomposer reaches into SubstrateCRUD directly to commit each intent inline.** Rejected — couples decomposer with CRUD invocation. The streaming-yield-and-let-IngestRunner-pipe-to-CRUD shape lets orchestration insert metrics, checkpointing, parallel workers, retry logic, etc. without each decomposer reimplementing.
- **Combine `IDecomposer` and `ISource` from ADR 0011 into one interface.** Possibly — they have substantial overlap. Deferred to a future ADR; this ADR ships the `IDecomposer` shape that covers what's needed for the 10+ planned decomposers. `ISource` per ADR 0011 may end up as a thinner alias or specialization.

## References

- [RULES.md R5](../../RULES.md) — attestation idempotency
- [RULES.md R10](../../RULES.md) — polymorphic plugin architecture
- [RULES.md R16](../../RULES.md) — separation of concerns (orchestration in C#)
- [STANDARDS.md Naming conventions (C#)](../../STANDARDS.md) — `IDecomposer` follows the I-prefix convention
- [STANDARDS.md Testing](../../STANDARDS.md) — xUnit + Testcontainers
- [STANDARDS.md "Reusable helpers — DRY at every layer"](../../STANDARDS.md)
- [DESIGN.md VI — Polymorphic plugin interfaces](../../DESIGN.md) (this ADR provides the C# realization)
- [DESIGN.md II.B — Module map](../../DESIGN.md) (lists `Laplace.Decomposers.*` as planned)
- [ADR 0011](0011-polymorphic-plugin-architecture.md) — polymorphic plugin architecture (six interfaces)
- [ADR 0016](0016-reusable-helpers-discipline.md) — reusable helpers
- [ADR 0026](0026-csharp-project-structure.md) — C# project structure
- [ADR 0027](0027-separation-of-concerns-invariants.md) — separation of concerns invariants
- [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md) — layered seed ingestion (LayerOrder enforcement)
- [ADR 0041](0041-decomposer-scope-full-domain-ecosystem.md) — decomposer scope = full domain ecosystem
- [ADR 0042](0042-bootstrap-order-and-substrate-canonical-seeding.md) — bootstrap order (InitializeAsync responsibility)
- [ADR 0043](0043-composite-decomposer-architecture.md) — composite decomposer (ModelDecomposer composition)
- [ADR 0044](0044-attestation-kind-priors-and-source-trust-taxonomy.md) — source-trust-class taxonomy (TrustClassId)
- [ADR 0047 TextDecomposer](0047-text-decomposer-pure-primitive.md)
- [ADR 0048 HashComposer](0048-hash-composer-leaf-to-trunk.md)
- [ADR 0049 SubstrateChange intent type](0049-substrate-change-intent-type.md)
- [ADR 0050 SubstrateCRUD write surface](0050-substrate-crud-write-surface.md)
- [ADR 0052 Ingest pipeline orchestration](0052-ingest-pipeline-orchestration.md) — IngestRunner uses this interface
- Conversation 2026-05-24: client-does-all-work + cross-decomposer convergence via content-addressing + ON CONFLICT
EOF
