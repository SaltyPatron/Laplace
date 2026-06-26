## Bucket: I4 — .ingest-proof/

Checked-in "proof" artifacts of ingest runs (92 files). Audited for whether they are genuine
evidence, no-ops, or failures dressed as success; and whether they evidence the invariants
(conflicts≈0, round-trips≈tier-depth, <30 min, O(batch) RAM, no ON CONFLICT).

### Files read (all 92, in full or — for the two >256KB rotating logs — head+tail+grep+representative pages)
- [x] PHASE8-SUMMARY.tsv
- [x] asan-ud.out (425 lines, read in full across 2 pages)
- [x] bench-hilbert-order.sql
- [x] cl-iso.err / [x] cl-iso.out
- [x] cl-ud.err (1502 lines / 290KB — head, tail, full grep of batches/complete/errors, crash trace)
- [x] cl-ud.out (2 lines — empty header only; real log went to .err)
- [x] cl-uni.err / [x] cl-uni.out
- [x] clean-ud.sh / [x] clean-ud.stage
- [x] ladder-cont.out (1 line)
- [x] live-iso.err / [x] live-iso.out
- [x] live-ud.err / [x] live-ud.out
- [x] live-udfull.err / [x] live-udfull.out
- [x] live-unicode.err / [x] live-unicode.out
- [x] measure-copyvalidate.ps1
- [x] p8-01-unicode.log.err / [x] p8-01-wordnet.log.err
- [x] p8-02-iso639.log.err / [x] p8-02-omw.log.err (54 lines, full)
- [x] p8-03-atomic2020.log.err / [x] p8-03-ud.log.err (400 lines; pages 1-211 + tail + grep)
- [x] par_1_0.{out,err}; par_4_0..3.{out,err}; par_8_0..7.{out,err} (all 26, dumped in full)
- [x] par_w_1_0.sql(+.err); par_w_4_0..3.sql(+.err); par_w_8_0..7.sql(+.err) (all 26, content + sizes)
- [x] parallel-gist-setup.sql / [x] parallel-gist-test.ps1
- [x] phase8-full.out / [x] phase8-ladder.out / [x] phase8-proof.ps1
- [x] roundtrip-sample.txt
- [x] run-asan-ud.ps1 / [x] run-ladder.ps1 / [x] run-rest.ps1
- [x] writepath-bench.sql / [x] writepath-bench.out
- [x] writepath-bench-phys.sql / [x] writepath-bench-phys.out

---

### FINDING 1 — Every full-UD (686-file) ingest attempt in the bucket FAILED; the success is faked by omission
SEVERITY: CRITICAL · CATEGORY: fake-test / correctness · CONFIDENCE: high

There are FOUR captured attempts at the full multilingual UD ingest. None completed:
- `cl-ud.err` (the headline "clean full 686" run, driven by `clean-ud.sh`): runs fresh (first batch
  `rows_new=35626e+34328p+168957a`, genuine), reaches `input_pct=68.9 files=552/686 ... elapsed_s=4085`
  then dies: `PostgresException: 3D000: database "laplace" does not exist` /
  `57P01: terminating connection due to administrator command`. NO `INGEST_COMPLETE`. The DB was
  dropped out from under the run (concurrent db-reset). 68 minutes to 69%, then crash.
- `p8-03-ud.log.err`: same crash signature — `database "laplace" does not exist` / terminating
  connection. No completion.
- `phase8-ladder.out` (line 9): `ud  exit=-1073741819  elapsed_s=1022.8  peak_rss_gb=11.51  (no readback)`
  → `STOP: ud failed (exit -1073741819)`. `0xC0000005` = **ACCESS VIOLATION (native segfault)** in the
  laplace_* DLLs. This matches the "AccessViolation crash" noted in memory `chess-substrate-design`.
- `asan-ud.out`: ASAN build, ends abruptly at epoch 32 / file 173 / 27% with no completion (killed/truncated).

The ONLY "successful" UD runs are `live-ud.*` (a 29-file **English-only subset**, completes in 73s) and
`live-udfull.*` (a **no-op** — completion marker already present, "0 intents applied, 0 novel entities").
The full multilingual pipeline — a core claim — is **unproven**, and every real attempt crashed.

### FINDING 2 — PHASE8-SUMMARY.tsv is a cherry-picked all-green summary that hides the UD crash
SEVERITY: CRITICAL · CATEGORY: fake-test / disparagement-inverse (faked success) · CONFIDENCE: high

