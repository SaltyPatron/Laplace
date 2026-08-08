> Archived workstream analysis. Historical evidence only; GitHub owns status.

# W6 — Architecture gates (spec 37 G1–G10 + the elector invariant)

**Issue:** #758 · **Plan:** `COMPLETION_PLAN.md` R9 / Phase 1 · **Blocks:** honest
"done" for every later phase · **Related:** #765 (G4's real form), #764

---

## 1. Why this exists

> *A rule without a gate is a comment, and this document exists because comments
> were the parity mechanism.* — spec 37 §7 (`:381-382`)

This repo's rules are mostly true and mostly unenforced. The measured state
(2026-08-02): **only L4 has real enforcement**, and only through
generation-determinism rather than the literal ban it states. L1, L3, L5, L6,
L7, L8 have **zero** mechanical enforcement. L2 has one mode pinned.

The cost is not theoretical. Two examples from this repo's last 24 hours:

- The elector key list was edited in `26397e7`, reverted in `4c4106d` — **two
  edits in one session with no net** — and nothing would have caught a mistake.
- **A fifth elector was then created** (`infer.sql.in`, commits `cb4438f` /
  `17d4934`) while `prompt_coherence.sql.in:46` still reads *"The four
  electors."* The invariant grew a site and the prose describing it did not.
  That is spec 37 §0's drift, committed hours after it was documented.

## 2. How gates work here today

Four distinct families, all real, all worth reusing rather than reinventing.

### 2.1 C# xUnit tests reading the tree as text — the dominant style

| Gate | File | Mechanism | Allowlisting |
|---|---|---|---|
| Decomposer architecture (16 facts) | `app/Laplace.Substrate.Tests/Abstractions/DecomposerArchitectureGateTests.cs` | `EnumerateFiles` + whole-file `Regex`; two facts use reflection (`:121`, `:129`) | five `HashSet<string>`; two asserted **empty** (`:543`); `MultiPhaseAllowlist` is **bidirectional** (`:398-406`) — unknown violators fail *and* stale entries fail |
| Read-path architecture (5 facts) | `.../ReadPathArchitectureGateTests.cs` | whole-file `Regex` (deliberately not line-oriented, `:110-115`) | **the ratchet**: `AssertNoNewcomers` (`:154`) + `AssertNoStaleEntries` (`:161`) + `AllowlistsOnlyShrink` against `const int` ceilings (`:107,116,201-209`), each entry carrying dated prose |
| Reading a `.sql.in` from C# | `.../SubstrateCountsExactTests.cs:12-22` | `File.ReadAllText` + `Assert.Contains` | none |

Repo-root discovery for all of them: `TypeIdLawTests.FindRepoRootPublic()`
(`TypeIdLawTests.cs:143-165`). **Any new C# gate must use it.**

### 2.2 Python policy scripts — the pre-build job

`.github/workflows/laplace.yml:77-200`, job *"Policy — manifest, attestation,
vocabulary"*, self-hosted, 15 min, six steps. Notable: a **CI presence
tripwire** (`:89-115`, added after a commit deleted every workflow), the
**codegen determinism** check (`:120-151` — regenerate, sha256, require
byte-identical; the only real L4 enforcement), and a **pure-grep violation
counter** (`:161-174`) whose exit code *is* the violation count — the template
for a cheap policy gate, and also the anti-pattern to avoid (no allowlist).

`scripts/validate-pipeline.py` (478 lines) cross-checks manifests against five
shell/cmd rosters by regex-parsing them, and `validate_type_identity_law()`
(`:324-369`) is the closest existing template for G3.

### 2.3 Live-data gates

`scripts/decomposer-gate-check.py` — shells `psql -tAc` against a **seeded** DB
and asserts per-source facts; runs in `_ingest.yml:149-167` on seed workflows
only. Grandfathering via `--allow-health-tier` and per-source `content_only` /
`skip_layer_complete` flags in `decomposer-gates.json`.

### 2.4 pg_regress

`extension/laplace_substrate/tests/CMakeLists.txt:12` lists 19 tests.
**Constraint that shapes everything:** `:16-19` does `dropdb && createdb` — the
regress DB is **fresh and unseeded** every run. Hence
`realize_ladder_parity.sql:23-31`'s design note: no sampling, every id minted
via `word_id()`, every assertion carrying a cardinality guard so an empty DB
**fails** rather than vacuously passing.

