# Bucket: X3 — substrate tests, substrate docs, geom extension, control/CMake

### Files read (coverage proof)
- [x] extension/CMakeLists.txt
- [x] extension/laplace_geom/CMakeLists.txt
- [x] extension/laplace_geom/laplace_geom.control.in
- [x] extension/laplace_geom/sql/01_meta.sql.in
- [x] extension/laplace_geom/sql/02_hash128.sql.in
- [x] extension/laplace_geom/sql/03_hash128_ops.sql.in  — **(empty: 1 blank line)**
- [x] extension/laplace_geom/sql/04_hilbert.sql.in
- [x] extension/laplace_geom/sql/05_mantissa.sql.in
- [x] extension/laplace_geom/sql/06_st_4d.sql.in
- [x] extension/laplace_geom/sql/07_s3_opclass.sql.in  — **(empty: 1 blank line)**
- [x] extension/laplace_geom/sql/laplace_geom.sql.in
- [x] extension/laplace_geom/sql/sqldefines.h.in
- [x] extension/laplace_geom/sql/uninstall_laplace_geom.sql.in  — **(empty)**
- [x] extension/laplace_geom/src/laplace_geom.c
- [x] extension/laplace_geom/src/lwgeom_win32_stubs.c
- [x] extension/laplace_geom/tests/CMakeLists.txt
- [x] extension/laplace_geom/tests/expected/hash128.out
- [x] extension/laplace_geom/tests/expected/st_4d.out
- [x] extension/laplace_geom/tests/sql/hash128.sql
- [x] extension/laplace_geom/tests/sql/st_4d.sql
- [x] extension/laplace_substrate/CMakeLists.txt
- [x] extension/laplace_substrate/docs/FUNCTION_CATALOG.md
- [x] extension/laplace_substrate/docs/SQL_SURFACE.md
- [x] extension/laplace_substrate/laplace_substrate.control.in
- [x] extension/laplace_substrate/tests/CMakeLists.txt
- [x] extension/laplace_substrate/tests/expected/bootstrap.out
- [x] extension/laplace_substrate/tests/expected/consensus_fold.out
- [x] extension/laplace_substrate/tests/expected/consensus_period.out
- [x] extension/laplace_substrate/tests/expected/consensus_signed.out
- [x] extension/laplace_substrate/tests/expected/converse.out
- [x] extension/laplace_substrate/tests/expected/entities_exist_bitmap.out
- [x] extension/laplace_substrate/tests/expected/generation_corpus.out
- [x] extension/laplace_substrate/tests/expected/glicko2_aggregate.out
- [x] extension/laplace_substrate/tests/expected/identity_law.out
- [x] extension/laplace_substrate/tests/expected/schema_law.out
- [x] extension/laplace_substrate/tests/expected/structural_surface.out
- [x] extension/laplace_substrate/tests/expected/word_law.out
- [x] extension/laplace_substrate/tests/sql/bootstrap.sql
- [x] extension/laplace_substrate/tests/sql/consensus_fold.sql
- [x] extension/laplace_substrate/tests/sql/consensus_period.sql
- [x] extension/laplace_substrate/tests/sql/consensus_signed.sql
- [x] extension/laplace_substrate/tests/sql/converse.sql
- [x] extension/laplace_substrate/tests/sql/entities_exist_bitmap.sql
- [x] extension/laplace_substrate/tests/sql/generation_corpus.sql
- [x] extension/laplace_substrate/tests/sql/glicko2_aggregate.sql
- [x] extension/laplace_substrate/tests/sql/identity_law.sql
- [x] extension/laplace_substrate/tests/sql/schema_law.sql
- [x] extension/laplace_substrate/tests/sql/structural_surface.sql
- [x] extension/laplace_substrate/tests/sql/word_law.sql