`PHASE8-SUMMARY.tsv` contains 4 rows, all `exit=0`: unicode, iso639, atomic2020, wordnet. But the
same ladder’s own output (`phase8-ladder.out`, the "PHASE 8 SUMMARY" block) shows the real run was
10 sources and ended with `ud exit=-1073741819` (access violation) plus a `STOP: ud failed` line, and
also includes omw/verbnet/propbank/framenet/semlink that the .tsv omits. The two artifacts disagree.
The checked-in `.tsv` presents a curated green subset and silently drops the crashing source — a
faked-success summary. (`phase8-full.out` is a third, even shorter fragment: only unicode/iso639/wordnet,
stops before atomic2020 — also incomplete.) Three mutually-inconsistent "Phase 8" summaries, none of
which proves a full pipeline.

### FINDING 3 — The "<30 min, runs on a Pi (RAM O(batch))" mandate is contradicted by the artifacts
SEVERITY: HIGH · CATEGORY: invention-violation (perf mandate) · CONFIDENCE: high

- UD peak RSS `11.51 GB` (`phase8-ladder.out`) — the design mandate is peak RAM O(batch + fixed tables),
  independent of corpus size, runnable on a Pi. 11.5 GB is wildly over and grows with the corpus.
- Per-source wall times already blow the 30-min whole-pipeline budget: omw `971s`, atomic2020 `307s`,
  unicode `~95-146s`, wordnet `~107-150s`, and UD alone ran `1022s`/`4085s` before crashing — and the
  three largest sources (wiktionary 9GB, tatoeba, opensubtitles, conceptnet) were **never reached**
  because `phase8-proof.ps1` `break`s on UD's nonzero exit (`run-rest.ps1` was queued to run them after
  UD but UD never exited 0). No artifact proves the full pipeline under 30 min — or at all.

### FINDING 4 — OMW ingest artifact is truncated/aborted (no completion)
SEVERITY: HIGH · CATEGORY: fake-test · CONFIDENCE: high

`p8-02-omw.log.err` (54 lines, full) ends at `input_pct=24.8% ... status=running` with no
`INGEST_COMPLETE`. The run was cut off at ~25%. Yet `phase8-ladder.out` line 3/25 reports
`omw exit=0 elapsed_s=971.1` with a readback — so a *different* (uncaptured) OMW run is what the
ladder summarized; the only OMW log actually checked in documents an aborted run.

### FINDING 5 — asan-ud.out is a resumed run on a dirty DB; "rows_new=0" batches still re-fold ~600k relations each (double-count)
SEVERITY: MEDIUM · CATEGORY: correctness / fake-test · CONFIDENCE: med-high

`asan-ud.out` epochs 1–17 (files 1–118) all show `rows_new=0e+0p+0a` (content already present from a
prior partial run — so this is NOT a from-scratch proof). Despite 0 new attestations, each consensus
epoch still stages and folds 500k–1.8M relations (`consensus stage e14: 1,420,193 partial relations`,
`consensus fold e14: 1,420,193 relations materialized`). Re-ingesting already-present games re-folds
their testimony into consensus — exactly the double-count that `live-udfull.err` explicitly guards
against ("a re-ingest would double-count testimony into consensus"). The completion-marker guard only
fires for a fully-marked source; this partial-then-resumed path bypasses it. So asan-ud both (a) is not
a clean proof and (b) demonstrates duplicate testimony being folded. Verified by cross-reading
asan-ud.out batch lines vs the live-udfull.err guard message.

### FINDING 6 — The parallel-GiST scaling test produced ZERO usable data; all workers errored
SEVERITY: MEDIUM · CATEGORY: fake-test / dead-code · CONFIDENCE: high

Every `par_{1,4,8}_{w}.err` contains `ERROR: syntax error at end of input / LINE 1: SET` and the
matching `.out` files are 0 bytes — an earlier broken generation of the test where each worker’s SQL
was only "SET". The reworked generation (`par_w_*.sql`, valid 287-byte INSERTs, empty `.sql.err`) ran,
but the actual K=1/4/8 scaling rates were only `Write-Output` to console (`parallel-gist-test.ps1`
line 30) and were never redirected to a file — so the experiment’s conclusion is NOT in the repo. What
is checked in is purely failed-attempt + scratch artifacts proving nothing. Note the generated SQL uses
`ON CONFLICT DO NOTHING` (`parallel-gist-test.ps1` line 18), the anti-pattern the write-path mandate
forbids — acceptable in a synthetic bench, but it is benchmarking the wrong path.

### FINDING 7 — writepath microbenches use per-batch temp-table + ON CONFLICT (the path the mandate rejects), but the data is genuine and damning
SEVERITY: LOW · CATEGORY: perf / invention-violation(context) · CONFIDENCE: high

