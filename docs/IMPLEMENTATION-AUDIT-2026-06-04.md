# Laplace implementation audit — 2026-06-04

Granular status of the codebase measured against the invention (CLAUDE.md +
docs/ARCHITECTURE.md). Each item: **IMPLEMENTED** (real, wired, exercised) /
**PARTIAL** (real but incomplete or unwired) / **STUB** (placeholder, returns
nothing real) / **DIVERGES** (built, but does the wrong thing vs the invention).
Every claim cites file:line or a live-DB fact. No status is asserted without
evidence.

## The invention, in five pillars (what is being audited against)

1. Ingest dissolves any source (incl. models) into content×content relations.
2. Relations accumulate into one signed Glicko-2 **consensus** per relation.
3. **Inference = indexed lookup** — ranked-μ sorted index scan; NO compute.
4. **Generation = recursive ranked-μ traversal** of stored relations (the
   model's lexical output tree), looked up, never recomputed.
5. **Export = fill a chosen mold** with consensus; never a codec / reproduction.

---

## Pillar 1 — Ingest → content×content relations

**Dataset decomposers (unicode, iso639, wordnet, omw, ud, tatoeba, atomic2020,
conceptnet, wiktionary): IMPLEMENTED + wired.** CLI `ingest <source>` routes each
through `IngestRunner` (`app/Laplace.Cli/Program.cs:356-365`). These emit real
content×content relations (is_a, synonym, translation, co-occurrence, …).

**Model ingest (the cell-ETL): PARTIAL, and DIVERGES on the central point.**
`ModelTableETL` (`app/Laplace.Decomposers.Model/ModelTableETL.cs`) runs end to end
— TinyLlama produced 153,184,256 consensus relations in `laplace-dev`, verified
per arena. BUT it emits only the **weight-factor** kinds — EMBEDS, Q/K/V/O_PROJECTS,
GATES, UP/DOWN_PROJECTS, NORMALIZES, OUTPUT_PROJECTS (`ModelTableETL.cs:96-104`,
:321). Those are token×channel, channel×neuron, channel×channel — the model's
**operational wiring**, not content×content (token→token) knowledge.
- The token→token **content** kinds the invention names — ATTENDS (QK),
  OV_RELATES (OV), COMPLETES_TO (FFN), SIMILAR_TO — are emitted by **nothing**:
  `grep AttendsKind|OvRelatesKind|CompletesToKind` finds no `AddAttestation`/emit
  site in `app/`. They exist only as kind constants and read-vocabulary.
- Consequence: the model contributes factor edges, not the `[the, capital, of] →
  [France, …]` relations. **This is the root gap** — see Pillar 3.

## Pillar 2 — Consensus accumulation

**IMPLEMENTED + wired + verified.** `ConsensusAccumulatingWriter` consumes
testimony into period partials; `materialize_period_consensus()` folds them
through the C Glicko-2 kernel (`extension/laplace_substrate/sql/13_consensus.sql.in`,
`06_glicko2.sql.in`). Live: 153M consensus rows, witness_count fan-in present,
signed μ symmetric about neutral 1500. Evidence layer is provenance-only
(no values persisted). This pillar genuinely works.

## Pillar 3 — Inference = indexed lookup (ranked-μ scan)

**The SQL primitives exist; nothing in the product calls them.**
- `top_relations`, `completions`, `consensus_out`, `consensus_in`,
  `generate_tree`, `generate_greedy` are defined in
  `extension/laplace_substrate/sql/13_consensus.sql.in`.
- C# call sites: only `consensus_out` is called, by `inspect` for **display**
  (`app/Laplace.Cli/Program.cs:263`). `generate_tree`/`generate_greedy`/
  `completions`/`top_relations` are called by **no C# code** (grep: zero hits).
- **There is no `generate`/`infer`/`chat` CLI verb** — wired verbs are ingest,
  synthesize, decompose, inspect, roundtrip, db-roundtrip, stats
  (`Program.cs:83-89`). Inference-as-a-product does not exist; it has only been
  hand-run as ad-hoc psql.

**Deeper divergence (the real one):** even if `generate_tree` were wired, the
substrate holds **factor** edges (token→channel→neuron→…), not token→token
content edges. A ranked-μ traversal over factors walks the wiring, not knowledge.
To get token→token from factors you must either materialize the bilinear densely
(the vocab² blowup, forbidden) or multiply factors at query time (the GEMM the
substrate exists to abolish, forbidden). **So lookup-inference currently has no
true content relations to look up.** Every "inference" demonstrated in session
was secretly one of those two banned bridges → noise.

**Unsolved center:** extract the finite, sparse set of meaningful token→token
relations from the composed circuits (OV/FFN) without densifying and without
query-time GEMM. No verified method exists in the codebase.

## Pillar 4 — Generation = recursive ranked-μ traversal

**STUB at the product layer; PARTIAL at the SQL layer; blocked by Pillar 3.**
- Serving: `app/Laplace.Endpoints.OpenAICompat/Program.cs:32-38` — `/v1/chat/
  completions`, `/v1/completions`, `/v1/embeddings` all return **501 Not
  Implemented**. There is no generation service.
- `generate_tree` (SQL) is the correct shape (recursive ranked-μ walk) but is
  unreachable from any product path and operates on factor edges (Pillar 3),
  so it cannot today reconstruct a lexical output tree of real completions.

## Pillar 5 — Export = fill a chosen mold (never a codec)

**DIVERGES — currently codec-shaped; the correct algorithm is not built.**
- `synthesize substrate` is wired (`Program.cs:84`, `SynthesizeFromSubstrateAsync`)
  and produces a structurally-valid GGUF (llama.cpp loaded all 201 tensors).
- But the fill path reconstructs the source's per-tensor weights from consensus
  (`ConsensusReExport.CalibratedInverse`) — that is **weight recovery / codec**,
  the banned anti-goal, not "consensus poured into a chosen shape." A run loaded
  in llama.cpp produced incoherent output; the magnitude-corrected variant was
  still per-tensor reconstruction, not the designed export.
- The invention's export — SVD-factor each consensus **circuit** into the mold's
  weights at the recipe rank, consensus-of-all-witnesses in the chosen shape —
  is **not implemented** (issue #272 llama_gguf_export, #231 ADR 0056 are open).

---

## Bottom line

- **Real and working:** dataset ingest, content-addressing, the consensus
  engine, evidence-as-provenance, the SQL read primitives as functions, the
  ingest plumbing at scale.
- **The product gap:** there is no wired inference or generation path (501 stub /
  unwired SQL), and the serving layer does nothing.
- **The conceptual gap (root cause):** model ingest stores weight **factors**,
  not the content→completion **relations** that lookup-generation requires; the
  finite extraction that would bridge them is unsolved; and export is currently
  weight-reconstruction (codec) rather than mold-fill from consensus circuits.

Until the model ingest emits content×content relations (or a verified
finite-extraction method exists), the lookup-based inference and the non-codec
export cannot be true — they have no correct relations to read or pour.

## Open issues that encode this work (GitHub, SaltyPatron/Laplace)

- #231 ADR 0056 — weight-tensor static ETL as arena-matchup observation (acceptance)
- #272 / #273 — engine `llama_gguf_export` (correct export path; replaces the C# codec)
- #222 / #221 — MoE / architecture-family attestation aggregation
- #207 / #230 — prompt decomposition + prompt-local scoping (inference input side)
- #259 epic — push reinventions to engine; C# = orchestration only
