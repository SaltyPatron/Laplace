# Ingest pipeline benchmark results

Reproducible benchmarks for the dedup-once / batched-probe / Hilbert-sorted bulk-append ingest refactor.

**Run script:** `scripts\win\bench-ingest.cmd` (sources `env.cmd`, isolates `laplace_bench` by default).

---

## Hardware

| Field | Value |
|-------|-------|
| Host | *(fill)* |
| CPU | *(fill — e.g. i9-14900KS 8P+16E)* |
| RAM | *(fill)* |
| Storage | *(fill — e.g. 2×990 EVO RAID-0)* |
| PostgreSQL | PG18, Windows native |
| Date | *(fill)* |

---

## Environment (record exact values)

| Variable | Value |
|----------|-------|
| `LAPLACE_BULK_FRESH` | |
| `LAPLACE_APPLY_PARTITIONS` | |
| `LAPLACE_INGEST_BATCH` | |
| `LAPLACE_INGEST_COMMIT_ROWS` | |
| `LAPLACE_INGEST_WORKERS` | |
| `LAPLACE_INGEST_COMPOSE_WORKERS` | |
| `LAPLACE_COMMIT_LANES` | |
| `LAPLACE_DECOMPOSE_WORKERS` | |
| Database | `laplace_bench` (isolated) or `laplace` |

---

## Dataset

| Field | Value |
|-------|-------|
| Source | `conceptnet` / `wiktionary` / `wiktionary-en` / `ud` |
| Path | |
| File count | |
| Total size | |
| Target | ~10 GB single-file (ConceptNet `assertions.csv` ≈ 9.5 GB) |

---

## Commands

```text
# 50k-unit slice (default) — safe smoke:
scripts\win\bench-ingest.cmd --confirm wiktionary-en

# Full corpus — explicit opt-in only:
scripts\win\bench-ingest.cmd --confirm --full conceptnet

# Plan only (no DB, no ingest):
scripts\win\bench-ingest.cmd --dry-run --confirm ud
```

Requires `LAPLACE_BULK_FRESH=0` (script sets this). `--force` on warm run re-ingests without
bulk-fresh bypass (fixed in IngestCommands.cs).

Raw logs: `docs\bench\runs\<timestamp>_<source>\`

---

## Results

### Cold run (fresh `laplace_bench`)

| Metric | Value |
|--------|-------|
| Wall clock (s) | |
| Input units / rows | |
| Units/s | |
| MB/s (file size ÷ wall) | |
| Intents applied | |
| Entities inserted | |
| Physicalities inserted | |
| Round-trips | |
| Consensus relations | |
| `entities_skipped` | 0 (expected) |
| `physicalities_skipped` | 0 (expected) |

### Warm run (`--force`, same DB)

| Metric | Value |
|--------|-------|
| Wall clock (s) | |
| Entities inserted | ≈ 0 (expected) |
| `entities_skipped` | high (dedup hit) |
| `physicalities_skipped` | high (dedup hit) |
| Round-trips | |

### Target check (10 GB single-file, few minutes)

| Criterion | Met? | Notes |
|-----------|------|-------|
| ≤ 5 min wall for ~10 GB | | |
| Warm entities ≈ 0 | | |
| Warm skips > 0 | | dedup-once working |

---

## Bottleneck hypothesis

*(fill after run — e.g. compose vs apply vs consensus fold vs disk)*

---

## Exact console excerpt

<details>
<summary>Cold run `done:` line</summary>

```text
(paste)
```

</details>

<details>
<summary>Warm run `done:` + MERGE_CONFLICT lines</summary>

```text
(paste)
```

</details>

---

## Blockers / limitations

- *(none / list missing data, extension stale, etc.)*

---

## Run history

| Date | Source | Size | Cold s | MB/s | Warm entities | Run dir |
|------|--------|------|--------|------|---------------|---------|
| | | | | | | |
