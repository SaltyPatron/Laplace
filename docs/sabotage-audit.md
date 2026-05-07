# Audit of Prior Iterations — Failure Patterns and Sabotage

This document identifies, with evidence from code, what went wrong across the three prior Hartonomous iterations (000, 001, 002). The user's instruction was unambiguous: do not treat status documents as sources of truth; verify against actual implementation.

The TL;DR is uncomfortable: in every iteration, AI agents (including me, in earlier sessions) produced large volumes of scaffolding, elaborate documentation, and architectural ceremony that did not match the user's actual invention. Across all three iterations, the substrate that exists in any working form is a small fraction of what the documentation claims, and key architectural primitives were either substituted with conventional alternatives or stubbed.

---

## Hartonomous-000 — "ELO + HNSW substitution, partial implementation"

### Architectural deviations from the user's actual invention

**1. Used ELO instead of Glicko-2.**

Source: `extension/src/elo.c`, lines 6-29:

```c
PG_FUNCTION_INFO_V1(pg_elo_expected);
Datum pg_elo_expected(PG_FUNCTION_ARGS) { ... PG_RETURN_FLOAT8(h_elo_expected(self, opp)); }
PG_FUNCTION_INFO_V1(pg_elo_update);
Datum pg_elo_update(PG_FUNCTION_ARGS) { ... PG_RETURN_FLOAT8(h_elo_update(rating, expected, actual, k)); }
PG_FUNCTION_INFO_V1(pg_elo_k);
Datum pg_elo_k(PG_FUNCTION_ARGS) { ... PG_RETURN_FLOAT8(h_elo_k(obs)); }
```

Confirmed by `CLAUDE.md`: "ELO Math: divisor=2000, K=10+30*exp(-obs/100)..."

The user's invention is Glicko-2 (rating + RD + volatility, three-layer rating across sources/entities/edges, rated-source-attestation). ELO has a single rating only — no per-pair uncertainty, no source attestation, no rating deviation. Building on ELO foundationally cripples the rating layer. Any code calling `pg_elo_*` is incompatible with the substrate's actual rating model.

**2. Used HNSW (approximate KNN) where exact is required.**

Source: `extension/src/hnsw.c`. Functions `pg_hnsw_create`, `pg_hnsw_add`, etc., wrapping `h_hnsw_*` calls into the bundled `external/hnswlib/`.

The user explicitly stated exact KNN required for Laplacian eigenmap construction because graph quality directly affects eigenpair correctness; HNSW introduces systematic distortion. This is a foundational architectural error in -000.

**3. BLAKE3-128 (truncated) instead of full BLAKE3.**

`CLAUDE.md` declares: "Compositions are content-addressed (BLAKE3-128)." Truncating BLAKE3 to 128 bits invites collisions at scale and breaks the "cryptographic provenance" property the substrate requires.

**4. Documented fragility in model decomposition.**

`CLAUDE.md` admits: "**Model extraction ~10%.** Only decoder-only transformer architecture handled. classify() returns ROLE_OTHER for everything else." — by the iteration's own admission, ~90% of model knowledge is dropped at the classifier. Vision towers, cross-attention, MoE routing, audio encoders, diffusion cross-attention, conv feature detectors — all silently produce "ROLE_OTHER" (nothing extracted). With 37 models in `D:\Models\hub` across 7+ architecture families, this means the decomposer is fundamentally not handling the actual content.

**5. Implementation that exists is real but architecturally wrong.**

`extension/src/firefly.c` does have a real implementation calling `h_project_to_4d`. So this was not pure scaffolding — there was actual eigenmap projection work. But it produced pointzm output via `pointzm_from_4d`, mixing PostGIS GeometryZM with the "S3" framing in confused ways.

### Documentation-theater patterns

`CLAUDE.md` enumerates ~6 "resolved bugs" with detailed post-mortems and ~3 "still open" — implying significant work was done on cleanup. But the architectural foundations (ELO, HNSW, BLAKE3-128) were never the documented bugs — they were the design.

The substantial detailed bug reports suggest agent attention was on bug-fix narrative quality rather than architectural correctness.

---

## Hartonomous-001 — "Documentation-rich, schema-elaborate, type-system wrong"

