#!/usr/bin/env python3
"""Measure exact existing GeometryZM payloads through logged COPY and committed readback.

This is a storage baseline. It never admits invented Laplace entities or changes
canonical tables. It creates and removes one uniquely owned benchmark schema.
Connection settings use normal libpq environment variables; no credentials are
written into receipts. The existing benchmark suite owns host scheduling.
"""
from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor, as_completed
import filecmp
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = "laplace.benchmark.postgres-geometry/v1"
SETTINGS = ("server_version", "fsync", "synchronous_commit", "full_page_writes",
            "wal_level", "wal_compression", "wal_sync_method", "shared_buffers",
            "work_mem", "maintenance_work_mem", "checkpoint_timeout", "max_wal_size",
            "default_transaction_isolation", "jit", "track_io_timing", "block_size",
            "synchronous_standby_names", "effective_io_concurrency", "max_parallel_workers")


def require(value, message):
    if not value:
        raise ValueError(message)


def ident(value):
    return '"' + value.replace('"', '""') + '"'


def literal(value):
    return "'" + value.replace("'", "''") + "'"


def save(path, value):
    path.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def artifact(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return {"path": str(path), "bytes": path.stat().st_size, "sha256": digest.hexdigest()}


def ranges(rows, transaction_rows):
    return [(start, min(rows, start + transaction_rows - 1))
            for start in range(1, rows + 1, transaction_rows)]


def validate(args):
    require(1 <= args.rows <= 1_000_000, "rows must be in 1..1000000")
    require(100 <= args.transaction_rows <= 1_000_000, "transaction rows must be in 100..1000000")
    require(1 <= args.repeats <= 10, "repeats must be in 1..10")
    require(1 <= args.max_bytes <= 4 << 30, "byte envelope must be in 1..4294967296")
    require(1 <= args.timeout <= 3600, "total timeout must be in 1..3600 seconds")
    concurrency = [int(value) for value in args.concurrency.split(",")]
    require(1 <= len(concurrency) <= 6 and len(set(concurrency)) == len(concurrency)
            and all(1 <= value <= 16 for value in concurrency),
            "concurrency must contain 1..6 distinct values in 1..16")
    require(bool(args.database) and "=" not in args.database and "://" not in args.database,
            "database must be a database name; use libpq environment variables for connections")
    return concurrency


class Pg:
    def __init__(self, database, timeout):
        self.executable = shutil.which("psql")
        require(self.executable, "psql is required on the measured host")
        self.deadline = time.monotonic() + timeout
        self.env = dict(os.environ)
        self.env["PGDATABASE"] = database
        # Match the existing installed query benchmark's libpq defaults.
        self.env.setdefault("PGHOST", "/var/run/postgresql")
        self.env.setdefault("PGPORT", "5432")
        self.env.setdefault("PGUSER", "laplace_admin")
        self.env["PGCONNECT_TIMEOUT"] = str(min(15, int(timeout)))
        self.env["PGOPTIONS"] = (self.env.get("PGOPTIONS", "")
            + " -c synchronous_commit=on -c statement_timeout=" + str(int(timeout * 1000))
            + " -c lock_timeout=15000")
        self.env["PGAPPNAME"] = "laplace-geometry-write-baseline"

    def command(self, sql):
        return [self.executable, "-X", "-w", "-q", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-c", sql]

    def execute(self, sql, *, source=None, destination=None):
        remaining = self.deadline - time.monotonic()
        require(remaining > 0, "benchmark exceeded its total time envelope")
        started = time.monotonic()
        result = subprocess.run(self.command(sql), env=self.env, stdin=source,
            stdout=destination if destination is not None else subprocess.PIPE,
            stderr=subprocess.PIPE, timeout=remaining, check=False)
        if result.returncode:
            # Do not propagate libpq connection strings or server data into logs.
            raise RuntimeError(f"psql operation failed with exit {result.returncode}; statement: {sql[:160]}")
        return (result.stdout or b""), time.monotonic() - started

    def json(self, sql):
        output, _ = self.execute(sql)
        return json.loads(output)

    def export(self, sql, path, maximum):
        # COPY remains PostgreSQL's binary serializer. Bound the retained stream
        # while it is written, including unexpectedly large TOASTed geometries.
        remaining = self.deadline - time.monotonic()
        require(remaining > 0, "benchmark exceeded its total time envelope")
        started = time.monotonic()
        with path.open("wb") as target:
            process = subprocess.Popen(self.command(sql), env=self.env, stdout=subprocess.PIPE,
                                       stderr=subprocess.DEVNULL)
            try:
                import selectors
                with selectors.DefaultSelector() as selector:
                    selector.register(process.stdout, selectors.EVENT_READ)
                    written = 0
                    while True:
                        require(self.deadline > time.monotonic(), "benchmark export timed out")
                        if not selector.select(timeout=min(1, self.deadline - time.monotonic())):
                            continue
                        block = os.read(process.stdout.fileno(), 1 << 20)
                        if not block:
                            break
                        written += len(block)
                        require(written <= maximum, "COPY payload exceeds the byte envelope")
                        target.write(block)
                require(process.wait(timeout=max(.01, self.deadline - time.monotonic())) == 0,
                        "PostgreSQL COPY export failed")
            finally:
                if process.poll() is None:
                    process.kill()
                process.wait()
                process.stdout.close()
        return time.monotonic() - started


def relation_sql(name):
    return f"""SELECT jsonb_build_object(
      'kind',c.relkind,'persistence',c.relpersistence,'options',c.reloptions,
      'partition_key',pg_get_partkeydef(c.oid),
      'direct_partitions',(SELECT count(*) FROM pg_inherits WHERE inhparent=c.oid),
      'columns',(SELECT jsonb_agg(jsonb_build_object('name',a.attname,
          'type',format_type(a.atttypid,a.atttypmod),'not_null',a.attnotnull,
          'generated',a.attgenerated) ORDER BY a.attnum)
          FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped),
      'indexes',(SELECT coalesce(jsonb_agg(jsonb_build_object('definition',pg_get_indexdef(i.indexrelid),
          'valid',i.indisvalid,'ready',i.indisready,'unique',i.indisunique,
          'options',ic.reloptions) ORDER BY i.indexrelid),'[]'::jsonb)
          FROM pg_index i JOIN pg_class ic ON ic.oid=i.indexrelid WHERE i.indrelid=c.oid),
      'constraints',(SELECT coalesce(jsonb_agg(jsonb_build_object('type',co.contype,
          'definition',pg_get_constraintdef(co.oid)) ORDER BY co.oid),'[]'::jsonb)
          FROM pg_constraint co WHERE co.conrelid=c.oid),
      'triggers',(SELECT coalesce(jsonb_agg(pg_get_triggerdef(t.oid) ORDER BY t.oid),'[]'::jsonb)
          FROM pg_trigger t WHERE t.tgrelid=c.oid AND NOT t.tgisinternal))
      FROM pg_class c WHERE c.oid={literal(name)}::regclass"""


def payload_sql(table):
    return f"""SELECT jsonb_build_object('rows',count(*),'unique_ids',count(DISTINCT id),
      'trajectory_vertices',coalesce(sum(ST_NPoints(trajectory)),0),
      'coordinate_vertices',coalesce(sum(ST_NPoints(coord)),0),
      'coordinate_ewkb_bytes',coalesce(sum(octet_length(ST_AsEWKB(coord))),0),
      'trajectory_ewkb_bytes',coalesce(sum(octet_length(ST_AsEWKB(trajectory))),0),
      'geometry_ewkb_bytes',coalesce(sum(coalesce(octet_length(ST_AsEWKB(coord)),0)
          +coalesce(octet_length(ST_AsEWKB(trajectory)),0)),0),
      'minimum_trajectory_vertices',min(ST_NPoints(trajectory)),
      'maximum_trajectory_vertices',max(ST_NPoints(trajectory)),
      'median_trajectory_vertices',percentile_cont(0.5) WITHIN GROUP (ORDER BY ST_NPoints(trajectory)),
      'p95_trajectory_vertices',percentile_cont(0.95) WITHIN GROUP (ORDER BY ST_NPoints(trajectory)),
      'trajectory_dimensions',array_agg(DISTINCT ST_NDims(trajectory) ORDER BY ST_NDims(trajectory)),
      'coordinate_dimensions',array_agg(DISTINCT ST_NDims(coord) ORDER BY ST_NDims(coord)),
      'coordinate_types',array_agg(DISTINCT GeometryType(coord) ORDER BY GeometryType(coord)),
      'trajectory_types',array_agg(DISTINCT GeometryType(trajectory) ORDER BY GeometryType(trajectory))) FROM {table}"""


def copy_out(table, columns, where=""):
    return f"COPY (SELECT {columns} FROM {table} {where} ORDER BY id) TO STDOUT (FORMAT BINARY)"


def verify_readback(item, path):
    proof = artifact(path)
    require(proof["bytes"] == item["bytes"] and proof["sha256"] == item["sha256"]
            and filecmp.cmp(item["path"], path, shallow=False),
            "committed binary readback differs from exact input")
    # Retain one exact copy of identical bytes, bounding storage independently
    # of the number of samples/concurrency points. A failed comparison retains
    # the differing output for diagnosis.
    del proof["path"]
    proof["identical_retained_input"] = item["path"]
    proof["byte_comparison"] = True
    path.unlink()
    return proof


def cleanup_owned_schema(pg, schema, marker):
    ownership = pg.json("SELECT coalesce((SELECT jsonb_build_object('same_owner',nspowner=(SELECT oid"
        " FROM pg_roles WHERE rolname=current_user),'marker',obj_description(oid,'pg_namespace'))"
        " FROM pg_namespace WHERE nspname=" + literal(schema) + "),'null'::jsonb)")
    if ownership is None:
        return "benchmark schema absent"
    require(ownership["same_owner"] is True and ownership["marker"] == marker,
            "cleanup refused: schema ownership marker differs")
    pg.execute(f"DROP SCHEMA {ident(schema)} CASCADE")
    return "owned benchmark schema removed"


def copy_transactions(pg, target, columns, fixtures, concurrency, case, checkpoint):
    case["transactions"] = [{"chunk": index, "submitted_rows": item["rows"],
        "copy_bytes": item["bytes"], "input_sha256": item["sha256"], "status": "not_started"}
        for index, item in enumerate(fixtures)]

    def write(index):
        item, observation = fixtures[index], case["transactions"][index]
        observation["status"] = "running"
        started = time.monotonic()
        try:
            with Path(item["path"]).open("rb") as stream:
                pg.execute(f"COPY {target} ({columns}) FROM STDIN (FORMAT BINARY)", source=stream)
            observation["status"] = "synchronous_commit_acknowledged"
        except (ValueError, RuntimeError, OSError, subprocess.SubprocessError) as error:
            observation["status"] = "failed_or_commit_not_acknowledged"
            observation["error"] = str(error) if isinstance(error, (ValueError, RuntimeError)) else type(error).__name__
        finally:
            observation["copy_and_commit_seconds"] = time.monotonic() - started
        return observation["status"] == "synchronous_commit_acknowledged"

    started = time.monotonic()
    failed = False
    try:
        with ThreadPoolExecutor(max_workers=concurrency) as pool:
            pending = [pool.submit(write, index) for index in range(len(fixtures))]
            for future in as_completed(pending):
                if not future.cancelled() and not future.result():
                    failed = True
                    for other in pending:
                        other.cancel()
                checkpoint()
    finally:
        case["copy_and_commit_seconds"] = time.monotonic() - started
        case["acknowledged_transactions"] = sum(
            item["status"] == "synchronous_commit_acknowledged" for item in case["transactions"])
        checkpoint()
    require(not failed, "COPY case failed; retained transaction outcomes identify partial acknowledged work")
    return case["copy_and_commit_seconds"]


def recording_link(directory):
    if directory is None:
        return {"status": "not_supplied", "replay": "not_measured"}
    owner = ROOT / "scripts/benchmark-recorded-chess.py"
    spec = importlib.util.spec_from_file_location("recorded_chess_baseline_owner", owner)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    names = ("recording.json", "experiment.json", "job.json", "request.json", "games.pgn", "receipt.json")
    bodies = {name: (directory / name).read_bytes() for name in names}
    case = json.loads(bodies["receipt.json"])
    require(case.get("status") == "passed", "supplied recorded-game case did not pass")
    metrics = module.validate_recording(json.loads(bodies["recording.json"]),
        json.loads(bodies["experiment.json"]), json.loads(bodies["job.json"]),
        json.loads(bodies["request.json"]), bodies["games.pgn"], bodies["experiment.json"],
        case["collectorWallSeconds"])
    return {"status": "validated_by_existing_recording_owner", "metrics": metrics,
            "artifacts": [artifact(directory / name) for name in names],
            "same_server": "not_established_by_recording_schema_v1",
            "writer_roundTrips_scope": "legacy logical phase counter; not total physical protocol round trips",
            "replay": "not_measured; a second new match is not an ingestion replay"}


def run(args):
    concurrencies = validate(args)
    args.output_dir.mkdir(parents=True, exist_ok=False)
    report = {"schema": SCHEMA, "status": "failed", "cases": [],
              "source_table": "laplace.physicalities", "requested_rows": args.rows,
              "scope": "Primitive storage of exact existing payloads; no canonical admission, evidence fold or gameplay"}
    schema = "laplace_geometry_bench_" + uuid.uuid4().hex
    namespace = ident(schema)
    snapshot = namespace + '."input"'
    selected_ids = namespace + '."selected_ids"'
    marker = "laplace-geometry-benchmark:" + uuid.uuid4().hex
    creation_attempted = False
    active_case = None
    try:
        report["recorded_game_comparison"] = recording_link(args.recorded_case_dir)
        pg = Pg(args.database, args.timeout)
        report["owned_schema"] = schema
        report["source_revision"] = subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
        report["harness"] = artifact(Path(__file__).resolve())
        report["client"] = {"platform": platform.platform(), "architecture": platform.machine(),
            "hostname": platform.node(), "cpu_affinity": sorted(os.sched_getaffinity(0)),
            "psql": artifact(Path(pg.executable).resolve())}
        report["server"] = pg.json("""SELECT jsonb_build_object('version',version(),
            'database',current_database(),'database_oid',(SELECT oid FROM pg_database WHERE datname=current_database()),
            'address',inet_server_addr(),'port',inet_server_port(),'postmaster_started',pg_postmaster_start_time(),
            'postgis',postgis_full_version(),'recovery',pg_is_in_recovery())""")
        report["settings"] = pg.json("SELECT jsonb_object_agg(name,setting) FROM pg_settings WHERE name IN ("
            + ",".join(literal(name) for name in SETTINGS) + ")")
        try:
            report["server"]["system_identifier"] = pg.json(
                "SELECT to_jsonb(system_identifier::text) FROM pg_control_system()")
        except RuntimeError:
            report["server"]["system_identifier"] = None
            report["server"]["system_identifier_status"] = "not accessible to the benchmark role"
        require(report["settings"]["fsync"] == "on" and report["settings"]["synchronous_commit"] == "on",
                "durable baseline requires server fsync=on and session synchronous_commit=on")
        require(report["server"]["recovery"] is False, "baseline requires a writable primary")
        source = pg.json(relation_sql("laplace.physicalities"))
        report["source_schema"] = source
        columns = [column["name"] for column in source["columns"] if not column["generated"]]
        require(len(columns) == len(source["columns"]),
                "full-row baseline requires explicit support for generated physicality columns")
        require({"id", "coord", "trajectory"} <= set(columns), "physicality geometry columns are missing")
        require("baseline_ordinal" not in columns, "source conflicts with the benchmark ordinal")
        full = ",".join(ident(name) for name in columns)
        geometry = '"id","coord","trajectory"'
        report["schema_ownership_marker"] = marker
        save(args.output_dir / "receipt.json", report)
        creation_attempted = True
        pg.execute(f"BEGIN; CREATE SCHEMA {namespace}; COMMENT ON SCHEMA {namespace} IS {literal(marker)}; COMMIT")
        preparation_started = time.monotonic()
        # Only bounded native IDs enter PostgreSQL before full payload export.
        # Large TOASTed rows are streamed through the byte guard before any
        # complete payload table is loaded in the benchmark namespace.
        pg.execute(f"CREATE TABLE {selected_ids} AS SELECT row_number() OVER (ORDER BY id)"
            f" AS baseline_ordinal,id FROM laplace.physicalities WHERE trajectory IS NOT NULL"
            f" ORDER BY id LIMIT {args.rows}")
        selected = pg.json(f"SELECT jsonb_build_object('rows',count(*),'unique_ids',count(DISTINCT id)) FROM {selected_ids}")
        report["source_selection"] = {"order": "physicality id ascending; non-null trajectories",
            "actual_unique_rows": selected["unique_ids"], "requested_rows_satisfied": selected["rows"] == args.rows,
            "capture_consistency": "one selected-ID statement, followed by bounded full-row exports; retained rows define the immutable measured payload",
            "snapshot_is_logged": True, "synthetic_canonical_ids": False}
        require(0 < selected["rows"] == selected["unique_ids"] <= args.rows,
                "source must supply at least one existing uniquely identified trajectory row")
        chunks = ranges(selected["rows"], args.transaction_rows)
        fixtures = {"geometry_heap": [], "physicality_indexes": []}
        report["fixtures"] = fixtures
        total_bytes = 0
        for index, (low, high) in enumerate(chunks):
            path = args.output_dir / f"physicality_indexes-input-{index:04d}.copy"
            pg.export(copy_out("laplace.physicalities", full,
                f"WHERE id IN (SELECT id FROM {selected_ids} WHERE baseline_ordinal BETWEEN {low} AND {high})"),
                path, args.max_bytes - total_bytes)
            item = artifact(path)
            total_bytes += item["bytes"]
            item.update(rows=high-low+1, low=low, high=high)
            fixtures["physicality_indexes"].append(item)
        pg.execute(f"CREATE TABLE {snapshot} AS SELECT {full} FROM laplace.physicalities WITH NO DATA")
        for item in fixtures["physicality_indexes"]:
            with Path(item["path"]).open("rb") as stream:
                pg.execute(f"COPY {snapshot} ({full}) FROM STDIN (FORMAT BINARY)", source=stream)
        payload = pg.json(payload_sql(snapshot))
        report["payload"] = payload
        require(payload["rows"] == payload["unique_ids"] == selected["rows"],
                "source row identities changed or disappeared during bounded capture")
        require(payload["geometry_ewkb_bytes"] <= args.max_bytes,
                "selected geometry payload exceeds the byte envelope")
        require(payload["trajectory_dimensions"] == [4] and payload["coordinate_dimensions"] == [4],
                "selected coordinates and trajectories must preserve GeometryZM dimensions")
        for index, (low, high) in enumerate(chunks):
            path = args.output_dir / f"geometry_heap-input-{index:04d}.copy"
            pg.export(copy_out(snapshot, geometry,
                f"WHERE id IN (SELECT id FROM {selected_ids} WHERE baseline_ordinal BETWEEN {low} AND {high})"),
                path, args.max_bytes - total_bytes)
            item = artifact(path)
            total_bytes += item["bytes"]
            item.update(rows=high-low+1, low=low, high=high)
            fixtures["geometry_heap"].append(item)
        report["source_selection"]["snapshot_and_serialization_seconds"] = time.monotonic() - preparation_started
        tables = {mode: namespace + '.' + ident(mode) for mode in fixtures}
        pg.execute(f"CREATE TABLE {tables['geometry_heap']} AS SELECT {geometry} FROM {snapshot} WITH NO DATA")
        pg.execute(f"CREATE TABLE {tables['physicality_indexes']} (LIKE laplace.physicalities INCLUDING ALL)")
        report["target_schemas"] = {mode: pg.json(relation_sql(schema + '.' + mode)) for mode in tables}
        require(all(info["persistence"] == "p" for info in report["target_schemas"].values()),
                "all measured target tables must be LOGGED")
        require(len(report["target_schemas"]["physicality_indexes"]["indexes"]) == len(source["indexes"]),
                "schema clone did not preserve the complete source index set")
        report["schema_clone_limits"] = ("INCLUDING ALL copies columns/defaults/checks/indexes/storage, not foreign keys, "
            "user triggers, partition routing or existing-table/index occupancy; exact source and target definitions are retained")
        for concurrency in concurrencies:
            for repeat in range(args.repeats):
                modes = list(tables)
                if repeat % 2:
                    modes.reverse()
                for mode in modes:
                    target = tables[mode]
                    active_case = {"mode": mode, "concurrency": concurrency, "repeat": repeat+1,
                        "status": "running", "transaction_rows_ceiling": args.transaction_rows,
                        "scheduled_transactions": len(fixtures[mode]),
                        "parallel_connections_ceiling": min(concurrency,len(fixtures[mode])),
                        "readbacks": []}
                    report["cases"].append(active_case)
                    save(args.output_dir / "receipt.json", report)
                    pg.execute(f"TRUNCATE {target}")
                    before = pg.json("SELECT jsonb_build_object('insert',pg_current_wal_insert_lsn(),'flush',pg_current_wal_flush_lsn())")
                    selected_columns = geometry if mode == "geometry_heap" else full
                    active_case["wal_before"] = before
                    write_seconds = copy_transactions(pg, target, selected_columns, fixtures[mode], concurrency,
                        active_case, lambda: save(args.output_dir / "receipt.json", report))
                    after = pg.json("SELECT jsonb_build_object('insert',pg_current_wal_insert_lsn(),'flush',pg_current_wal_flush_lsn())")
                    wal = pg.json("SELECT jsonb_build_object('insert_bytes',pg_wal_lsn_diff("
                        + literal(after["insert"]) + "::pg_lsn," + literal(before["insert"]) + "::pg_lsn),"
                        + "'flush_bytes',pg_wal_lsn_diff(" + literal(after["flush"]) + "::pg_lsn,"
                        + literal(before["flush"]) + "::pg_lsn))")
                    read_started = time.monotonic()
                    observed = pg.json(payload_sql(target))
                    require(observed == payload, "committed geometry payload counts differ")
                    readbacks = active_case["readbacks"]
                    for index, item in enumerate(fixtures[mode]):
                        path = args.output_dir / f"{mode}-c{concurrency}-r{repeat+1}-readback-{index:04d}.copy"
                        pg.export(copy_out(target, selected_columns, f"WHERE id IN (SELECT id FROM {selected_ids}"
                            f" WHERE baseline_ordinal BETWEEN {item['low']} AND {item['high']})"), path, item["bytes"])
                        readbacks.append(verify_readback(item, path))
                    read_seconds = time.monotonic() - read_started
                    active_case.update({"status": "passed", "readback_seconds": read_seconds,
                        "rows_per_second": payload["rows"] / write_seconds,
                        "trajectory_vertices_per_second": payload["trajectory_vertices"] / write_seconds,
                        "geometry_ewkb_bytes_per_second": payload["geometry_ewkb_bytes"] / write_seconds,
                        "copy_bytes": sum(item["bytes"] for item in fixtures[mode]),
                        "wal": {**wal, "before": before, "after": after,
                            "scope": "cluster-wide LSN deltas; may include concurrent unrelated activity"},
                        "exact_committed_readback": True})
                    save(args.output_dir / "receipt.json", report)
        report["timing"] = {"write": "concurrent psql process start, connect, binary COPY transport, index/check work and acknowledged synchronous COMMIT for every transaction",
            "readback": "new connections, committed aggregate query and complete binary export/byte comparison; measured separately",
            "excluded_from_write": "source snapshot, fixture serialization, table/index creation, TRUNCATE, readback and cleanup",
            "cache_state": "no server restart or cache eviction; retained inputs and repeated samples are warm; no cold-storage claim"}
        report["status"] = "passed"
    except (ValueError, RuntimeError, KeyError, TypeError, OSError, subprocess.SubprocessError, KeyboardInterrupt) as error:
        report["error"] = str(error) if isinstance(error, (ValueError, RuntimeError)) else type(error).__name__
        if active_case is not None and active_case["status"] == "running":
            active_case["status"] = "failed"
            active_case["error"] = report["error"]
    finally:
        if creation_attempted:
            try:
                pg.deadline = max(pg.deadline, time.monotonic() + 30)
                report["cleanup"] = cleanup_owned_schema(pg, schema, marker)
            except (ValueError, RuntimeError, OSError, subprocess.SubprocessError):
                report["status"] = "failed"
                report["cleanup"] = "failed; owned schema " + schema + " remains"
        save(args.output_dir / "receipt.json", report)
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", default=os.environ.get("PGDATABASE", "laplace"))
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--rows", type=int, default=100000)
    parser.add_argument("--transaction-rows", type=int, default=10000)
    parser.add_argument("--concurrency", default="1,2,4")
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--max-bytes", type=int, default=512 << 20)
    parser.add_argument("--timeout", type=float, default=900)
    parser.add_argument("--recorded-case-dir", type=Path)
    args = parser.parse_args(argv)
    try:
        report = run(args)
    except (ValueError, OSError) as error:
        print(type(error).__name__ + ": " + str(error), flush=True)
        return 1
    print(json.dumps({"schema": SCHEMA, "status": report["status"],
                      "receipt": str(args.output_dir / "receipt.json")}), flush=True)
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
