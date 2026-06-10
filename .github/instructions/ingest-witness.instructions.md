---
applyTo: "{app/Laplace.Decomposers.*/**,app/Laplace.Ingestion/**,engine/core/**}"
description: "Use when touching decomposers, corpus ingest, witness adapters, or text/content pipelines."
---

# Laplace Ingest Witness Rules

Corpus ingest deposes testimony into substrate. Parsing is witness extraction, not domain modeling in C#.

## Default pipeline: fast ingest

For TSV, JSONL, CSV, and paired-line corpora, use the fast-ingest pattern:

1. `StreamingUtf8LineReader` — chunked UTF-8, zero-copy or rented buffers (no per-line `new byte[]`)
2. Span/byte field extraction (`TsvSpan`, `*RowFilter`, `Utf8JsonReader`)
3. Witness class walks rows → `SubstrateChangeBuilder` (attestations, entity refs)
4. `ContentWitnessBatch` or memoized content root IDs — not full `ContentEmitter` per row

Reference implementations: `ConceptNetFastIngest`, `TatoebaFastIngest`, `WiktionaryFastIngest`.

## Grammar compose path (restricted)

Use `StructuredGrammarIngest` / `GrammarRowComposer` only when tree-sitter semantics are required:

- Source code repos (`RepoDecomposer`, tree-sitter grammars)
- Formats where row structure is genuinely syntactic, not tabular

Do not route TSV/JSONL/CoNLL-U through grammar compose. Mark grammar-only entry points `[Obsolete]` for tabular sources.

## Content witnesses

- `ContentEmitter` is for low-volume or test paths. Corpus inner loops must use `ContentWitnessBatch` (native → `IntentStage`) or content-root memo keyed by `(sourceId, blake3(utf8))`.
- `TextEntityBuilder.TryBuildRows` is grammar-path and test-only on hot ingest paths.
- Never call `Encoding.UTF8.GetBytes` per row when the row is already UTF-8 bytes.

## Native boundary

- Text decomposition, hash composition, tier trees, trajectories → `laplace_core`
- Unicode UCD property tables and codepoint emit → `unicode_seed` / native perfcache (not C# `UcdProperties.Load` at scale)
- Intent staging binary → `intent_stage` in native; C# passes pinned structs, not reconstructed row lists on hot paths

## Per-decomposer expectations

| Source | Pattern |
|--------|---------|
| ConceptNet, Tatoeba, Wiktionary | Fast ingest (done) |
| OpenSubtitles, UD, OMW, WordNet | Fast ingest (migrate) |
| FrameNet, PropBank, VerbNet | XML witness + content batch; no per-node grammar compose |
| Unicode | Native UCD emit batches |
| Model safetensors | `ModelTableETL` + native gather/matmul; no C# tensor math |
| Code repos | Grammar compose (tree-sitter) |

## Tests

Each fast-ingest migration requires a parity fixture test against the legacy path on a small corpus sample.