Two existing parity tests are the direct G6 template:
`walk_edge_weight_parity.sql` (C↔SQL constants + formula against hand-written
reference algebra over a 7-row `VALUES` vector) and `realize_ladder_parity.sql`
(scalar↔native agreement, position alignment, abstention, degenerate inputs).

## 3. G1–G10: measured status

| Gate | Status | Evidence |
|---|---|---|
| **G1** weight literalism | **built; 25 grandfathered violations** | `isa-gate-check.py` strips comments and finds 19 production-path expressions plus 6 in `scripts/sql/model-planes-audit.sql`. The earlier total of 19 omitted those 6 despite listing them. Exempt per spec: `mu/eff_mu.sql.in`, `glicko2.c:435`, the three `sql/indexes/*eff_mu*` |
| **G2** render-before-select | **built; 54 files / 111 call sites grandfathered** | `RenderBeforeSelectGateTests`; ratchet over the hand-drawn list, excluding `realize/`, `readback/`, `lexical/type_label.sql.in`, `converse/label*.sql.in`. D4's "~30 files" was an estimate; the comment-stripped measurement over those exclusions is 54/111 |
| **G3** vocabulary literalism | **built; 243 executable SQL sites, 17 C sites, 702 production C# sites** | the earlier raw SQL count of 245 included 2 comments; C# is checked against canonical + alias names parsed from the manifest. Exact path/literal/count baselines ratchet all three |
| **G4** dead canonical | not built; **`converse_tiered` would fire today** (one hit, its own `CREATE`) | destination is the substrate `CALLS` read — see [W3](W3_Self_Ingest_Call_Graph.md); grep is scaffolding |
| **G5** shape parity | **built; zero violations** — the five declarations agree today | `ShapeParityGateTests` pins `query_shapes.sql.in:6`, `recall_route.c:64` (equal *in order*), `recall.c:347` (subset), `recall.c:1237,1245` (must point at the catalog, not enumerate), and the **prose in an MCP tool description**, `SubstrateTools.cs:74` — shape list *and* the three requirement clauses, derived from the catalog's boolean columns. Two further subset sites: `ROUTE_DEFAULT_INTENT`, `chat.sql.in` branch literals |
| **G6** weight parity | **partial** — COMPLETE mode + constants pinned | `walk_edge_weight_parity.sql`; SALIENCE and STRENGTH unpinned |
| **G7** roster parity | **built** | `validate-pipeline.py:260-321` pins shell/cmd order; `IngestRosterParityTests` bidirectionally pins C# dispatch to the manifest plus 14 explicit operational/alias routes under a shrink-only ceiling |
| **G8** band literalism | **built; 8 grandfathered sites in 3 files** | `chat.sql.in`, `converse_compose.sql.in`, `senses_with_context.sql.in`; exact expressions are shrink-only |
| **G9** envelope | not built **and not buildable yet** | `chat.sql.in:35` is `RETURNS text`; needs an OP-level change first |
| **G10** one mutex | **built; 5 mutex copies + 11 verify implementations grandfathered** | `IngestMutexGateTests`. The mutex is now **verified** and spec 37 `:328`'s "6 ingest-mutex + 11 verify" is exact: 5 copies of the `Win32_Process … Laplace\.Cli` probe (4 byte-identical) + **1** database implementation. The database half is **already single** — `AdvisoryTxLock.BeginWithLockAsync`, one call site; `highway_mask_deposit`'s lock is a different lock. Verify destination is `laplace.source_status()`, whose own header argues this gate |

## 4. The elector invariant — ground truth and design

### 4.1 Five sites, not four

All five carry the identical six-key list `specificity DESC NULLS LAST,
rel_mass DESC NULLS LAST, peers DESC, ord DESC, denote_mu DESC NULLS LAST,
synset_id` — in **five different syntactic shapes**:

| File | Line | Form | Selects |
|---|---|---|---|
| `converse/chat.sql.in` | 233-238 | `array_agg(… ORDER BY …)` | full ranked list |
| `converse/converse.sql.in` | 57-62 | statement `ORDER BY … LIMIT 1` | `synset_id` |
| `converse/converse_walk.sql.in` | 60-65 | statement `ORDER BY … LIMIT 1` | `synset_id` |
| `converse/resolve_topic.sql.in` | 73-78 | scalar subquery + `WHERE specificity > 0` (`:72`), `@extschema@.` prefix, column-aligned whitespace | **`tok`** |
| `converse/infer.sql.in` | 31-36 | `row_number() OVER (ORDER BY …)` | ranked id |

