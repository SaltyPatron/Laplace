---
applyTo: "{app/Laplace.Decomposers.*/**,app/Laplace.Ingestion/**,engine/core/**}"
description: "Use when touching decomposers, corpus ingest, witness adapters, or text/content pipelines."
---

# Laplace Ingest Witness Rules

Corpus ingest deposes testimony into substrate. Parsing is witness extraction, not domain modeling in C#.

## Default pipeline: grammar compose (tree-sitter)

Every delimited vault corpus (TSV, CSV, JSONL, paired-line) uses `StructuredGrammarIngest`:

1. `laplace_grammar_row_iter` — newline-delimited rows, chunked file read (engine)
2. `tree-sitter` modality recipe (`tsv`, `csv`, `json`, …) — parse each row to a Laplace AST
3. `laplace_grammar_compose` — deposit constituents as entities/physicalities/trajectories (same kernel as code)
4. Thin `IGrammarWitness.WalkRow` — semantic attestations over **composed spans** via `GrammarRowComposer.TrySpanEntity`

Code repos use the same machinery directly (`GrammarEntityBuilder` / tags.scm). JSONL is not special — each line is a `json` document.

The `*FastIngest` helpers are **deprecated corner-cuts**: they skip constituent compose and re-emit strings via `ContentEmitter`. Do not extend them. Migrate callers to `StructuredGrammarIngest` and delete the FastIngest file when parity is proven.

## Semantic witnesses

- Never `ContentEmitter.Emit` / `JsonElement` DOM in corpus inner loops for attestation objects.
- Attestation objects must be composed constituents (`TrySpanEntity`) or registry-resolved entities (language, POS).
- POS tags and relation types: `PosReference.Attest`, `RelationTypeRegistry` — not hand-built content roots.
- Free text (books, user prompts): `TextDecomposer` (UAX#29 tier tree), not tree-sitter.

## Native boundary

- Grammar parse, compose, tier trees, trajectories → `laplace_core`
- Unicode UCD → `unicode_seed` / perfcache (not C# `UcdProperties.Load` at scale)
- Intent staging binary → native `intent_stage`; C# passes pinned structs on hot paths

## Per-decomposer expectations

| Source | Modality | Pattern |
|--------|----------|---------|
| ConceptNet, Tatoeba, OMW, Atomic2020 | `tsv` | `StructuredGrammarIngest` + `IGrammarWitness` (migrate from FastIngest) |
| Wiktionary | `json` | `StructuredGrammarIngest` + `WiktionaryGrammarWitness` |
| OpenSubtitles | `tsv` or paired-line | `StructuredGrammarIngest` (migrate) |
| WordNet, FrameNet, PropBank, VerbNet | XML | XML witness + compose for text fields; no per-node C# row materialization |
| Unicode | native | `unicode_seed` emit batches |
| Model safetensors | tensor | `ModelTableETL` + native gather |
| Code repos | `python`, `c`, … | `GrammarEntityBuilder` + tags.scm |

## Tests

Each migration from FastIngest requires a parity fixture on a small corpus sample: same attestation counts and sampled composed ids as the grammar path.
