# ADR 0047: TextDecomposer — pure text-decomposition primitive (NFC + UAX#29 → TierTree)

## Status

**Proposed** — 2026-05-24
**Authors:** Anthony Hart

## Context

Every text-bearing source the substrate ingests — WordNetDecomposer, OMWDecomposer, UDDecomposer, WiktionaryDecomposer, TatoebaDecomposer, ConceptNetDecomposer, Atomic2020Decomposer, TreeSitterDecomposer (for code-as-text), the ModelDecomposer's TextModality ModalityBinder (per [ADR 0043](0043-composite-decomposer-architecture.md)) for tokenizer vocab entries, plus the prompt-ingestion path at request time (per [RULES.md R19](../../RULES.md) + [ADR 0035](0035-prompt-ingestion-and-compiled-cascade.md)) — eventually breaks text into the substrate's typed-Merkle-DAG tier hierarchy (T1 graphemes → T2 words → T3 sentences → T4 paragraphs → T5 sections → T6 documents → T7+ corpora per [GLOSSARY.md](../../GLOSSARY.md)).

That decomposition has exactly one correct algorithm: **NFC normalization + UAX#29 grapheme/word/sentence segmentation**, pinned to the Unicode version that [UnicodeDecomposer](0042-bootstrap-order-and-substrate-canonical-seeding.md) used to produce the T0 perfcache + DB seed. If different sources implement it differently, the same text content hashes to two different entity IDs, cross-source deduplication breaks, and the substrate's whole consensus story collapses (see [STANDARDS.md ID discipline](../../STANDARDS.md) — content-addressing requires deterministic canonicalization).

Without this ADR, each per-source decomposer either:

1. **Reinvents NFC + UAX#29 in C# (or in its own engine module).** N implementations, N drift surfaces, N bugs to fix in N places — directly violating [STANDARDS.md "Reusable helpers — DRY at every layer"](../../STANDARDS.md) ("every operation used more than once must be a named, tested, single-source-of-truth helper") and [ADR 0016](0016-reusable-helpers-discipline.md).
2. **Pile UAX#29 segmentation into UnicodeDecomposer.** Wrong layer — UnicodeDecomposer owns the Unicode ecosystem (UCDXML, UCA, Unihan, emoji, auxiliary segmentation property tables per [issue #183](https://github.com/SaltyPatron/Laplace/issues/183)) at install-time pace, producing T0 atoms + their substrate-canonical CONTENT physicalities. Text-stream decomposition into T1+ tiers happens at ingest time for every text-bearing source and at request time for every prompt — completely different invocation frequency, completely different caller set.
3. **Treat text decomposition as an `IDecomposer` plugin.** Per [ADR 0011](0011-polymorphic-plugin-architecture.md) `IDecomposer`s emit substrate state (entities + physicalities + attestations). TextDecomposer is upstream of substrate state emission: it returns a *structure* that callers use, then callers compose that structure into substrate state. Mixing the two collapses the source-specific layer (which knows WordNet's IS_HYPERNYM_OF vs Wiktionary's HAS_DEFINITION) into the shared text-segmentation primitive (which knows nothing about either).

The 2026-05-24 conversation that surfaced this ADR also clarified two further constraints:

- **Same content = same hash regardless of who processed it** ([conversation excerpt: "The TextDecomposer should do nothing but break down text in a deterministic predictable way..."]). The primitive is pure: same input bytes (post-source-stripping) → identical TierTree structure → identical hashes once HashComposer ([ADR 0048](0048-hash-composer-leaf-to-trunk.md)) runs over it.
- **Decomposition is trunk-to-leaf; hash composition is leaf-to-trunk; dedup is trunk-to-leaf** ([conversation excerpt: "text decomposition should be from trunk to leaf client-side..."]). TextDecomposer owns the first phase (trunk-to-leaf parsing). It produces only structure — no IDs, no coords, no DB interaction — because hashing requires bottom-up composition that's a separate concern.

## Decision

**Introduce `TextDecomposer` as a pure primitive callable from every text-bearing decomposer + the prompt-ingestion path.** Its contract:

### Input