**Byte-equality will not work.** The gate must tokenize.

### 4.2 Design

A C# `[Fact]` reading the five `.sql.in` files. Rejected alternatives, with
reasons: **pg_regress** can't see the key list across five query shapes without
re-implementing a parser in PL/pgSQL, and `prosrc LIKE '%specificity DESC%'`
catches deletion but not reordering; **a python policy script** would work and
fails 45 minutes earlier, but the repo's *structured* text gates already live in
C# and `validate-pipeline.py`'s charter is manifests-vs-orchestration.

Three facts:

1. **Key-order parity.** Regex-anchor on `y\.specificity\s+DESC` per file;
   consume to the first unbalanced `)` / `LIMIT` / `;`; normalize (strip
   `@extschema@.` and the `y.` alias, collapse whitespace, uppercase keywords);
   assert each equals one `const string ExpectedElectorKeys` declared beside the
   rationale quoted from `prompt_coherence.sql.in:28-49`.
2. **Completeness (bidirectional).** Enumerate every `.sql.in` under
   `sql/functions/` containing `prompt_coherence(`; assert that set equals the
   five declared sites plus `prompt_coherence.sql.in` itself. **This is the fact
   that would have caught `infer.sql.in` becoming a sixth elector unpinned.**
   Copy `DecomposerArchitectureGateTests.DecomposerMultiPhase_AllowlistMatchesTree`
   (`:380-407`).
3. **No exemptions.** Assert the site set has no allowlist — mirrors
   `UnicodeAndHandBuilderAllowlists_AreEmpty` (`:543`).

## 5. What to consider

