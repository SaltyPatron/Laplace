# Execution queue — worked top to bottom, no reordering

Status legend: [x] landed  [~] in progress this turn  [ ] queued

## 1. Per-file ingest journal — the run is not the unit, the file is
- [x] `laplace.ingest_file_journal` table (run_id, file_label, source_name, file_id,
      status, started_at, ended_at, bytes, records, entities, physicalities,
      attestations, error). DDL validated live, rolled back.
- [x] Registered in `sql/manifest.install` + `sql/manifest.upgrade`.
- [x] `IIngestObservability.OnFileStarted` / `OnFileFinished` (default no-op, so no
      implementor breaks).
- [x] `NpgsqlIngestObservability` writes both, upsert-keyed on (run_id, file_label).
- [x] Orphan reconciliation drives a killed run's `running` file rows to `cancelled`.
- [~] Call sites in `IngestPipeline.cs`: parallel worker (skip at :780, start at :802,
      finish at :831) and sequential path (:526–534).
- [ ] `files_total` is 0 in 16 of 36 run rows — fix the inventory count at the same site.
- [ ] Test: kill mid-run, assert the mid-apply file is the one left `cancelled`.

## 2. Ingest memory is bounded by file COUNT, not bytes
- [x] Measured: UD's 686 treebanks span 4,577 B – 360,217,466 B (78,000x); knobs are
      `file_channel=30`, `file_workers=10`, and `working_set_budget_bytes=4 GiB`
      against a measured 83 GB RSS at OOM.
- [x] Eliminated the presence preload as the cause (run logged
      `index_cycle_defer: false`; `LAPLACE_PRESENCE_PRELOAD` set nowhere in CI).
- [ ] `_canonicalNames` (`ConcurrentDictionary<string,byte>`) is run-scoped and never
      cleared anywhere in the repo — 7 decomposers carry one. Scope it to the file.
- [ ] Admit files by byte budget instead of count.

## 3. CI
- [x] #1102 checkout EACCES — merged.
- [~] #1103 — ISA gate green + 201 BEGIN ATOMIC conversions. Open, needs merge.
- [ ] `consensus.walk_branches` has 7-arg and 8-arg overloads live; 4-arg callers
      (`converse_facts:104`, `recall_walk_response:34`) error as ambiguous. Source
      already drops the 7-arg form — needs the extension upgrade to land.
- [ ] Seed re-dispatch: omw / atomic2020 / conceptnet were failed by a gate bug already
      fixed in 3bd8f25c. wiktionary is a 6-hour run — alone, not behind others.

## 4. SQL performance — 382 files total
- [x] 201 converted to BEGIN ATOMIC, each validated live.
- [x] Measured warm: recall 0.86 ms, resolve 37 ms, senses 799 ms, bubble_up 743 ms,
      salient_facts 2,420 ms, **relate_path 24,000–30,000 ms**.
- [ ] `relate_path`: 18,303,185 shared buffer hits, 383 MB spilled to temp. Two
      unbounded recursive arms (`ux`, `uy`) expanded to p_depth from both ends before
      the LCA join cuts to 1 row.
- [ ] `salient_facts` 2.4 s.

## 5. Chess
- [x] Measured: 3 incomplete ChessPgn runs wrote 20,866,751 attestations; 1,472,737
      distinct. Content addressing converges — partial, not corrupted.
- [ ] ChessPgn has never reached `ok`. Best run: 41,900 / 875,671 input units (4.8%).