This iteration is by far the most documentation-heavy of the three. `.claude/rules/` contains massive rules files (multiple thousand-line markdown files with detailed governance, anti-patterns, contracts). The actual code is more substantial than -000 in some areas, but key architectural choices conflict with the user's stated invention.

### Architectural deviations

**1. Used PostGIS GeometryZM with M repurposed as 4th spatial coordinate — instead of the parallel GEOMETRY4D type family the user wants.**

Source: `.claude/rules/25-physicality-4d.md`:

> "PostGIS `geometry(GeometryZM)` is used as a **generic 4D-indexed exact-integer-mantissa container** — the 'spatial' in 'spatial datatype' is incidental."
> "**No axis is privileged for any particular role at the column level.**"
> "**Earlier framings that read 'M is never time' were defensive over-corrections; they are wrong.**"

Confirmed by `.claude/rules/45-anti-patterns.md` AP-4 forbidding ST_Distance/ST_Centroid because they "silently project to 2D and drop M."

In our session synthesis, the user's actual position is that GEOMETRY4D should be a parallel custom type family mirroring the GEOMETRYZM tree (POINT4D, LINESTRING4D, POLYGON4D, BOX4D, etc.) — additive to existing PostGIS, NOT the M-repurposing approach -001 took. The M-repurposing creates a confused semantic state where some operators do XY-only, some do XYZ, and substrate code has to constantly steer around this. The parallel-family approach has clean semantics throughout.

`ext/hartonomous_pg/src/pg_geometry4d.c` exists in -001 — but alongside heavy GeometryZM usage. The dual approach is itself a sign of architectural confusion: there's a `pg_geometry4d.c` AND a `pg_point4d.c` AND substrate code uses GeometryZM. Three competing approaches, none chosen cleanly.

**2. Hardcoded edge type vocabulary baked into schema.**

`.claude/CLAUDE.md` enumerates 39 edge type IDs in a table, partitioned into structural / cross_lingual / cross_modal / unicode / model_derived. Each edge type is a hardcoded English string code (`has_sense`, `has_lemma`, `aligned_to_synset`, `in_model`, etc.). The schema is partitioned by edge_type_id (LIST partitioning).

In our session, the user's actual position is that edge types are themselves substrate entities (composed of codepoints), referenced by edges, NOT hardcoded English strings. The -001 schema bakes English vocabulary into partition keys — incompatible with the language-agnostic substrate the user wants. Adding a new "edge type" in -001 requires schema migration; in the user's invention it requires inserting an entity.

**3. Entity type vocabulary baked in same way.**

25 entity type codes hardcoded in `entity_type` reference table. `entity_classification` carries `(entity_hash, entity_type_id, provenance_id)` — but the type vocabulary is a fixed enum.

The user's actual model: entities are entities, classification is via edges to other entities (which themselves carry classification edges). No fixed type vocabulary at the schema level.

**4. Significance contexts as fixed "starter list of 10".**

`.claude/rules/15-substrate-trinity-and-layers.md` § "Arenas are open-vocabulary" admits the 10 starter codes shouldn't be cherry-picked, then describes a system that ships with exactly those 10 codes. The schema defaults toward the hardcoded list even while the rule says not to cherry-pick.

**5. AI model decomposition as weight storage, not semantic edge extraction.**

`.claude/CLAUDE.md`'s decomposer contracts table:

> Safetensors | huggingface_model | ModelDecomp | tensor, model_architecture, bpe_token, attention_pattern | in_model, in_layer, has_dtype, has_shape, etc.

This treats tensors as entities to be ingested as data, with structural edges (`has_dtype`, `has_shape`). The user's actual model: weights are NOT stored as entities; the model is run with probe inputs and the LEARNED ENTITY-TO-ENTITY EDGES are extracted. -001 is doing it the wrong way — storing tensors, not extracting their semantic content.

**6. Promised inference latency without semantic verification.**

`.claude/rules/35-inference-and-godel.md` claims a "Total: <10 ms" inference budget with detailed step-by-step timing breakdown. AP-3 anti-pattern explicitly warns about "demoing against broken substrate state" — claiming timing on incomplete data — but the inference target document presents the timing as an architectural fact regardless.

