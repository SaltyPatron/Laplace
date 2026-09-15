# PostgreSQL geometry write baseline

This benchmark answers a narrow question: **how quickly does the selected PostgreSQL server persist the exact geometry payloads already present in Laplace?** It measures rows, trajectory vertices, serialized bytes, actual COPY transactions, and committed binary readback. It does not substitute storage-row throughput for native admission, recorded games, plies, or evidence-fold throughput.

## Run through the existing benchmark owner

Run on the measured PostgreSQL host, using the same libpq connection environment as the installed query benchmark:

```bash
python3 scripts/benchmark_suite.py run \
  --suite geometry \
  --database laplace \
  --receipt-dir /build/laplace/work/geometry-measurement-001 \
  --repeats 3 \
  --geometry-rows 100000 \
  --geometry-transaction-rows 10000 \
  --geometry-concurrency 1,2,4 \
  --geometry-max-bytes 536870912 \
  --geometry-timeout 900
```

The new output directory must not already exist. `PGHOST`, `PGPORT`, `PGUSER`, and other normal libpq variables select the connection. Defaults match the existing query benchmark: `/var/run/postgresql`, port 5432, and `laplace_admin`. Credentials stay in the environment. The harness needs `psql`, the existing PostGIS functions, read access to `laplace.physicalities`, and permission to create its own schema. It requires server `fsync=on` and requests `synchronous_commit=on` for its sessions; it does not change database-wide settings.

The existing `benchmark-evidence.yml` workflow also exposes the explicit `geometry` suite, under its existing shared host lock. It does not build or install a replacement Laplace core. Geometry is excluded from the default `all` suite because it creates logged benchmark tables.

## Exact input and two storage cases

The source selection is the first requested number of existing physicality IDs in byte order with non-null trajectories. If fewer unique rows exist, the receipt gives the actual count and marks the requested count as unmet. It never pads the input or generates new canonical IDs.

The harness initially snapshots only the selected IDs. PostgreSQL exports the complete selected physicality rows in bounded binary COPY streams before the harness loads any full-payload snapshot. The retained full rows then define one immutable input for every sample. Capture uses multiple source statements; it is not advertised as a single MVCC snapshot. Missing or changed row cardinality during capture rejects the run.

| Case | Stored columns and indexes |
|---|---|
| `geometry_heap` | Exact existing ID, coordinate and trajectory, with no secondary indexes or added primary key |
| `physicality_indexes` | Complete physicality rows, in a table created with `LIKE laplace.physicalities INCLUDING ALL` |

Both targets are **LOGGED**. The receipt retains the actual source and target column, index, constraint, trigger, persistence and partition descriptions. `LIKE INCLUDING ALL` does not copy foreign keys, user triggers, partition routing, or the source estate's existing index occupancy. Those differences prevent the cloned-table rate from being called the production writer's rate.

The payload receipt includes rows, unique IDs, coordinate vertices, trajectory vertices, dimensions, coordinate/trajectory EWKB byte counts, and minimum/median/p95/maximum trajectory sizes. Packed trajectory coordinates remain exact carriers; the benchmark does not interpret them as semantic positions or run path metrics over them.

## Timing and durability boundaries

Each transaction imports a disjoint retained input chunk using PostgreSQL's own binary COPY protocol. Concurrency is an actual limit on simultaneous client processes/connections. Every measured transaction finishes with acknowledged synchronous commit. The receipt records transaction outcomes independently; a failed sample retains any acknowledged transactions and does not emit a successful throughput result.

- **Write interval:** client process startup, connection, file transport, COPY, index/check work, synchronous commit acknowledgements, and coordinator progress bookkeeping. Each transaction also has its own measured interval.
- **Readback interval:** subsequent connections, aggregate count/size verification, complete binary export, and byte comparison. This interval is separate from writes.
- **Excluded from writes:** selecting source IDs, retaining input, loading the input snapshot, creating tables/indexes, truncating between samples, readback and cleanup.

Every committed output chunk must match its retained input in size, SHA-256, and an actual byte-for-byte comparison. Identical readback bytes reference the already retained input after verification; repeated samples do not multiply retained payload storage. A mismatching output is retained for diagnosis.

The byte envelope bounds combined retained binary inputs, not total database disk use, index/WAL space or process RSS. Selected IDs, row counts, concurrency, repeats and total runtime have separate finite limits. Full payloads are bounded before the input table is loaded. No cold-cache or restart claim is made. WAL insert/flush LSN deltas are explicitly cluster-wide and may include unrelated concurrent activity.

Creation and its random schema ownership comment share one transaction. Even after an ambiguous client failure, cleanup checks the exact schema owner and marker. It never drops an unowned same-name schema. The receipt retains the schema name and cleanup outcome.

## Compare with the normal writer and recorded games

The normal writer does more than store a geometry. `NpgsqlWorkingSetApply.cs` owns native COPY-blob collection, managed tuple parsing and deduplication, presence verification, the cross-process advisory apply lock, write epochs, replay journals, staged COPY, and the evidence/consensus transaction boundary. Its `WS_APPLY` lines report preparation, verification and COPY phases. `ApplyResult.RoundTrips` is a legacy logical phase counter; it is not a complete count of physical PostgreSQL protocol crossings. Actual COPY-transaction counters and commit acknowledgement receipts should be used when present.

The existing physicality throughput gate in `WriterThroughputTests.cs` stages coordinate-only rows with an empty trajectory. Its timed apply excludes preparation and the subsequent `CompleteBulkRunAsync` GIN drain. A threshold in that test is a requirement, not a measured claim about complete recorded games or long trajectories.

To attach a completed recorded-game case, add:

```bash
--geometry-recorded-case-dir /build/laplace/work/recorded-match/c1-r1
```

The harness calls the existing recorded-game collector's validator over its retained request, job, experiment, PGN, recording and receipt files. It preserves game/ply denominators, stage durations, writer attempts/inserts/skips, replay counters, and other validated fields. The recording schema does not by itself establish that it used the same PostgreSQL server; that limitation is explicit. A second new match is not a replay benchmark. The existing ingestion endpoint's unmeasured replay is not silently converted into replay throughput.

Interpret differences only after checking payload size, novelty, index/partition state, transaction sizes, concurrency, durability, and the measured boundary. Neither a fast COPY sample nor a slow full game establishes a universal PostgreSQL or Laplace throughput ceiling.