Already-canonical text bytes — meaning the caller has stripped its source-specific markers (HTML tags, MediaWiki markup, BPE leading-space markers like `▁` / `Ġ` / `##`, XML entities, etc.) BEFORE invoking TextDecomposer. The caller's source-specific pre-canonicalization is documented in that caller's per-source ADR (e.g., WiktionaryDecomposer's wiki-markup stripping, ModelDecomposer.TextModality's BPE marker stripping); TextDecomposer is downstream of all such source-specific concerns.

### Algorithm (deterministic, pure, stateless)

1. **NFC normalization** per [GLOSSARY.md Canonicalization](../../GLOSSARY.md) ("Text: NFC-normalized Unicode codepoint sequence (UTF-8 vs UTF-16 of identical codepoints → same ID)").
2. **UAX#29 grapheme-cluster segmentation** → T1 grapheme entities.
3. **UAX#29 word boundary segmentation** → T2 word entities.
4. **UAX#29 sentence boundary segmentation** → T3 sentence entities.
5. **Source-policy-driven higher-tier segmentation** for T4 paragraphs / T5 sections / T6 documents / T7+ corpora. Default rule: paragraph = sentences separated by `\n\n`; section/document/corpus boundaries are caller-supplied because they're not derivable from text content alone (a 1000-word string could be one document or 1000 single-word documents depending on the source's framing). Caller passes higher-tier boundaries explicitly when known; TextDecomposer threads them through the TierTree without inventing structure.

The Unicode version of the UAX#29 segmentation algorithm MUST match the Unicode version of the perfcache + DB seed produced by UnicodeDecomposer. Cross-version drift breaks the determinism contract per [RULES.md R7](../../RULES.md) ("Perf-cache and DB seed are sibling artifacts, both derived independently from Unicode UCD") and the broader cross-machine reproducibility mandate. This is enforced by a single `LAPLACE_UNICODE_VERSION` build-time constant consulted by both UnicodeDecomposer and TextDecomposer.

### Output

A `TierTree` — a structural representation of the tier hierarchy with parent/child links per tier, **but no IDs, no coords, no Hilbert indices, no physicalities, no attestations**. The leaves carry codepoint values; intermediate nodes carry tier + text-range references. The trunk is the input text as a single highest-tier-bound entity (typically T3 sentence or T6 document depending on what the caller asked for).

The `TierTree` is the input to [ADR 0048 HashComposer](0048-hash-composer-leaf-to-trunk.md), which populates IDs / coords / Hilbert leaf-to-trunk using engine kernels + perfcache lookups for T0.

### What TextDecomposer does NOT do