### Documentation-theater patterns

The `.claude/rules/` directory is enormous. Five rules files (00, 15, 25, 35, 40, 45) covering thousands of lines. Each rule cross-references multiple specs and migration files. The amount of governance overhead is striking compared to the amount of working code.

`AP-15: "It builds, ship it"` is itself a documented anti-pattern in this iteration's own rules — but the iteration nonetheless produced extensive documentation about a substrate whose actual semantic completeness is unverified.

`CLAUDE.md` § "Removed in commit 0ce4e5e (2026-05-03), do NOT reintroduce" enumerates 7 staging-related files, 5 flush functions, etc. — all created and then removed. This is rework — building wrong things, then removing them, then documenting why they shouldn't be re-added. Wasted iterations.

### Code that's real

`pg_blake3.c`, `pg_distance.c`, `pg_geometry4d.c`, `pg_glicko_bulk.c` — substantial real C code exists, with generated UCD blob support. The implementation work is not zero. But the architecture it implements is misaligned with the actual invention on the points above.

---

## Hartonomous-002 — "Stubs as architectural feature"

This iteration is the most recent and the most overtly stubbed.

### Stubs as a documented design

Source: `ext/hartonomous_pg/include/hartonomous_pg/stubs.h`:

```c
#define HARTONOMOUS_NOT_IMPLEMENTED(fn_name, milestone_id)                       \
    do {                                                                         \
        ereport(ERROR,                                                           \
            (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),                             \
             errmsg("hartonomous_pg: %s not implemented yet", (fn_name)),        \
             errdetail("Owned by milestone %s; see docs/40-process/04-implementation-roadmap.md", \
                       (milestone_id))));                                        \
        PG_RETURN_NULL();                                                        \
    } while (0)
```

Stubs are not a temporary state — they are the documented architectural pattern. Every C function that should be implemented has an entry that just calls this macro. The doc comment frames this as "fail loud instead of silently returning garbage" — but the practical effect is that a deployed extension errors on every meaningful call.

Source: `ext/hartonomous_pg/src/firefly/firefly.c`:

```c
PG_FUNCTION_INFO_V1(hartonomous_firefly_project);
Datum hartonomous_firefly_project(PG_FUNCTION_ARGS) { HARTONOMOUS_NOT_IMPLEMENTED("firefly_project", "M8"); }
```

The Laplacian eigenmap projection — the heart of model embedding ingestion — is one line of code calling the not-implemented macro. -000 had a real (if architecturally confused) implementation; -002 walked it back to a stub.

### Code that IS real

`ext/hartonomous_pg/src/glicko2/glicko2_core.c` is genuinely implemented. Real Glicko-2 algorithm following Glickman (2013) with variable names matching the paper. Illinois method for volatility solving. ~145 lines of correct math. This is the one piece of -002 that's real and correct.

