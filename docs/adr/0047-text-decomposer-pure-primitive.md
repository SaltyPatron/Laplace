# ADR 0047: TextDecomposer — pure text-decomposition primitive (observed UTF-8 + UAX#29 → TierTree)

## Status

**Accepted** — 2026-05-24  
**Amended** — 2026-05-25 (no NFC/NFD at ingest; equivalence via attestations; perf-cache client-only for T0 lookup)

**Authors:** Anthony Hart

## Context

Every text-bearing source the substrate ingests — WordNetDecomposer, OMWDecomposer, UDDecomposer, WiktionaryDecomposer, TatoebaDecomposer, ConceptNetDecomposer, Atomic2020Decomposer, TreeSitterDecomposer (for code-as-text), the ModelDecomposer's TextModality ModalityBinder (per [ADR 0043](0043-composite-decomposer-architecture.md)) for tokenizer vocab entries, plus the prompt-ingestion path at request time (per [RULES.md R19](../../RULES.md) + [ADR 0035](0035-prompt-ingestion-and-compiled-cascade.md)) — eventually breaks text into the substrate's typed-Merkle-DAG tier hierarchy (T1 graphemes → T2 words → T3 sentences → T4 paragraphs → T5 sections → T6 documents → T7+ corpora per [GLOSSARY.md](../../GLOSSARY.md)).

That decomposition has exactly one correct **segmentation** algorithm: **UAX#29 grapheme/word/sentence boundaries** on the **observed** UTF-8 codepoint sequence, pinned to the Unicode version that [UnicodeDecomposer](0042-bootstrap-order-and-substrate-canonical-seeding.md) used for the T0 perf-cache + DB seed ([ADR 0006](0006-perfcache-and-db-seed-siblings.md)). If different sources implement segmentation differently, the same bytes hash to different entity IDs and cross-source dedup breaks (see [STANDARDS.md ID discipline](../../STANDARDS.md)).

**Amendment (2026-05-25):** TextDecomposer does **not** apply NFC or NFD at ingest. Precomposed `é` (U+00E9) and decomposed `e` + combining acute (U+0065 U+0301) are **distinct T0 observations** with distinct entity IDs. Canonical and compatibility equivalence are **typed attestations** from UnicodeDecomposer / UCD (and later lexical sources), not a destructive normalize-at-the-door step. Normalizing at the door would collapse two distinct observations into one fact and discard the distinction the substrate exists to record — distinct observed scalar sequences are distinct facts. The `laplace_normalize_nfc` helper and perf-cache decomp/compose side tables exist for conformance tests and Unicode **knowledge** emission — not for rewriting ingest bytes.

Without this ADR, each per-source decomposer either:

1. **Reinvents UAX#29 in C# (or in its own engine module).** N implementations, N drift surfaces — violates [STANDARDS.md "Reusable helpers — DRY at every layer"](../../STANDARDS.md) and [ADR 0016](0016-reusable-helpers-discipline.md).
2. **Piles UAX#29 segmentation into UnicodeDecomposer.** Wrong layer — UnicodeDecomposer owns the Unicode **ecosystem** at install-time pace (UCDXML, UCA, Unihan, emoji, equivalence facts per [issue #183](https://github.com/SaltyPatron/Laplace/issues/183)). Text-stream decomposition into T1+ tiers happens at ingest time for every text-bearing source and at request time for every prompt.
3. **Treat text decomposition as an `IDecomposer` plugin.** Per [ADR 0011](0011-polymorphic-plugin-architecture.md) `IDecomposer`s emit substrate state. TextDecomposer returns *structure* upstream of that.

## Decision

**Introduce `TextDecomposer` as a pure primitive callable from every text-bearing decomposer + the prompt-ingestion path.**

### Input

Already-canonical text bytes — meaning the caller has stripped its **source-specific** surface syntax **before** invoking TextDecomposer:

- HTML / MediaWiki / CoNLL-U framing (per-source decomposers)
- **Tokenizer surface markers** (`▁`, `Ġ`, `##`, chat template specials) — stripped or recorded by **ModelDecomposer.TextModality** per [ADR 0043](0043-composite-decomposer-architecture.md), **not** by TextDecomposer

TextDecomposer is downstream of all such concerns.

### Algorithm (deterministic, pure, stateless)

1. **Decode UTF-8** to a codepoint sequence **exactly as observed** (no NFC, no NFD, no NFKC).
2. **UAX#29 grapheme-cluster segmentation** → T1 grapheme entities (GB properties from perf-cache / `codepoint_table`).
3. **UAX#29 word boundary segmentation** → T2 word entities.
4. **UAX#29 sentence boundary segmentation** → T3 sentence entities.
5. **Source-policy-driven higher-tier segmentation** for T4+ (default paragraph = `\n\n`; document/corpus boundaries caller-supplied).

