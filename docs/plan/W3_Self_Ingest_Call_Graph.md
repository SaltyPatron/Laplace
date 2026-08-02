# W3 — The substrate reads its own source (the call graph as evidence)

**Issue:** #765 · **Plan:** `COMPLETION_PLAN.md` R0 / Phase 1 item 0 ·
**Enables:** #758 (G4 in its correct form) · **Complement:** #764 (BEGIN ATOMIC)

---

## 1. Why this exists

The repo's one recurring failure mode, named by spec 37 §0:

> *An operation gets a canonical implementation. The orchestrator that should
> call it is never rewired. Both survive. They drift.*

Every instance found in the 2026-08-02 audit — `converse_compose` (installed,
zero callers), `converse_tiered` (perf-fixed in `main`, unwired behind a stale
comment), `walk_branches(p_topic_bias)` (zero callers repo-wide),
`prompts_smoke.txt` (no runner), `DocumentRouter` (tested, no production
caller) — **was found by grep, because grep was the only instrument available.**

Spec 37 §7 proposes G4 (dead-canonical gate) as the mechanical kill. The
question this document answers is *what G4 should be*, and the answer is not a
grep and not a catalog query.

**A grep gate string-matches source.** It renders and compares text inside the
very check meant to enforce L1 (*hash space until S8*), it cannot see dynamic
dispatch, it is fooled by comments, and it must be maintained separately from
the code it polices.

**A `pg_depend` catalog query is better and still partial.** It sees only
*installed* objects — never the `.sql.in` templates that are the schema of
record, never C, never C#. And today it sees almost nothing anyway: of the
`laplace` schema's functions (247 `LANGUAGE sql`, 32 plpgsql, 77 C), **zero**
have parsed bodies, and the whole schema carries **9** dependency edges, all
from its 9 views (measured 2026-08-02).

**The substrate's own answer:** ingest the source; a dead canonical is a
function id with **zero incoming `CALLS` edges**. One indexed read in id space,
perfcacheable per spec 33, rendering only the final list of names. And because
it is consensus rather than a static assertion, a false positive is
**refutable** — it folds like any other testimony.

## 2. How it works today — measured 2026-08-02

The capability is declared and does not function. Two stacked failures.

### 2.1 `.sql.in` files are invisible to the code lane

```
laplace ingest code extension/laplace_substrate/sql/functions/converse   (44 files)
→ INGEST_START source=CodeDecomposer layer=2 unit_type=units input_units=0 files=0
```

Cause: `CodeDecomposer.ModalityOf` (`app/Laplace.Decomposers/Code/CodeDecomposer.cs:58-64`)
uses `Path.GetExtension`, which returns `.in` for `chat.sql.in`. The extension
resolver (`GrammarDecomposer.ModalityByExt` → native
`grammar_modality_by_ext`) has no `in` mapping, so `ModalityOf` returns null and
`EnumerateCodeFiles` (`:40-56`) skips every file.

The grammar itself is present and registered: `tree_sitter_sql` at
`engine/core/src/grammar_registry.c:74`, extension map `{"sql","sql"},
{"ddl","sql"}, {"dml","sql"}` at `:132`.

**Code-level correction (2026-08-02):** `ModalityOf` now strips the recognized
templating suffix and re-resolves the preceding extension, so `.sql.in` reaches
the existing SQL grammar without globally mapping arbitrary `.in` files to SQL.
`CodeDecomposerTests` pins plain, templated, mixed-case, unknown and repeated
suffixes. The live `input_units > 0` check remains owed after the empty
substrate is seeded; §2.2 remains the structural blocker even when files parse.

### 2.2 Even when accepted, SQL yields zero structural edges

Same 8 files copied to `*.sql` and re-ingested:

```
INGEST_COMPLETE input_done=8 input_total=8 rows_new=1944e+1939p+0a status=ok
```

1,944 entities, 1,939 physicalities, **0 attestations**. Attestation totals for
`source_id('CodeDecomposer')` across both runs: 228 `HAS_NAME_ALIAS` + 46
`IS_A` — bootstrap only. No `CALLS`, no `DEFINES`, no `REFERENCES`.

So on SQL the code lane behaves exactly like the document lane: content DAG plus
trajectories, no typed structure. **Whether that is a deliberate content-only
posture or an unfinished extractor is not determinable from the code and must be
decided** — the difference matters, because one is a design and the other is a
gap.

The relations are governed and waiting: `CALLS` (`relation_types.toml:124`),
`REFERENCES` (`:1134`).

## 3. How it should work

```
INGEST     laplace ingest repo <root>          (or code <subtree>)
           → DEFINES  (file/function root → function id)
             CALLS    (caller id → callee id)
             REFERENCES (function id → table/column id)
INDEX      the CALLS in-degree per function id
PERFCACHE  compile the hot mask per spec 33 (blob + BLAKE3 CRC + fingerprint key)
QUERY      dead canonical := function id with in-degree 0
           filtered to ids that DEFINES-link to a governed opcode entry point
RENDER     realize_batch over the survivors — the only render, at the end
REFUTE     a false positive is attested false and stops firing
```

Properties this form has that the alternatives do not:

- **One identity space across languages.** A C# call site and a SQL body and a
  C function are all content ids; the graph spans them without a per-language
  gate.