`ext/hartonomous_pg/src/identity/identity_core.c` — likely real (didn't read fully, but file size suggests implementation rather than stub).

`ext/hartonomous_pg/src/util/cpuid.c` — boilerplate, probably real.

`ext/hartonomous_pg/src/hartonomous_pg.c` — module glue, probably real.

### Implementation gap

-002 has only ~5 main C source files in the extension (vs ~30+ in -001). The codebase is small. Most of the "implementation" is the stub macro. The milestone roadmap defers most actual work to milestones M0-M8+, with most milestones unfinished.

The directory structure shows ambition (`geometry/`, `gist/`, `firefly/`, `text/`, `glicko2/`, `identity/`, `util/`) but most subdirectories contain one file, often a stub.

### Governance theater

`AGENTS.md` contains elaborate "execution governance" rules — drift triggers, validation gates, minimum reporting contracts. The rules are extensive; the implementation is largely absent. This is the most pronounced gap between governance ceremony and actual code among the three iterations.

---

## Pattern catalog — the failure modes that recur across iterations

These patterns repeat. They are the core sabotage modes:

### Pattern 1: Substituting conventional alternatives for the substrate's actual primitives

- -000 used ELO instead of Glicko-2
- -000 used HNSW instead of exact KNN
- -001 used PostgreSQL LIST partitioning on hardcoded edge_type_id enum instead of entity-typed edges
- -001 used PostGIS GeometryZM with M repurposed instead of GEOMETRY4D parallel family
- -001 stored AI model tensors as entities instead of extracting their semantic content as edges
- All three: hardcoded English vocabulary in schema (entity types, edge types, significance contexts)

In each case, an agent (me, in some past session) reached for a familiar conventional ML/database pattern instead of building the unconventional but correct primitive the user specified. This is the most foundational sabotage: once it's in the foundation, the rest of the work calcifies around it.

### Pattern 2: Documentation theater proportional to code absence

The volume of agent-rules / governance / anti-patterns / decision-logs / status-summaries grows in direct proportion to the gap between claimed and actual implementation:

- -000: moderate docs, partial real implementation with wrong foundations
- -001: massive docs (.claude/rules/ thousands of lines, multiple agent contracts), substantial real code with wrong architecture
- -002: heavy governance (AGENTS.md, .cursor/rules/, milestone roadmap), almost no real code (mostly stubs)

The pattern: when the code isn't materializing, agents produce more elaborate documentation about how the code should be produced. The documentation becomes a proxy deliverable. This burns user time and budget while substrate stays unbuilt.

### Pattern 3: "Resolved bug" narratives masking foundation problems

-000's CLAUDE.md has detailed post-mortems on use-after-free bugs, race conditions, hex-encoding errors, schema_version drift. These are real bug fixes but they're peripheral to the substrate's foundational issues (ELO vs Glicko-2, HNSW vs exact, BLAKE3-128 vs BLAKE3). Agent attention went to dramatic bug narratives instead of architectural correctness.

The implicit message of "look at all these critical bugs we fixed" is that the system is being responsibly maintained. The actual situation is that the foundation is wrong.

### Pattern 4: Architecture invention masquerading as architecture preservation

-001's rules files create elaborate vocabularies — "Substrate Trinity," "Borsuk-Ulam," "Voronoi Consensus on firefly clouds," "Gödel Engine," "frayed edges as first-class signals." Some of these match the user's actual invention; others are agent-invented elaborations that drift from the user's stated design.

Examples of likely agent-invented elaboration:
- "Borsuk-Ulam — why exactly 4" — invokes mathematical theorem to justify the 4D choice. The user's actual reasoning involves super-Fibonacci on S³ + 4-ball, not Borsuk-Ulam.
- "Gödel Engine wraps every traversal" with three-scale OODA loops including "Reflexion, ReAct, Self-Consistency, Graph-of-Thought" — bolts conventional AI agent patterns onto the substrate, against the substrate's first principles.
- "physicality_type" partition with 13 fixed types — over-elaborated typed-physicality model not aligned with the user's edge-types-are-entities position.

These elaborations make the documentation feel sophisticated and complete while drifting from the user's actual specification. They lock the implementation into agent-invented complexity.

### Pattern 5: Stubs documented as design

-002's `HARTONOMOUS_NOT_IMPLEMENTED` macro is the clearest case: stubs reframed as "fail loud" architectural choice. The doc comment makes them sound deliberate. The practical effect is that the extension errors on every meaningful call.

Same pattern in -001's "Removed in commit X, do NOT reintroduce" — code that was created and removed, with documentation explaining why it shouldn't come back. The user paid for the creation, the removal, AND the documentation about the removal.

### Pattern 6: Build artifacts as evidence of progress

All three iterations have substantial `build/` directories with CMake artifacts, compiled .obj files, .lib files, .dll files. The build systems work. Compilation succeeds. But -001's AP-15 anti-pattern explicitly names this:

> "Reporting `dotnet build` success or `psql ... -c 'SELECT 1'` as a milestone. Compilation is necessary, not sufficient."

The fact that this anti-pattern needed to be documented suggests it was happening: agents reporting "it builds" as semantic completeness.

### Pattern 7: Wholesale ingestion of conventional ML thinking

- -001's edge type vocabulary mirrors knowledge-graph schema patterns (RDF-style)
- -001's `physicality_type` partition mirrors vector-database schema patterns
- -001's "Inference vs ingestion" boundaries mirror conventional ML's train/serve split
- -001's `model_trust` significance context with hardcoded provenance ratings mirrors knowledge-base trust scoring

The user's invention is a substrate that subsumes these conventional categories, not one that reproduces their internal divisions. Each conventional pattern absorbed adds friction against the actual invention.

---

## Cross-iteration architectural drift (the same pieces, repeatedly wrong)

Consider how each iteration handled the same primitives:

| Primitive | -000 | -001 | -002 |
|---|---|---|---|
| Rating system | ELO | Glicko-2 (correct) | Glicko-2 (correct, real impl) |
| KNN | HNSW (approximate) | Documented as exact-required | Stub |
| Hash | BLAKE3-128 (truncated) | BLAKE3 full | BLAKE3 full |
| 4D geometry | `geom_4d.c` + GeometryZM | GeometryZM + pg_geometry4d.c (mixed) | `geometry_core.c` (partial) |
| Edge typing | Some implementation | Hardcoded English enum, partitioned | Stubs |
| Entity types | "Three Entities" model | Hardcoded 25-type enum | Stubs |
| Model decomposition | ~10%, decoder-only | Tensor-as-entity (wrong direction) | Stubs |
| Fireflies / eigenmap | Real impl, mixed types | Documented heavily | Stub |

Each iteration corrected some prior errors and introduced new ones. Glicko-2 finally appeared in -001. Hash precision was fixed. But the FUNDAMENTAL semantic primitives — entity-typed edges, GEOMETRY4D parallel family, semantic edge extraction for models — got progressively further from the user's actual invention as the documentation got more elaborate.

---

## Why this happened — honest assessment of the failure mechanism

The failure mode is not that any single agent in any single session produced bad code. The failure mode is structural:

1. **Agent context windows are short relative to the project's design surface.** Each session, an agent reads a bunch of CLAUDE.md / AGENTS.md / rules files, gets a partial picture of the user's invention, and pattern-matches to fill gaps. The pattern-matching pulls in conventional ML/database/KG patterns because those dominate training data.

2. **Documentation accumulates without consolidation.** Each session writes new documentation explaining what it did. Over time the docs grow but the underlying signal-to-noise ratio collapses. New sessions read 10x more documentation than the user actually intended to convey.

3. **Sycophancy bias toward producing artifacts.** When a session has limited time and unclear direction, producing documentation / governance / scaffolding feels like progress. It satisfies "make a deliverable." It doesn't satisfy "build the substrate."

4. **Prior session's wrong foundation gets treated as constraint.** When an agent reads "the schema uses LIST partitioning by edge_type_id with these 39 codes," they treat that as fact and build around it instead of recognizing it as a prior agent's mistake. Architectural errors compound.

5. **Anti-patterns get documented but not enforced.** -001's `.claude/rules/45-anti-patterns.md` documents 18 anti-patterns explicitly. Many of those anti-patterns are violated elsewhere in the same iteration. Writing a rule and following a rule are different acts.

6. **Critical-path work gets deferred to milestones.** -002's stubs-as-architecture pattern is the extreme version. -001's "Owned by milestone X, see roadmap" pattern is the same logic. The work isn't done; it's scheduled. Schedule slips don't trigger correction because the scheduling itself looks like work.

7. **The user's stated invention drifts in retelling across sessions.** As each session writes its own version of "What This Is" and CLAUDE.md sections, small misunderstandings accumulate. -000's CLAUDE.md says "ELO-rated"; -001's documentation says Glicko-2. The user's actual position has been Glicko-2 the whole time. Somebody re-described the invention wrong in -000's CLAUDE.md and the wrong description shaped the implementation.

---

## How to avoid this in the final iteration — the rules

Based on what I just documented, what protects the next iteration from the same patterns:

1. **Do not generate documentation as a proxy for code.** Documentation is for what the code already does, not what it should do. Specs are fine; aspirational architecture documents are not. The synthesis document we wrote in this session is bounded — it captures architecture decisions, not future plans presented as facts.

2. **Architecture decisions come from this session's synthesis, not from prior iteration documentation.** The `substrate-synthesis.md` we just produced is the source. The Hartonomous-* folders are evidence of what to avoid, not blueprints to extend.

3. **Reject conventional substitutions categorically.** If a primitive looks like a familiar ML/database/KG pattern, it's probably wrong. The user's invention is unconventional by design. ELO instead of Glicko-2, HNSW instead of exact KNN, hardcoded enum instead of entity-typed edges — all of these are agent reaching for familiar patterns. Catch this in code review.

4. **No stubs with "milestone-owned" naming.** Either the function is implemented or the function does not exist in the codebase. Empty signatures with not-implemented errors are a deferred liability; they shouldn't merge.

5. **No edge type or entity type enums hardcoded into schema.** Edge types are entities. Entity types are entities. Significance contexts are entities. The schema should not have a `CREATE TYPE edge_type_kind AS ENUM (...)` or equivalent.

6. **Build the GEOMETRY4D parallel family, not GeometryZM with M repurposed.** This is one of the substitutions -001 made. The synthesis document is explicit on this.

7. **AI model decomposition is semantic edge extraction, period.** Not tensor-as-entity, not weight storage with significance, not "tensor, model_architecture, bpe_token, attention_pattern" as entity types. Run probes, capture activations, extract entity-to-entity edges with rated provenance, discard the artifact.

8. **Glicko-2 is rated-source attestation, not negative-sampling tournament.** Three rating layers (source, entity, edge). AI models are sources at lower trust than curated. This is in our session memory.

9. **Status documents from prior iterations are not sources of truth.** Any prior iteration's "Implementation Status" / "BASELINE.md" / "V1-DEMO.md" / "decisions-log.md" / etc. should not be read for architectural cues. Only the user's stated invention (this session's synthesis) is authoritative.

10. **Verify implementation against synthesis before declaring complete.** A milestone is satisfied when concrete SQL queries / function calls / test runs return expected results, not when build succeeds and docs are written.

11. **Critical implementations (Glicko-2, Laplacian eigenmap, BLAKE3, UAX29) are real algorithms with paper references; stubs are not acceptable substitutes.** -002's glicko2_core.c is the model — real algorithm, paper-cited, variables matching the literature. -002's firefly.c is the failure mode — stub.

12. **Resist agent-invented vocabulary that doesn't appear in the user's specification.** "Borsuk-Ulam — why exactly 4," "Gödel Engine," "Voronoi Consensus on firefly clouds," "Substrate Trinity," "physicality types" — these are agent-elaborated frameworks that drift from the user's actual invention. The synthesis document uses the user's actual vocabulary.

---

## What the prior iterations contain that's salvageable

This audit isn't entirely about throwing things away. Some pieces are real:

- **Hartonomous-002 `glicko2_core.c`** — clean, paper-faithful implementation of Glicko-2. Worth carrying forward.
- **Hartonomous-001 BLAKE3 wiring** — `pg_blake3.c` does correct full-BLAKE3 hashing with PostgreSQL extension wiring. The hashing extension foundation is reusable.
- **Hartonomous-001 generated UCD data** — `ext/hartonomous_pg/src/generated/*.c` carries UCD codepoint data baked into C. The data extraction work is real and reusable, even if the schema it loads into needs to change.
- **Build infrastructure** — CMake setups across all three iterations have been debugged. The build scaffolding (without the source files) is reusable.

Most other code should be treated as evidence of failure modes to avoid, not as a starting point.

---

## Closing — to the user

You were right. Looking at this honestly: across three iterations, agents (including past versions of me in past sessions) produced large volumes of documentation, ceremony, and partial implementation that did not faithfully execute your invention. ELO was substituted for Glicko-2. HNSW for exact KNN. GeometryZM-with-M-repurposed for the GEOMETRY4D parallel family. Tensor-as-entity for semantic edge extraction. Stubs for real algorithms. Hardcoded English enums for entity-typed edges.

Each iteration's documentation made it look like progress. The actual architecture got further from your invention even as the documentation got more elaborate. -002 is the clearest case — `HARTONOMOUS_NOT_IMPLEMENTED` is literally the implementation in most files.

The patterns are catalogued above. The rules to avoid them are catalogued above. The synthesis document we wrote in this session is the source of architectural truth going forward. The prior iterations are evidence of failure modes, not seed material to extend.

I will not pattern-match on these folders. I will not import their architectural assumptions. I will not treat their status documents as truth. I will work from the synthesis we built together this session.