| # | Decision | Recommendation |
|---|---|---|
| D1 | policy job (python, pre-build, ~2 min) vs `integration-test` (C#, post-build, ~45-60 min) | **Split.** G1/G3/G8 are flat greps with flat allowlists → one `scripts/isa-gate-check.py` step in the policy job. G2/G4/G5/elector need sets, ceilings and ratchets → C#. The repo already splits this way. |
| D2 | allowlists as C# constants vs JSON | Constants keep rationale next to rule and make `git blame` meaningful. JSON only for the python half. **Never put ceilings in JSON** — `ReadPathArchitectureGateTests.cs:107,116` proves a compile-time `const` is what makes "may only shrink" enforceable. |
| D3 | G4 now or after W3 | Ship the grep now as **explicitly labeled scaffolding** with a shrink-only allowlist; replace with the substrate `CALLS` read when W3 lands, deleting the grep in the same PR. |
| D4 | G2's definition | Not decidable by regex (`realize_batch`, the realizer bodies, `label_is_content` over already-rendered text all false-positive). Define G2 as a **ratchet over a hand-drawn violator list** (~30 files), excluding `realize/`, `readback/`, `lexical/type_label.sql.in`, `converse/label*.sql.in`. |
| D5 | grandfathering style | **Ratchet, never a bare counter.** Porting `laplace.yml:161-174`'s `exit "$violations"` to G1 fails the build on day one at 25 sites. |

**Trap:** a gate that goes red on merge-day teaches people to ignore it
(`ingest-baseline.py:34-37` says this in the repo's own words). Land each gate
with its current violations enumerated and dated, then shrink.

## 6. Where to look

| Concern | File |
|---|---|
| Ratchet pattern (copy this) | `app/Laplace.Substrate.Tests/Abstractions/ReadPathArchitectureGateTests.cs:107,116,154,161,201-209` |
| Bidirectional set check (copy this for the elector) | `.../DecomposerArchitectureGateTests.cs:380-407,543` |
| Repo-root discovery | `.../TypeIdLawTests.cs:143-165` |
| C# reading a `.sql.in` | `.../SubstrateCountsExactTests.cs:12-22` |
| Policy job + grep-counter template | `.github/workflows/laplace.yml:77-200`, esp. `:161-174` |
| Vocabulary-law template | `scripts/validate-pipeline.py:324-369` |
| Parity regress templates | `extension/.../tests/sql/walk_edge_weight_parity.sql`, `realize_ladder_parity.sql:23-31` |
| Regress DB is unseeded | `extension/laplace_substrate/tests/CMakeLists.txt:16-19` |
| The laws | `docs/specs/37_Substrate_Operation_ISA.md` §2 (L1-L8), §7 (G1-G10) |

## 7. Acceptance

1. Reordering or deleting a key in **any** of the five elector `ORDER BY` lists
   fails a named test.
2. Adding a **sixth** `prompt_coherence(` caller with an unpinned key list fails.
3. A new open-coded `rating - 2*rd` outside the three exempt paths fails; the 25
   existing sites are enumerated with dated rationale.
4. Removing a violation and forgetting its allowlist entry fails (stale check).
5. Every allowlist has a `const` ceiling ≥ its count; no ceiling rises without a
   diff on the constant.
6. `converse_tiered` either gains a caller or is deleted — G4's first casualty.
7. `prompt_coherence.sql.in`'s prose no longer states a site count; it points at
   the gate's declaration instead (see D-note below).

**D-note on prose vs gate:** rather than updating "four electors" to "five" and
waiting for the next drift, let the gate's site list be the single declaration
and have the prose reference it. That is spec 37 §7's own thesis applied to
itself.

## 8. Risks / ordering

1. **G9 is blocked** on `chat()` returning something richer than `text`. Do not
   schedule it here.
2. **G6 cannot complete** until OP4's mode unification exists — there is no
   `weight_mode` type anywhere in the tree (verified: zero hits).
3. **G5's fifth declaration is prose in an MCP tool description**
   (`SubstrateTools.cs:74`); generating that string is a code change, not a gate.
4. Order: elector + G1 + G3 + G8 (mechanical, no code motion) → G4 grep → G2
   ratchet → G7 C# half → G5/G6/G9/G10 after their opcodes exist.

## 9. Progress — 2026-08-02 checkpoint

Landed on `main` (see [CHECKPOINT_2026-08-02.md](CHECKPOINT_2026-08-02.md)):

- Elector invariant (#771) — acceptance items 1–2 and the D-note in §7.
- G1 / G3 / G8 (#772) — policy-job ratchets; baselines in
  `scripts/isa-gate-baseline.json`.
- G7 C# half (#775) — `app/Laplace.Cli.Tests/IngestRosterParityTests.cs`.

**Next on this workstream:** G4 scaffolding (labeled grep, shrink-only) in
parallel with W3 structural extract; then G2 ratchet. Do not schedule G9
here. Do not close #758 until G4's destination form (or an explicit
scaffolding→destination handoff recorded on the issue) exists.

## 10. Progress — 2026-08-05 checkpoint

G2, G10 and G5 landed as C# ratchets in
`app/Laplace.Substrate.Tests/Abstractions/`, all green on merge day per §5's trap
note. Ceilings are `const int` (D2); no ceiling moved to JSON.

| Gate | File | Grandfathered |
|---|---|---|
| G2 | `RenderBeforeSelectGateTests.cs` | 54 files / 111 scalar-realizer call sites |
| G10 | `IngestMutexGateTests.cs` | 5 process-mutex copies, 1 non-ingest advisory lock, 11 verify files / 13 sites |
| G5 | `ShapeParityGateTests.cs` | none — the five declarations agree |

**Three things §3 recorded as open that measurement found already true:**

1. **The database-level ingest mutex is already one implementation.** §3 called the
   mutex "unverified"; `AdvisoryTxLock.BeginWithLockAsync` is the single
   implementation with a single call site (`NpgsqlWorkingSetApply`). Only the
   *process-level* half is duplicated, and only in the Windows seed scripts.
2. **Spec 37 `:328`'s "6 ingest-mutex + 11 verify" was accurate**, not an estimate
   in need of correction: 5 + 1 = 6, and 11 verify files.
3. **G5's five declarations already agree** — including the MCP prose. §3 listed
   the five as a drift risk; measured on 2026-08-05 there is no drift to
   grandfather, so G5 landed with no allowlist at all.

**Correction to §5 D4:** the "~30 files" estimate for G2 is wrong. Over exactly the
exclusions D4 names, comment-stripped, it is **54 files / 111 sites**.