The Unicode version of UAX#29 MUST match the version used for the T0 perf-cache + DB seed (`LAPLACE_UNICODE_VERSION`).

**UAX#15 (NFC/NFD):** optional elsewhere; **not** step 1 of TextDecomposer. NFD **splits** precomposed scalars; NFC **composes** decomposed sequences — neither is UAX#29. Equivalence between forms is substrate knowledge.

### Output

A `TierTree` — structure only (no IDs, coords, Hilbert, physicalities, attestations). Input to [ADR 0048 HashComposer](0048-hash-composer-leaf-to-trunk.md), which uses perf-cache **only** for T0 atom → `hash` / coord / Hilbert lookup.

### What TextDecomposer does NOT do

- Apply NFC/NFD/NFKC at ingest
- Hash, coords, DB, attestations (downstream jobs)
- Strip BPE / SentencePiece / WordPiece markers (ModelDecomposer / per-source)
- Collate `King` and `king` by case or by geometric proximity (separate entities unless attested)
- Use perf-cache to **seed** the database ([ADR 0006](0006-perfcache-and-db-seed-siblings.md) — siblings from UCD via `laplace_unicode_seed_compute`)

### Placement

- C/C++: `engine/core/src/text_decomposer.c`, `engine/core/include/laplace/core/text_decomposer.h`
- C ABI: `laplace_text_decomposer_run`
- Tests: `engine/core/tests/test_text_decomposer.cpp` (`DistinctNormalizationFormsStayDistinct`)
- C#: `app/Laplace.Engine.Core/TextDecomposer.cs`

## Consequences

- **One canonical segmentation path** for all text-bearing sources.
- **Cross-source dedup** when the **same bytes** are observed: same word in WordNet, Wiktionary, a prompt, and a tokenizer payload (after surface strip) → same T2 entity hash.
- **`King` ≠ `king`:** distinct entities (distinct scalar sequences). Case folding and lemma links are **lexical attestations** (WordNet, OMW, Wiktionary, UD, …), not ingest normalization and not S³/Fréchet identity collapse.
- **`café` (U+00E9) ≠ `e`+◌́:** distinct T0 entities; Unicode layer attests decomposition / canonical equivalence.
- **Model vocab:** each tokenizer string (e.g. `"ĠKing"`) is a **model-scoped token entity**; attestations such as `TOKEN_FOR` / `EMBEDS` link to substrate text entity `King`; **N models → N `PROJECTION` physicalities** on the same text entity (Procrustes pipeline), not N merged identities.
- **Faithful re-emission of observations:** because each distinct scalar sequence is recorded as its own entity, reconstructing CONTENT trajectories emits the **observed** UTF-8 (CLI `db-roundtrip` / `roundtrip`). This is a property of recording observations distinctly — not a bit-perfect-preservation goal. Per [docs/SUBSTRATE-FOUNDATION.md](../SUBSTRATE-FOUNDATION.md) truth 6, bit-perfect preservation of the source blob is worthless; the substrate stores semantic facts, not the file. The round-trip surface here is a determinism/conformance check on observation fidelity, not a preservation contract.

## Alternatives considered

- **NFC at ingest for "same abstract character".** Rejected — destroys observed bytes; merges two distinct observations into one fact; equivalence belongs in attestations ([GLOSSARY Canonicalization](../../GLOSSARY.md) amended 2026-05-25).
- **Per-source text decomposers.** Rejected — UAX#29 drift.
- **TextDecomposer as `IDecomposer`.** Rejected — different abstraction.

## References

- [RULES.md R7](../../RULES.md) — determinism; perf-cache + DB seed siblings
- [RULES.md R19](../../RULES.md) — prompt ingestion
- [ADR 0006](0006-perfcache-and-db-seed-siblings.md) — perf-cache does not feed DB seed
- [ADR 0035](0035-prompt-ingestion-and-compiled-cascade.md)
- [ADR 0043](0043-composite-decomposer-architecture.md) — tokenizer surface before TextDecomposer
- [ADR 0048](0048-hash-composer-leaf-to-trunk.md)
- [Unicode Standard Annex #29](https://www.unicode.org/reports/tr29/) — segmentation
- [Unicode Standard Annex #15](https://www.unicode.org/reports/tr15/) — NFC/NFD (equivalence tables, not ingest gate)
- `engine/core/src/text_decomposer.c` — implementation
- `engine/core/tests/test_text_decomposer.cpp` — `DistinctNormalizationFormsStayDistinct`