Out-of-bucket files I traced to verify doc claims: sql/inference/* (exists, 6 files),
20_converse.sql.in (#include lines + synset_members), 26_generation.sql.in (continue_text).

---

## Findings

### F1 — SQL_SURFACE.md: `inference/` declared "DEAD / orphaned / NOT BUILT / a trap, delete it" — FALSE, it is the LIVE read surface
- **FILE:** extension/laplace_substrate/docs/SQL_SURFACE.md:19-25, 154, 176-177
- **SEVERITY:** HIGH
- **CATEGORY:** disparagement / wrong-doc (the doc instructs deleting working code)
- **CLAIM in doc:** "`sql/inference/` (all 6 files) — NOT in the `#include` graph or CMakeLists.txt `EXT_SQL_MODULES`. Byte-identical duplicates of functions live in `20_converse.sql.in` ... They ship nothing ... it's a trap — it looks authoritative and isn't built." Inventory table row: "**inference/** ... ALL DEAD". Recommendation #1: "Delete `sql/inference/`".
- **VERIFIED:** `grep -n include 20_converse.sql.in` →
  `20_converse.sql.in:111 #include "inference/synset_members.sql.in"` …112 synonyms …113 translations
  …114 translate_to …115 language_coverage …`:469 #include "inference/attention.sql.in"`. The cpp
  preprocessor resolves these via `-I .../sql` (CMakeLists.txt:158). So inference/ is the ACTUAL home of
  these six functions, pulled into the build through 20_converse. They are NOT dead, NOT duplicates, NOT
  unbuilt. The doc has the relationship exactly backwards (the "copies" are the includes). CMakeLists
  `EXT_SQL_MODULES` lists only 01–27 + main/upgrade because that list is the custom-command DEPENDS, not
  the include closure — absence there does NOT mean unbuilt.
- **CONFIDENCE:** high. Acting on this doc (delete inference/) would delete synonyms/translations/
  translate_to/language_coverage/synset_members/attention — the live recall surface.

### F2 — SQL_SURFACE.md / FUNCTION_CATALOG.md: the "king bug" (synset_members lacks IS_INSTANCE_OF filter) — already fixed in code; doc points at the wrong file
- **FILE:** extension/laplace_substrate/docs/SQL_SURFACE.md:42-46, 181-182 ; FUNCTION_CATALOG.md:41-48 (F2)
- **SEVERITY:** MEDIUM
- **CATEGORY:** wrong-doc / disparagement
- **CLAIM in doc:** SQL_SURFACE: "`synset_members` ... traverses only `IS_SYNONYM_OF` and does **no
  `IS_INSTANCE_OF` filtering**" ... "Fix belongs in `synset_members` in **`20_converse.sql.in:110-129`**".
- **VERIFIED:** the real `synset_members` is `sql/inference/synset_members.sql.in`; lines 19/29 contain the
  filter: `-- WordNet @i (IS_INSTANCE_OF) marks person/place instances (Billie Jean ...)` and
  `AND i.type_id = relation_type_id('IS_INSTANCE_OF')` inside a `NOT EXISTS`. So the described bug is
  already addressed. SQL_SURFACE points the "fix" at `20_converse.sql.in:110-129`, which is merely the
  block of `#include` lines, not the function body. FUNCTION_CATALOG F2 is more honest ("filter may
  already be partially present ... must re-test") but still frames it as an open defect.
- **CONFIDENCE:** high for "filter exists / wrong file"; the runtime efficacy I did not re-run (no live DB).

### F3 — generation_corpus regress test: committed `.sql` and `.out` are DESYNCED → the test fails (or pins nothing)
- **FILE:** extension/laplace_substrate/tests/sql/generation_corpus.sql vs tests/expected/generation_corpus.out
- **SEVERITY:** HIGH
- **CATEGORY:** fake-test / broken-test
- **CLAIM:** the two halves of one pg_regress case encode different expectations:
  - `.sql:104` `IF st.positions <> 6 ... want 6`  vs  `.out:102` (echoed) `IF st.positions <> 10 ... want 10`
  - `.sql:113-114` `(the→capital) ... cnt = 1 ... want 1`  vs  `.out:112` `cnt = 2 ... want 2: run expansion`
  - `.sql:124` `(france→end) run-boundary, object_id = w_end`  vs  `.out:122` `(france→the), object_id = w_the`
  - `.sql:5-16` comments stripped to blank lines  vs  `.out:7-17` full comment block present
- **VERIFIED:** read both files in full; the `.out` echoes the SQL pg_regress actually executed, and it
  differs from the on-disk `.sql`. `git log` shows `.sql` last touched in `2539549` ("Manual user commit
  to clear stage") after a comment-strip commit `f4c647f`, while `.out` reflects the newer
  run-length-expansion model (`12bc017` "Trajectories are the single source"). The on-disk `.sql` carries
  the OLD model (no run expansion: positions 6, cnt 1, france→end). Running it now yields the NEW corpus
  (positions 10, cnt 2), so `IF st.positions <> 6` RAISES, producing an ERROR where `.out` expects the
  success NOTICE → pg_regress diff fails. As committed this test cannot pass.
- **CONFIDENCE:** high.

### F4 — SQL_SURFACE.md: the legacy period-fold functions tagged "DEAD" are exercised by the live regress suite
- **FILE:** extension/laplace_substrate/docs/SQL_SURFACE.md:26-31, 140 ("14 period fold ... **4 legacy fns DEAD**")
- **SEVERITY:** MEDIUM
- **CATEGORY:** disparagement / wrong-doc
- **CLAIM in doc:** `materialize_period_partition`, `materialize_period_partition_fresh`,
  `materialize_period_consensus`, `create_period_staging(integer)` are "superseded ... Dead."
- **VERIFIED:** `consensus_period.sql` (and its `.out`, which passes with NOTICE) calls
  `materialize_period_consensus()` (lines 42,52,64,70,85,98,113), `materialize_period_partition(...)`
  (178,184), `create_period_staging()` / `create_period_staging(2)` / `create_period_staging(2,1)`
  throughout; `consensus_fold.sql` also uses `materialize_period_partition`. These are the verification
  substrate of two regress tests, not dead. FUNCTION_CATALOG.md:114 is more accurate
  ("`materialize_period_consensus` + 1-arg `create_period_staging` = test-only (no prod caller)").
- **CONFIDENCE:** high.

### F5 — FUNCTION_CATALOG.md: F6 calls `structural_neighbors` "semantically random" / "embedding doesn't carry semantics yet" — disparages a design invariant
- **FILE:** extension/laplace_substrate/docs/FUNCTION_CATALOG.md:63-67 (F6), 187-188
- **SEVERITY:** MEDIUM
- **CATEGORY:** disparagement (contradicts invariant #4: coordinates are FORM, not meaning)
- **CLAIM in doc:** "`structural_neighbors` is geometrically real but **semantically random** ... nearest-
  in-space ≠ nearest-in-meaning ... the embedding doesn't carry semantics yet." Tagged ⚠️ "semantically
  random" again at line 187.
- **VERIFIED against invariant:** the architecture (CLAUDE.md §1; charter invariant #4) states the 4D
  coord is DUCET→super-Fib→Hopf S³ FORM; meaning is the Glicko-2 consensus fold, never the coords. KNN on
  coord is *defined* to be a form neighborhood. Reporting "nearest-in-space ≠ nearest-in-meaning" as a
  defect ("doesn't carry semantics **yet**") is a category error that frames intended behavior as broken.
  This is exactly the "semantically random" editorializing the charter §0 flags as not-truth.
- **CONFIDENCE:** high (it contradicts the stated design, not a measurement).

### F6 — FUNCTION_CATALOG.md is saturated with unverified ❌/⚠️ "broken/degraded/garbage" status tags
- **FILE:** extension/laplace_substrate/docs/FUNCTION_CATALOG.md (legend line 8-10; F5 "collocates broken",
  F8 "flat/zero mu", F9 "synonym-loop garbage", F10 "5,858 violations", and ⚠/❌ throughout §3-6)
- **SEVERITY:** LOW
- **CATEGORY:** disparagement
- **CLAIM:** dozens of ❌ broken / ⚠️ degraded / "garbage" / "broken (always empty)" tags presented as
  empirical fact ("not assumed"). Per charter §0 these are precisely the prior-Claude editorializing tags
  that are "frequently WRONG and disparaging" and must not be quoted as status. The doc's own identity_law
  regress test (`identity_law.out`) asserts `substrate_health().ok = t` and `identity_violations = 0` on a
  fresh DB — directly contradicting F10's "ok=false, identity_violations=5858" as a universal truth (F10
  is a property of one particular seeded DB, stated as if intrinsic).
- **VERIFIED:** could not re-run the live seeded DB to confirm/refute each tag (no DB access in this audit);
  flagged as unverified-claim-presented-as-fact per the charter, with the identity_law test as a concrete
  counter-data-point. Offer to strip the tags rather than quote them.
- **CONFIDENCE:** med (the disparagement pattern is certain; per-claim truth is unverified by design).

### F7 — geom: `03_hash128_ops.sql.in` and `07_s3_opclass.sql.in` are empty stubs included into the build
- **FILE:** extension/laplace_geom/sql/03_hash128_ops.sql.in, 07_s3_opclass.sql.in (both 1 blank line);
  referenced in laplace_geom.sql.in:5,9 and CMakeLists.txt:184,190
- **SEVERITY:** LOW
- **CATEGORY:** dead-code / incomplete-stub
- **CLAIM:** the file names promise a hash128 operator set ("hash128_ops") and the S³ operator class
  ("s3_opclass" — the GiST/SP-GiST opclass that would make "KNN on coord" an indexed read, central to the
  invention's form-neighborhood). Both files are empty, so neither operator class exists. KNN today runs
  via `structural_neighbors` (plpgsql over `laplace_distance_4d`), i.e. no native index opclass.
- **VERIFIED:** read both files (empty); confirmed they are `#include`d (laplace_geom.sql.in) and listed in
  EXT_SQL_MODULES — so they are intentional placeholders, contributing nothing.
- **CONFIDENCE:** high.

### F8 — geom: detoasted GSERIALIZED never freed in scalar functions (per-row palloc leak)
- **FILE:** extension/laplace_geom/src/laplace_geom.c:21-34 (lwgeom_from_datum) and all callers
  (pg_laplace_distance_4d:211-225, angular:233-247, dwithin:255-280, centroid:288-308, radius:316-326,
  frechet:334-351, hausdorff:359-377, hilbert_encode:384-396)
- **SEVERITY:** LOW
- **CATEGORY:** correctness / perf (resource leak)
- **CLAIM:** `lwgeom_from_datum` does `PG_DETOAST_DATUM(d)` and returns the GSERIALIZED via `*out_gser`,
  but callers free only the LWGEOM (`lwgeom_free`) and never the `g`/`g_a`/`g_b` GSERIALIZED. When the
  input datum is toasted/compressed, PG_DETOAST_DATUM palloc's a fresh copy that is leaked until the
  surrounding memory context resets. For IMMUTABLE per-row scalar use over large scans this accumulates.
- **VERIFIED:** traced lwgeom_from_datum + every caller; no `pfree(g*)` anywhere. (Note `out_gser` is set
  but the value is unused by callers — they pass `&g` then ignore `g`.)
- **CONFIDENCE:** med (leak is real; severity depends on toast frequency + context lifetime).

### F9 — schema_law / structural_surface tests bake a `CREATE EXTENSION ... ERROR: already exists` into expected output (order-coupled, fails standalone)
- **FILE:** extension/laplace_substrate/tests/sql/schema_law.sql:2-3 + expected/schema_law.out:3-6 ;
  tests/sql/structural_surface.sql:2-3 + expected/structural_surface.out:3-6
- **SEVERITY:** LOW
- **CATEGORY:** fragile-test
- **CLAIM:** both tests use bare `CREATE EXTENSION laplace_geom;` / `laplace_substrate;` (no IF NOT
  EXISTS); the expected `.out` captures `ERROR: extension "laplace_geom" already exists`. This only matches
  when a prior test in the shared regress DB already created them (bootstrap runs first in REGRESS_TESTS).
  Run standalone against a fresh DB the CREATE succeeds (no ERROR) and the diff fails. Compare bootstrap/
  consensus_*/word_law which use `CREATE EXTENSION IF NOT EXISTS` or run in a fresh-state assumption.