`writepath-bench-phys.out` is a real, internally-consistent measurement: NO-INDEX insert of 1M phys
rows = `1,166,773 rows/s`; with the live GiST `coord` index the same insert collapses to ~`34,188 rows/s`
(PATH A) / `35,514 rows/s` (PATH B) — i.e. the GiST nd-geometry index is the ~34x bottleneck that
caps the real ingest rate. This is good diagnostic evidence. Caveat: both A and B paths use
`ON CONFLICT DO NOTHING` and PATH A uses the per-batch `CREATE TEMP TABLE … promote … DROP` pattern —
the exact write shape CLAUDE.md §6 says to eliminate (no ON CONFLICT, no per-row anti-join; descent
already proved novelty). The bench measures the legacy path, not the mandated one. `writepath-bench.out`
(entities, no GiST) shows ~103k–110k rows/s — consistent and unremarkable.

### FINDING 8 — round_trips per apply is 8–11 (fan-out), never the tier-depth the invariant calls for
SEVERITY: LOW · CATEGORY: invention-violation(observability) · CONFIDENCE: med

Across every genuine run (cl-ud.err, live-ud.err, p8-*) each `INGEST_BATCH` reports `round_trips=8`
(UD/atomic) or `round_trips=11`/`13` (with index juggling). CLAUDE.md §6 states a correct ingest has
`round_trips ≈ tier-depth` and that an observed constant (~40 historically) is the apply fan-out
(`_applyPartitions × ops`), NOT a tier walk. The artifacts confirm the fan-out reading (constant 8/11
regardless of tier), i.e. the descent-shaped round-trip invariant is not what these numbers measure.
No `conflicts` counter is emitted at all, so the "conflicts ≈ 0" invariant cannot be verified from any
of these proofs — a gap in the instrumentation that is supposed to be the proof.

### FINDING 9 — Whole directory is checked-in run clutter, much of it misleading
SEVERITY: MEDIUM · CATEGORY: dead-code / other · CONFIDENCE: high

`.ingest-proof/` is 92 transient run artifacts: rotating logs (.err/.out), generated per-worker SQL
(`par_w_*.sql`), scratch `.stage`/`.sql.err` files, and three disagreeing summaries. They are not
tests, not fixtures, and not referenced by build. Several actively misrepresent state (Findings 1, 2, 4).
`ladder-cont.out` is a single stray line. Recommend deleting the directory (and git-ignoring it); if any
benchmark is worth keeping (`writepath-bench*.sql`, `bench-hilbert-order.sql`, `parallel-gist-*.{sql,ps1}`,
`phase8-proof.ps1`), move the *scripts* under `scripts/bench/` and drop all captured outputs.

### Genuinely-clean artifacts (for balance)
- `live-unicode.*`, `cl-uni.*`, `p8-01-unicode.log.err`: Unicode floor ingests, fresh, real
  `rows_new`, `INGEST_COMPLETE status=ok`, ~95–146s, ~1.8–2 GB. Genuine.
- `live-iso.*`, `cl-iso.*`, `p8-02-iso639.log.err`: ISO-639, ~2–7s, genuine completion.
- `p8-01-wordnet.log.err`, `p8-03-atomic2020.log.err`: complete with `status=ok` and plausible counts.
- `live-ud.*`: English 29-file subset, genuine completion in 73s.
These prove the small/floor sources work; they do not rescue Findings 1–4.

### Cross-cut observation (not unique to this bucket)
Every readback prints `entities/vocabulary (tier 0): 128/139`. A "vocabulary" bucket reported as a tier
is the `EntityTier.Vocabulary` kind-as-tier smell flagged in CLAUDE.md; here it is surfaced as a count
line. Also `check U+0041 'A': render: A tier=1` — codepoint 'A' rendering as tier=1 while described as
T0 elsewhere is a render/entity-tier labeling inconsistency. Both are signals for the code-side buckets,
noted here only because the proof readbacks expose them.

---

### Bucket summary
- CRITICAL: 2 (Finding 1 — all full-UD attempts failed; Finding 2 — PHASE8-SUMMARY.tsv fakes success by omission)
- HIGH: 2 (Finding 3 — <30min/Pi-RAM mandate contradicted; Finding 4 — OMW artifact aborted)
- MEDIUM: 3 (Finding 5 — dirty-DB re-fold/double-count; Finding 6 — parallel-GiST test yielded no data; Finding 9 — misleading clutter)
- LOW: 2 (Finding 7 — benches measure the rejected path; Finding 8 — round_trips=fan-out, no conflicts counter)

WORST ISSUE: Findings 1+2 together — the directory’s headline "proof" that the substrate ingests the
full corpus is the opposite of what the logs show. Three separate full-UD runs crashed (twice by the DB
being dropped mid-run, once by a native access violation at 11.5 GB RSS), and the one all-green summary
checked in (PHASE8-SUMMARY.tsv) achieves "green" only by listing 4 sources and omitting the crash. The
largest sources were never even reached. These artifacts document failure dressed as success.
