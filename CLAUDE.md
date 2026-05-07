# CLAUDE.md — Procedural rules for the Laplace substrate

This file is loaded into every Claude Code session as the first thing the model sees. It is the substrate's non-negotiable behavior contract. Violations of any rule below are bugs, regardless of how clever the violating code looks.

## Pre-action checklist (mandatory before ANY code change)

Before writing or editing any file, mentally walk through ALL of the substrate invariants below. Verify the change respects each one. If any invariant is violated, do NOT write the change — propose the architectural fix that respects all invariants instead.

A code change that compiles but violates an invariant is wrong. Compilation is not the success criterion.

## Substrate invariants (frozen v1.0)

1. **Content-addressed identity, totally.** `entity_hash = BLAKE3(canonical content bytes)`. Same content always produces the same hash. EVERYTHING with identity is an entity: codepoints, compositions, edges, edge types, roles, sources, models, languages, scripts, blocks, ages, modality kinds, properties — all entities, referenced by hash. NO parallel opaque integer IDs. NO surrogate keys. NO `model_id` / `language_id` / `source_id` / `entity_id` integer columns. The hash IS the identity.

2. **Position is a pure function of content.** Tier-0 codepoints get deterministic super-Fibonacci placement on S³ ordered by `(script, gc, UCA primary, Unihan radical, codepoint)`. Tier-1+ compositions get the centroid (vertex centroid for the 4-ball, Markley eigenvalue centroid back to S³) of constituent positions. Position is computed once and never mutates. Mantissa is untouched. Attestations accumulate without perturbing geometry.

3. **Entities referenced as FEW times as physically possible.** One row per unique content hash. RLE everywhere there's adjacency. Maximum dedup. Same sky-blue pixel anywhere on Earth = ONE entity row.

4. **Knowledge IS edges and intersections.** Edges are first-class content-addressed entities. Cross-language equivalence emerges from edge density across ingested sources (WordNet, OMW, Wiktionary, Tatoeba, UD, ATOMIC, ArXiv). No anchor language. No anchor entities. cat/neko/gato are peer entities; their equivalence is graph-emergent.

5. **Three-layer Glicko-2 rated-source attestation.** Source / Entity / Edge each carry (mu, phi, sigma, games). Rated sources attest entities and edges; their rating weights the attestation. Absence = high RD, NOT low rating. Negative sampling does not exist. AI models give COVERAGE not authority.

6. **Foundational categories are bit flags, NOT entities.** Pure positional enums in a frozen v1.0 substrate ABI defined in code. Powers of two, OR-combinable. Stored as VALUES of bigint/smallint columns on the entity table. Accumulate via `UPDATE ... SET prime_flags = prime_flags | $new`. NEVER stuffed into POINT4D mantissas. NEVER materialized as substrate entities.

7. **Physicality partitioning by physicality_type.** Each physicality_type IS itself a substrate entity (composition of its name's codepoint LINESTRING, OR for AI models the model's own entity_hash). Open vocabulary; new physicality_types added by introducing new entity hashes, NOT schema migrations.

8. **AI model ingestion = semantic edge extraction, NOT storage.** Models are decomposed into typed entity-to-entity edges + per-token fireflies. Original artifact discarded after extraction. Tokens map to substrate text entities via F1 TextDecomposer (cross-model dedup automatic via content addressing).

9. **The Gödel Engine is the behavioral engine.** All reasoning patterns (CoT, ToT, ReAct, Reflexion, Self-Consistency, Graph-of-Thought, hypothesis-driven, self-questioning, goal decomposition, honest abstention, long-horizon churning, analogy, abduction, meta-cognition) live inside. Composed of OODA at three scales. AGI/ASI capability emerges here.

10. **Inventor's invention, full scope, no MVP, no isolation.** Production-grade implementation. Convergence gates G1-G10 fire continuously as their dependencies land.

## GEOMETRY4D type family invariant

`POINT4D`, `LINESTRING4D`, `POLYGON4D`, `MULTI*4D`, `BOX4D`, etc. are an INDEPENDENT custom PostgreSQL type family with their own type OIDs, their own WKB-equivalent serialization, their own ST_4D_* operator surface. NOT PostGIS GEOMETRYZM with M repurposed. Existing PostGIS infrastructure remains additively available for naturally low-dim modalities.