- **Cheap repeat cost.** `laplace ingest` is idempotent for rows; a re-run after
  a code change re-witnesses only novel content (the tier descent proves
  novelty, `CLAUDE.md` Writes).
- **It is the invention doing its own job.** If the substrate cannot answer
  "what calls this" about its own source, the claim that it can answer
  structural questions about any corpus is untested where it is cheapest to
  test.

## 4. What to consider

| Decision | Options | Notes |
|---|---|---|
| Compound extensions | strip a trailing templating suffix and re-resolve, vs add `{"in","sql"}` | Strip-and-re-resolve. `.in` is a generic templating convention (autoconf, etc.) and means nothing on its own; mapping it to SQL would mis-route any other `.in` file. Implement in `ModalityOf` (`CodeDecomposer.cs:58-64`) or in the native map — decide where compound handling belongs *once*, since `.sql.in` will not be the last case. |
| SQL structural extraction | full extractor vs `DEFINES` only | `DEFINES` alone (function name → id at `CREATE FUNCTION`) already gives the *node set*; `CALLS` gives the edges G4 needs. Shipping `DEFINES` first is a valid increment but does not deliver G4 — say so rather than declaring partial victory. |
| Where extraction lives | C (tree-sitter query in `engine/`) vs C# decomposer | The tree-sitter parse is already native. Per `CLAUDE.md` Reads, per-row set-returning work and string operations belong in C. But decomposers are pure C# by law (Writes) — so the parse/extract belongs native, the `SubstrateChange` emission in the decomposer. Follow the existing grammar decomposer's split. |
| Relation declaration | must declare every relation emitted | `DecomposerArchitectureGateTests` pins this; emitting an undeclared relation faults the native attestation path. `CALLS`/`REFERENCES` exist; `DEFINES` must be checked and added if absent. |
| Scale | whole repo vs targeted subtrees | GH #595: `grammar_compose` span lookup and span-array growth are both O(n²) and pin a single high-token file for 40+ minutes. Measure on a subtree before pointing it at the repo root, or fix #595 first. |
| Resolution fidelity | syntactic vs semantic | GH #593 already records that tree-sitter gives syntax, not resolved symbols — a `CALLS` edge from a bare identifier may be ambiguous across overloads/schemas. For SQL specifically, `laplace.foo(...)` is usually unambiguous; for C#/C it is not. Scope G4 to SQL first, where the resolution problem is smallest. |
| Perfcache | now vs later | Not needed for correctness. Add it when the query is proven and its cost measured (spec 33 requires a BLAKE3 CRC and an input-fingerprint staleness key — see GH #525, which records that the highway blob currently violates that law). |

**Trap:** do not let "the gate is blocked" become "no gate." Ship the grep as
**explicitly labeled scaffolding** with a shrink-only allowlist, and delete it
when the substrate query lands. Mistaking scaffolding for the destination is the
exact failure mode this workstream exists to kill.

## 5. Where to look

| Concern | File |
|---|---|
| Extension filter (the blocker) | `app/Laplace.Decomposers/Code/CodeDecomposer.cs:40-64` |
| Extension → grammar map | `app/Laplace.Core/Core/GrammarDecomposer.cs:14`, `engine/core/src/grammar_registry.c:74,132` |
| Governed relations | `engine/manifest/relation_types.toml:124` (CALLS), `:1134` (REFERENCES) |
| A finished code source | `app/Laplace.Decomposers/Code/RepoSource.cs:6-25` (declares CONTAINS, CALLS, DEFINES, REFERENCES, …) |
| Ingest entry points | `app/Laplace.Cli/IngestDispatchTable.cs:84-88`, `IngestCommands.cs:447-457` |
| Decomposer relation gate | `DecomposerArchitectureGateTests` |
| Perfcache law | `docs/specs/33_Perfcache_Blob_Law.md`, GH #525 |
| The gate this unblocks | `docs/specs/37_Substrate_Operation_ISA.md` §7 (G4) |

## 6. Acceptance

1. `laplace ingest code extension/laplace_substrate/sql/functions/converse`
   reports `input_units > 0` **and** emits non-zero `CALLS`/`DEFINES`.
2. A SQL query over consensus returns the five known dead canonicals
   (`converse_compose`, `converse_tiered`, and the others named in §1) **without
   any string matching** — id space until the final `realize_batch`.
3. The query returns **no false positive** for a function known to be called
   (spot-check `prompt_coherence`, which has four callers).
4. A deliberately introduced dead function is detected on the next ingest.
5. G4 (#758) is reimplemented against the graph and the grep scaffolding is
   deleted in the same PR — not left alongside.

## 7. Risks

- **#595 (O(n²) grammar_compose)** can make a repo-scale ingest impractical.
  Measure on a subtree; treat #595 as a possible prerequisite rather than
  discovering it at hour three.
- **Symbol resolution (#593)** limits precision outside SQL. A `CALLS` edge is
  evidence, not proof — which is *fine*, because consensus is built for exactly
  that: rate it, and let refutation correct it. Do not model it as ground truth.
- **Ingesting the repo changes the substrate the repo is measured on.** Code
  content will appear in walks and recalls. Give it its own source and trust
  class so it can be scoped or evicted (`evict_source` exists), and do not let
  it silently colonize conversational reads.