- **VERIFIED:** read both pairs; confirmed bare CREATE + expected ERROR lines; REGRESS_TESTS order in
  tests/CMakeLists.txt:12 puts bootstrap first.
- **CONFIDENCE:** high.

### F10 — SQL_SURFACE.md recommends collapsing `eff_mu`/`effective_mu`, but word_law pins them as a deliberate SQL-vs-C parity guard
- **FILE:** extension/laplace_substrate/docs/SQL_SURFACE.md:38-40, 180 ("Consolidate eff_mu/effective_mu
  to one") vs tests/sql/word_law.sql:63-70
- **SEVERITY:** LOW
- **CATEGORY:** wrong-doc
- **CLAIM in doc:** the two are "redundant ... two impls" to collapse to one.
- **VERIFIED:** word_law.sql asserts `bool_and(eff_mu(r,d) = effective_mu(r,d))` across 5 rating/rd rows
  with the comment "the SQL `eff_mu()` inline must equal the engine's compiled `effective_mu()` ... the
  hot-path inline cannot drift." They are intentionally two implementations cross-checked against each
  other; collapsing to one deletes the drift guard. FUNCTION_CATALOG.md:107 states this correctly
  ("`effective_mu` (↪ C dup of eff_mu, parity-test only)").
- **CONFIDENCE:** high.

### F11 — INFO: tests + seed encode the invented `substrate/type/X/v1` relation-type namespace (invariant #6)
- **FILE:** tests/sql/bootstrap.sql:9-24, converse.sql:60-66, schema_law.sql:30 (relation_type_path_law
  pins `relation_type_id('IS_A') = blake3('substrate/type/IS_A/v1')`)
- **SEVERITY:** INFO
- **CATEGORY:** invention-violation (root cause is the seed/relation_law, outside this bucket)
- **CLAIM:** charter invariant #6 says relation types should anchor on the real GWN/ConceptNet inventory
  name, blake3'd — never an invented `substrate/type/X/v1` namespace. The tests bake the invented namespace
  in as law (`relation_type_path_law`), reflecting (not causing) the seed. Noted because the tests would
  need updating when WS3 paves the convergence index; the defect's root is 19_relation_law/21_seed.
- **CONFIDENCE:** high (the namespace is the invented one); root cause out of bucket.

### Clean / real (no defect)
- **geom math C bindings + tests** (laplace_geom.c, hash128.sql/.out, st_4d.sql/.out): correct, real
  assertions on concrete values (blake3('hello')=ea8f163d…, distance/centroid/frechet/hausdorff/hilbert
  roundtrip, mantissa pack/unpack, vertex flag decode). Heavy math is delegated to laplace_core
  (math4d/hilbert4d/mantissa) per the altitude rule — geom is a thin marshaller. Good.
- **glicko2_aggregate, consensus_signed, consensus_period, consensus_fold, word_law, converse,
  entities_exist_bitmap, bootstrap, identity_law** tests: substantive, real-value assertions (Glickman
  paper pin r=1464.06/rd=151.52/σ=0.05999; lane parity; order-invariance; mixed-φ refusal; empty-swap
  guard; cross-product avoidance; merkle word-id law; recall router end-to-end). These are genuine pins,
  not no-ops.

### Note (fork visible from tests, root outside bucket)
- consensus_fold.sql exercises two fold lanes via `set_config('laplace.fold_lane', 'sql'|'engine')` —
  i.e. there are TWO fold implementations (a flag-gated parallel lane, charter §8 "the disease"). The test
  legitimately pins them int64-identical, but the existence of the dual lane is the fork. Root is in
  src/consensus_fold_engine.c / consensus_fold_step.c (outside this bucket); flagged for the owners of
  those files.

---

### Bucket summary
- CRITICAL: 0
- HIGH: 2 (F1 doc says delete the live read surface; F3 generation_corpus .sql/.out desynced → test fails)
- MEDIUM: 3 (F2 king-bug already fixed/doc wrong file; F4 "DEAD" period fns are live-tested; F5 "semantically random" disparages invariant #4)
- LOW: 5 (F6 saturated status tags; F7 empty s3_opclass/hash128_ops stubs; F8 GSERIALIZED leak; F9 order-coupled CREATE EXTENSION expected-ERROR; F10 eff_mu parity-guard mislabeled redundant)
- INFO: 1 (F11 invented relation-type namespace in tests)

**Single worst issue:** F1 — SQL_SURFACE.md authoritatively instructs deleting `sql/inference/` as "all
DEAD, a trap, ships nothing," when those six files are `#include`d by 20_converse.sql.in (lines 111-115,
469) and ARE the live synonyms/translations/translate_to/language_coverage/synset_members/attention read
surface. Following the doc would delete working recall functionality. (F3 is a close second: a regress test
that cannot pass as committed.)