- Hash anything (HashComposer's job per [ADR 0048](0048-hash-composer-leaf-to-trunk.md))
- Compute coords or Hilbert indices (HashComposer's job)
- Touch the database (`SubstrateCRUD`'s job per [ADR 0050](0050-substrate-crud-write-surface.md))
- Emit attestations (per-source decomposer's job)
- Strip source-specific markers (per-source decomposer's job, runs BEFORE TextDecomposer)
- Know about any source format (decoupled from caller identity)
- Maintain state across calls (pure function — same input always produces same output)
- Have side effects beyond returning the TierTree

### Placement

- **Algorithm implementation in C/C++** under `engine/core/src/text_decomposer.{c,cpp}` + header at `engine/core/include/laplace/core/text_decomposer.h`. C ABI surface per [RULES.md R14](../../RULES.md). Loaded by the PG extension (for prompt-ingest-from-SQL-side via the compiled cascade SRF per [ADR 0035](0035-prompt-ingestion-and-compiled-cascade.md)) AND by the C# orchestration layer (for ingestion + CLI + endpoint plugins) via P/Invoke per [ADR 0026](0026-csharp-project-structure.md).
- **C ABI shape**: `text_decomposer_decompose(const char* utf8_text, size_t len, text_tier_tree_t** out_tree)`. Opaque `text_tier_tree_t*` handle per [RULES.md R22](../../RULES.md) (don't expose Eigen / internal C++ types across the boundary).
- **Test coverage**: GoogleTest under `engine/core/tests/test_text_decomposer.cpp` per [STANDARDS.md Testing](../../STANDARDS.md). Cross-language consistency test (same input through the SQL wrapper and through the C# P/Invoke binding must produce byte-identical TierTree representations).
- **C# binding**: `Laplace.Engine.Core` per ADR 0026.

### Tier promotion rules

| Tier | Default boundary | Source-overridable? |
|---|---|---|
| T0 codepoint | UTF-32 codepoint | NO (substrate invariant) |
| T1 grapheme | UAX#29 grapheme cluster | NO (UAX#29 is the standard) |
| T2 word | UAX#29 word boundary | NO (UAX#29 is the standard) |
| T3 sentence | UAX#29 sentence boundary | NO (UAX#29 is the standard) |
| T4 paragraph | `\n\n` (two newlines) | YES (caller can pass alternate paragraph delimiters) |
| T5 section | caller-supplied (e.g., markdown `## ` headers) | YES |
| T6 document | caller-supplied (one whole input is one document by default) | YES |
| T7+ corpus | caller-supplied (group of documents) | YES |

T0–T3 are fixed by Unicode standards; T4+ involve source-policy because text alone doesn't carry section/document/corpus semantics.

## Consequences

- **One canonical text-decomposition path** for the whole substrate. Bug fix in TextDecomposer applies uniformly to WordNet ingest, prompt processing, model-tokenizer ingest, and every other text-bearing path. No drift across sources.
- **Cross-source dedup works by construction.** The token `walk` from Qwen3's vocabulary, the lemma `walk` from WordNet's `data.verb`, the word `walk` from a Wiktionary definition, the word `walk` in a user's prompt — all produce identical T2 word entities because they go through the same NFC + UAX#29 pipeline. Cross-source consensus on shared text falls out automatically.
- **Source-specific work stays in source-specific decomposers.** Wiki-markup stripping is WiktionaryDecomposer's concern, not TextDecomposer's. BPE marker stripping is ModelDecomposer.TextModality's concern. The N decomposers can evolve independently without touching the shared text primitive.
- **Determinism contract locked.** Same Unicode version + same NFC + same UAX#29 rules → byte-identical TierTree on any machine, any caller, any sequence. The `LAPLACE_UNICODE_VERSION` build constant pins this across the perfcache, the DB seed, and the runtime text decomposition.
- **Forces an upstream library choice.** ICU is the obvious candidate (per [STANDARDS.md](../../STANDARDS.md) approved libraries it's already listed for UCA collation runtime), and ICU implements both NFC and UAX#29. Alternatively we could write our own UAX#29 implementation (small — a few hundred lines per the UAX#29 spec) to avoid the ICU runtime dep at the cost of having our own UAX#29 to maintain. Decision deferred to implementation Story but documented here as the open call.
- **Tier promotion rules need source-policy plumbing.** T4–T7+ boundaries vary per source; the `TierTree` representation needs slots for caller-supplied higher-tier boundaries. Adding a new source-defined tier (e.g., T8 collection) doesn't require TextDecomposer changes — just caller plumbing.
- **No PG-side compute** per [RULES.md R6](../../RULES.md). TextDecomposer is called by the C# orchestration layer for ingestion + by the cascade SRF (C/C++ in-process inside PG) for prompt decomposition; PG itself never invokes anything beyond the engine boundary.

## Alternatives considered

- **One per-source text decomposer per source plugin.** Rejected — produces N implementations of NFC + UAX#29 with inevitable drift. Violates STANDARDS.md DRY mandate + ADR 0016. Breaks cross-source dedup as soon as one implementation rounds a corner the others don't.
- **Pile text decomposition into UnicodeDecomposer.** Rejected — wrong layer. UnicodeDecomposer is install-time, owns T0 atoms + the Unicode ecosystem. TextDecomposer is per-ingest-call + per-prompt-call, owns T1+ segmentation. Different cadence, different caller set, different scope.
- **TextDecomposer as an `IDecomposer` plugin per ADR 0011.** Rejected — `IDecomposer`s emit substrate state (entities + physicalities + attestations). TextDecomposer returns *structure* that callers compose into substrate state. The two are different abstractions; conflating them sacrifices the per-source layer's source-specific responsibilities. (Per-source decomposers ARE `IDecomposer`s and use TextDecomposer as a primitive.)
- **Decompose + hash + insert in one pass.** Rejected — couples three distinct concerns. Per the 2026-05-24 conversation, the ingest pipeline factors into three pure stages with three different traversal directions (trunk→leaf decomposition / leaf→trunk hash composition / trunk→leaf dedup), each composable independently. Single-pass implementation would make optimizing any one stage (e.g., adding a local LRU existence cache to the dedup walk) impossible without rewriting all three.

## References

- [RULES.md R5](../../RULES.md) — attestation idempotency (affects how decomposers using TextDecomposer compose with `SubstrateCRUD`)
- [RULES.md R6](../../RULES.md) — DB as dumb columnar store; text decomposition is NOT PG-side compute
- [RULES.md R7](../../RULES.md) — determinism by construction (the Unicode-version-pinning invariant)
- [RULES.md R10](../../RULES.md) — polymorphic plugin architecture (TextDecomposer is upstream of `IDecomposer`s, not one itself)
- [RULES.md R14](../../RULES.md) — C ABI at engine boundaries
- [RULES.md R16](../../RULES.md) — separation of concerns (math in C/C++, orchestration in C#/SQL)
- [RULES.md R19](../../RULES.md) — prompt is ingestion (uses TextDecomposer at request time)
- [RULES.md R22](../../RULES.md) — use existing types
- [STANDARDS.md "Reusable helpers — DRY at every layer"](../../STANDARDS.md)
- [STANDARDS.md ID discipline](../../STANDARDS.md) — content-addressing requires deterministic canonicalization
- [STANDARDS.md Cross-language consistency](../../STANDARDS.md)
- [GLOSSARY.md Canonicalization](../../GLOSSARY.md)
- [GLOSSARY.md Universal T0](../../GLOSSARY.md)
- [GLOSSARY.md Tier](../../GLOSSARY.md)
- [ADR 0011](0011-polymorphic-plugin-architecture.md) — polymorphic plugin architecture
- [ADR 0015](0015-blake3-for-entity-hashing.md) — BLAKE3-128
- [ADR 0016](0016-reusable-helpers-discipline.md) — reusable helpers
- [ADR 0024](0024-engine-modularization.md) — engine modularization (placement in `liblaplace_core`)
- [ADR 0026](0026-csharp-project-structure.md) — C# project structure (P/Invoke binding)
- [ADR 0035](0035-prompt-ingestion-and-compiled-cascade.md) — prompt ingestion + compiled cascade
- [ADR 0037](0037-layered-seed-ingestion-and-model-codec-fidelity.md) — layered seed ingestion
- [ADR 0040](0040-multi-modal-entity-types-universal-t0.md) — multi-modal entity types + universal T0
- [ADR 0041](0041-decomposer-scope-full-domain-ecosystem.md) — decomposer scope
- [ADR 0042](0042-bootstrap-order-and-substrate-canonical-seeding.md) — bootstrap order (Stage 5 perfcache + T0 seed)
- [ADR 0043](0043-composite-decomposer-architecture.md) — composite decomposer (ModelDecomposer.TextModality uses TextDecomposer)
- [ADR 0048 HashComposer](0048-hash-composer-leaf-to-trunk.md) — consumes TextDecomposer's TierTree
- [ADR 0049 SubstrateChange intent type](0049-substrate-change-intent-type.md) — what decomposers ultimately emit
- [ADR 0050 SubstrateCRUD write surface](0050-substrate-crud-write-surface.md) — consumes `SubstrateChange` intents
- [Unicode Standard Annex #29](https://www.unicode.org/reports/tr29/) — UAX#29 text segmentation
- [Unicode Standard Annex #15](https://www.unicode.org/reports/tr15/) — UAX#15 NFC normalization
- Issue #183 — UnicodeDecomposer (Layer 1) — established Unicode-version pinning
- Conversation 2026-05-24: TextDecomposer SRP clarification ("TextDecomposer should do nothing but break down text"); three-direction ingest pipeline clarification.