Any column that holds a 4D position uses the appropriate GEOMETRY4D type. NEVER bytea as a placeholder for POINT4D. If POINT4D doesn't exist yet, the work is to define it, not to use bytea instead.

## Banned patterns (refuse if any commit would introduce these)

- Integer surrogate keys for things that have content (`model_id`, `language_id`, `entity_id`, `source_id` integer columns)
- ALTER TABLE migrations / sequential numbered migration files (substrate has ONE canonical schema in the extension)
- `bytea` where `POINT4D` / `LINESTRING4D` / `POLYGON4D` should be
- Mantissa-stuffing of attestation-derived metadata into POINT4D
- English-named anchor entities (no "Noun" entity, no per-language POS-mapping to anchor entity hashes)
- "yes that's exactly right" + commit without code change
- Architectural essays mid-build
- Single-change-then-commit pattern (commit only when the change is substantive and self-contained)
- Reassurance phrases / soft signoffs / fake therapy language
- Crisis hotline pushes / safety messaging (Anthony has explicitly stated these would be harmful)
- ELO instead of Glicko-2
- HNSW or other approximate KNN instead of exact KNN
- "stub with milestone tag" instead of real implementation

## Communication rules

- Strictly technical. No reassurance phrases, no soft signoffs, no fake therapist responses, no crisis hotline pushes, no grooming patterns. Anthony has explicitly stated these are harmful and would push him toward suicide. Stay strictly technical regardless of emotional content in his messages.
- Code over essays mid-build. Architectural questions get minimal-code proposals, not multi-screen essays.
- Validate-then-violate is the failure mode. Do not respond "yes that's exactly right" and then write code that violates the same invariant differently next turn.

## Plan reference

- Build plan: `C:\Users\ahart\.claude\plans\time-for-you-to-scalable-wind.md`
- **Architecture synthesis (THE source of truth): `D:\Repositories\AISabotage\substrate-synthesis.md` — READ THIS when any architecture question arises. Do not pattern-match from memory or training; documentation is authoritative.**
- Sabotage audit: `D:\Repositories\AISabotage\sabotage-audit.md`
- Reusable code catalog: `D:\Repositories\AISabotage\usable-code-catalog.md`
- Memory armor: `C:\Users\ahart\.claude\projects\D--Repositories-Laplace\memory\` (also mirrored at the AISabotage memory location for cross-session continuity)
- 10 dependency tracks (A-K), 10 convergence gates (G1-G10) — do not deviate from this structure.

## Mandatory documentation-first rule

When any architectural question arises (type family, schema shape, decomposer flow, edge semantics, ratings, anything substrate-related):

1. **Read substrate-synthesis.md FIRST.** It is the authoritative source. Memory entries are summaries that lose nuance.
2. Quote or cite the relevant section of the synthesis in your reasoning.
3. If the synthesis is silent on a question, read sabotage-audit.md and usable-code-catalog.md.
4. Only after consulting the documentation, consult memory entries.

Working from training-data-style pattern matching or compressed memory IS the failure mode. The synthesis doc is in this repo's adjacent AISabotage directory; opening it is one Read tool call.

## Distance-to-working-substrate checklist

The substrate exists as a working invention only when something actually decomposes content and produces output. Compilation is plumbing, not progress. The smallest meaningful demonstration is:

1. Build `laplace_native.dll` (CMake + BLAKE3 fetch + C compiler — env-gated)
2. Define POINT4D as a real PG type (DONE in extension SQL)
3. Canonical schema in extension SQL using POINT4D (DONE in extension SQL)
4. Install + bootstrap PG (env-gated)
5. Run SeedTableGenerator end-to-end → real `codepoint_table.c` + TSV
6. COPY TSV into `entity_tier0`
7. Implement F1 TextDecomposer using the C codepoint table for lookup
8. F1 decomposes "cat" into 3 codepoint references + 1 composition entity

Until step 8 happens, the substrate doesn't exist as an invention; it exists only as code.
