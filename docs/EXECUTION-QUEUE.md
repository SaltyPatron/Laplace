# Execution queue

**Status as of 2026-08-15.** Checkboxes below are a point-in-time
snapshot; re-verify against HEAD before treating any `[x]` as landed or any `[ ]` as
outstanding.

[x] landed   [~] written, not landed   [ ] not started

## 1. Per-file ingest journal — the file is the unit, not the run
- [x] `laplace.ingest_file_journal` table; DDL validated live, rolled back
- [x] Registered in manifest.install + manifest.upgrade
- [x] `OnFileStarted` / `OnFileFinished` on IIngestObservability
- [x] Npgsql writer, upsert on (run_id, file_label)
- [x] Orphan reconciliation: killed run's `running` file rows -> `cancelled`
- [x] `IngestObservabilityScope` ambient, so the static pipeline reaches it
- [x] Wired at all six boundaries; build green
- [x] Committed e41cea1c, branch pushed. NO PR OPENED.
- [ ] `files_total` = 0 in 16 of 36 run rows
- [ ] Kill-mid-run test

## 2. Ingest memory — bounded by file COUNT, not bytes
- [x] Measured: UD 686 files, 4,577 B to 360,217,466 B (78,000x), against
      file_workers=10 and a 4 GiB declared budget. OOM at 83,136,288 kB RSS,
      file 30/686, rows_new=0.
- [x] Presence preload ruled out (index_cycle_defer:false, PRELOAD unset in CI)
- [x] `_canonicalNames` ruled out — bounded vocabulary, not gigabytes
- [x] `ByteAdmissionGate` — byte-budget admission, wired into the parallel worker, builds green
- [ ] Measure RSS against the same UD corpus to confirm the ceiling holds

## 3. CI
- [x] #1102 checkout EACCES — merged
- [~] #1103 — ISA gate green + 201 BEGIN ATOMIC conversions. OPEN, UNMERGED.
      main stays red until it lands.
- [ ] `walk_branches` 7-arg + 8-arg overloads live; 4-arg callers error.
      Source already drops the 7-arg form; needs the extension upgrade.
- [ ] Re-dispatch omw / atomic2020 / conceptnet (gate bug fixed in 3bd8f25c).
      wiktionary alone — 6 hours.

## 4. SQL — 382 files
- [x] 201 converted to BEGIN ATOMIC, each validated live. THIS IS NOT THE
      REFACTOR — it is a syntax change that satisfies a gate. No query got faster.
- [x] Warm timings: recall 0.86ms, resolve 37ms, senses 799ms, bubble_up 743ms,
      salient_facts 2,420ms, relate_path 24,000-30,000ms
- [ ] relate_path — 18,303,185 shared buffer hits, 383 MB temp, two unbounded
      recursive arms expanded from both ends before the LCA join cuts to 1 row
- [ ] salient_facts 2.4s
- [ ] ~370 functions never read for performance

## 5. Chess
- [x] 20,866,751 attestations written by 3 incomplete runs -> 1,472,737 distinct.
      Converges. Partial, not corrupted.
- [ ] ChessPgn has never reached `ok`. Best run 41,900/875,671 units = 4.8%.

## 6. UD
- [x] Full run attempted. Killed at 21:12:31 by a Postgres restart, not by code.
- [ ] Re-run to completion
