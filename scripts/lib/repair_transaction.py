"""Durable pre-mutation receipts for a bounded, caller-owned PostgreSQL repair.

The caller supplies fixed reviewed SQL, including evidence eligibility and locks.
This module owns only the psql transaction/receipt boundary. It cannot select rows,
calculate identities, or activate a repair on its own.
"""
from __future__ import annotations

import hashlib
import json
import math
import os
from pathlib import Path
import selectors
import shutil
import subprocess
import time
import uuid


# PostgreSQL 18 implements FETCH_COUNT with libpq chunked result delivery, so
# SELECT stays one set operation while psql releases each group after printing.
# Eight admitted 2 MiB records carry at most 16 MiB of JSON payload per group
# (plus client allocation overhead). Larger caller-declared record bounds reduce
# the group size; an oversized individual record still fails the line protocol.
PSQL_FETCH_MAX_ROWS = 8
PSQL_FETCH_PAYLOAD_BYTES = 16 * 1024 * 1024
RESOURCE_RECORD_MAX_BYTES = 64 * 1024
RESOURCE_SCHEMA = "laplace.legacy-content-repair-resources/v1"
RESOURCE_FIELDS = ("physicality_rows", "native_input_rows", "context_rows", "summary_rows",
                   "plan_bytes", "max_line_bytes", "max_jsonl_line_bytes")


class RepairProtocolError(RuntimeError):
    pass


def sync_directory(directory: Path) -> None:
    descriptor = os.open(directory, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def write_new_json(path: Path, value: dict) -> None:
    """Create, flush and durably link one receipt without replacing old evidence."""
    with path.open("xb") as target:
        target.write((json.dumps(value, sort_keys=True) + "\n").encode())
        target.flush()
        os.fsync(target.fileno())
    sync_directory(path.parent)


class PsqlTransaction:
    def __init__(self, command: list[str], errors, *, timeout: int, max_line_bytes: int):
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=errors, bufsize=0)
        self.timeout = timeout
        self.max_line_bytes = max_line_bytes
        self.pending = bytearray()
        assert self.process.stdin is not None
        os.set_blocking(self.process.stdin.fileno(), False)
        self.begin_phase()

    def begin_phase(self) -> None:
        """One finite budget covers both transmitting SQL and receiving its barrier."""
        self.deadline = time.monotonic() + self.timeout

    def remaining(self) -> float:
        remaining = self.deadline - time.monotonic()
        if remaining <= 0:
            raise RepairProtocolError("repair transaction phase timed out")
        return remaining

    def send(self, sql: str) -> None:
        assert self.process.stdin is not None
        remaining = memoryview(sql.encode())
        with selectors.DefaultSelector() as selector:
            selector.register(self.process.stdin, selectors.EVENT_WRITE)
            while remaining:
                if not selector.select(self.remaining()):
                    raise RepairProtocolError("repair transaction phase timed out while sending SQL")
                self.remaining()
                try:
                    sent = os.write(self.process.stdin.fileno(), remaining)
                except BlockingIOError:
                    continue
                if not sent:
                    raise RepairProtocolError("database connection stopped accepting repair SQL")
                remaining = remaining[sent:]

    def lines(self, marker: str):
        """Read bounded newline-delimited JSONB output until a psql echo barrier."""
        assert self.process.stdout is not None
        with selectors.DefaultSelector() as selector:
            selector.register(self.process.stdout, selectors.EVENT_READ)
            while True:
                self.remaining()
                while b"\n" in self.pending:
                    self.remaining()
                    raw, _, tail = self.pending.partition(b"\n")
                    self.pending = bytearray(tail)
                    if len(raw) > self.max_line_bytes:
                        raise RepairProtocolError("repair row exceeds byte bound")
                    if raw == marker.encode():
                        return
                    if raw:
                        yield raw
                if len(self.pending) > self.max_line_bytes:
                    raise RepairProtocolError("repair row exceeds byte bound")
                if not selector.select(self.remaining()):
                    raise RepairProtocolError("repair transaction response timed out")
                chunk = os.read(self.process.stdout.fileno(), 65536)
                if not chunk:
                    raise RepairProtocolError("database connection ended before phase confirmation")
                self.pending.extend(chunk)

    def close(self) -> None:
        # EOF disconnects an open transaction and PostgreSQL rolls it back. Never
        # send COMMIT as cleanup, including after a receipt write/fsync failure.
        if self.process.stdin is not None:
            self.process.stdin.close()
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.terminate()
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()
        if self.process.stdout is not None:
            self.process.stdout.close()


def parse_record(raw: bytes) -> dict:
    try:
        value = json.loads(raw)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise RepairProtocolError("database emitted invalid repair JSON") from error
    if not isinstance(value, dict):
        raise RepairProtocolError("database repair record must be an object")
    return value


def validate_resources(resources: dict) -> None:
    if resources.get("kind") != "resources" or resources.get("schema") != RESOURCE_SCHEMA:
        raise RepairProtocolError("repair resource summary has an unknown schema")
    for field in RESOURCE_FIELDS:
        if type(resources.get(field)) is not int or resources[field] < 0:
            raise RepairProtocolError("repair resource summary lacks a nonnegative integer " + field)
    if resources["context_rows"] != 1 or resources["summary_rows"] != 1 \
            or resources["max_line_bytes"] <= 0 \
            or resources["max_jsonl_line_bytes"] != resources["max_line_bytes"] + 1:
        raise RepairProtocolError("repair resource summary has inconsistent record structure")


def resource_rejections(resources: dict, *, max_rows: int, max_native_inputs: int,
                        max_bytes: int, max_line_bytes: int, disk_free_bytes: int,
                        auxiliary_reserve_bytes: int) -> list[str]:
    rejected = []
    for field, limit in (("physicality_rows", max_rows), ("native_input_rows", max_native_inputs),
                         ("plan_bytes", max_bytes), ("max_line_bytes", max_line_bytes)):
        if resources[field] > limit:
            rejected.append(f"{field}={resources[field]} exceeds declared limit {limit}")
    required = resources["plan_bytes"] + auxiliary_reserve_bytes
    if required > disk_free_bytes:
        rejected.append(f"receipt and auxiliary reserve require {required} bytes; {disk_free_bytes} available")
    return rejected


def preserve_and_apply(command: list[str], plan_sql: str, apply_sql: str,
                       directory: Path, *, source_sha: str, max_rows: int,
                       max_bytes: int = 512 * 1024 * 1024,
                       max_line_bytes: int = 2 * 1024 * 1024,
                       max_native_inputs: int = 100000,
                       timeout: int = 180,
                       persistence_timeout: int = 60,
                       receipt_sql: str | None = None,
                       measurement_only: bool = False,
                       auxiliary_reserve_bytes: int = 0) -> dict:
    """Keep the same database transaction open while its complete plan is fsynced.

    plan_sql yields context, native-input and physicality JSONB records, then a
    plan summary with both counts and unresolved evidence. apply_sql validates
    and updates that exact locked plan and yields one applied(count) record.
    Each phase shares one timeout across SQL send and complete response receipt.
    Final pre-apply flush/fsyncs share a separate persistence_timeout; an overrun
    retains evidence and forbids submission. This function supplies BEGIN/COMMIT.
    With receipt_sql, plan_sql emits one resource summary; only an admitted,
    durable summary permits the separately submitted receipt stream. Both use
    the same planning deadline. measurement_only confirms ROLLBACK at that
    barrier and reports sizing, including envelope rejections, without streaming.
    There is no arbitrary SQL command-line entry point.
    """
    if max_rows < 0 or max_native_inputs < 0 or max_bytes <= 0 or max_line_bytes <= 0:
        raise ValueError("repair bounds must be nonnegative rows and positive bytes")
    if not math.isfinite(timeout) or not math.isfinite(persistence_timeout) \
            or timeout <= 0 or persistence_timeout <= 0:
        raise ValueError("repair phase and persistence timeouts must be finite and positive")
    if type(auxiliary_reserve_bytes) is not int or auxiliary_reserve_bytes < 0:
        raise ValueError("repair auxiliary reserve must be nonnegative integer bytes")
    if measurement_only and receipt_sql is None:
        raise ValueError("repair measurement requires the separate resource barrier")
    if receipt_sql is not None and not receipt_sql.strip():
        raise ValueError("repair receipt SQL must be nonempty")
    fetch_rows = max(1, min(PSQL_FETCH_MAX_ROWS, PSQL_FETCH_PAYLOAD_BYTES // max_line_bytes))
    directory.mkdir(mode=0o770, parents=False, exist_ok=False)
    sync_directory(directory.parent)
    token = uuid.uuid4().hex
    plan_marker, commit_marker = f"PLAN_{token}", f"COMMITTED_{token}"
    plan_hash = hashlib.sha256()
    count = native_inputs = total_bytes = largest_line = 0
    context = summary = None
    resources = resources_hash = disk_free = None
    submitted = confirmed = False
    tx = None
    started = time.time_ns()
    try:
        with (directory / "database-errors.log").open("xb") as errors:
            tx = PsqlTransaction(command, errors, timeout=timeout,
                                 max_line_bytes=RESOURCE_RECORD_MAX_BYTES if receipt_sql is not None else max_line_bytes)
            tx.send("\\set ON_ERROR_STOP on\n\\set SHOW_ALL_RESULTS on\n"
                    f"\\set FETCH_COUNT {fetch_rows}\n"
                    "BEGIN ISOLATION LEVEL SERIALIZABLE;\n"
                    "SET LOCAL client_encoding='UTF8';\n"
                    "SET LOCAL lock_timeout='10s';\n"
                    f"SET LOCAL statement_timeout='{timeout}s';\n"
                    # The server may finish before buffered rows reach Python.
                    # Its idle window covers the remaining receive phase plus
                    # the separately bounded durable-persistence interval.
                    f"SET LOCAL idle_in_transaction_session_timeout='{timeout + persistence_timeout}s';\n"
                    + plan_sql + f"\n\\echo {plan_marker}\n")
            if receipt_sql is not None:
                for raw in tx.lines(plan_marker):
                    if resources is not None:
                        raise RepairProtocolError("repair planning emitted more than one resource summary")
                    resources = parse_record(raw)
                if resources is None:
                    raise RepairProtocolError("repair planning omitted its resource summary")
                # Preserve complete measured counts even when the proposed
                # receipt is too large to admit under this invocation's limits.
                write_new_json(directory / "resources.json", resources)
                resources_hash = hashlib.sha256(
                    (json.dumps(resources, sort_keys=True) + "\n").encode()).hexdigest()
                tx.remaining()
                validate_resources(resources)
                disk_free = shutil.disk_usage(directory).free
                rejected = resource_rejections(resources, max_rows=max_rows,
                    max_native_inputs=max_native_inputs, max_bytes=max_bytes,
                    max_line_bytes=max_line_bytes, disk_free_bytes=disk_free,
                    auxiliary_reserve_bytes=auxiliary_reserve_bytes)
                if measurement_only:
                    rollback_marker = f"ROLLED_BACK_{token}"
                    tx.begin_phase()
                    tx.send(f"ROLLBACK;\n\\echo {rollback_marker}\n")
                    for raw in tx.lines(rollback_marker):
                        raise RepairProtocolError("repair measurement emitted unexpected rollback output")
                    measurement = {"schema": "laplace.legacy-content-repair-measurement/v1",
                        "disposition": "measurement-only-rollback-confirmed", "source_sha": source_sha,
                        "resources": resources, "resources_sha256": resources_hash,
                        "envelope_admitted": not rejected, "resource_rejections": rejected,
                        "max_rows": max_rows, "max_native_inputs": max_native_inputs,
                        "max_bytes": max_bytes, "max_line_bytes": max_line_bytes,
                        "auxiliary_reserve_bytes": auxiliary_reserve_bytes,
                        "disk_free_bytes": disk_free, "timeout_seconds": timeout,
                        "plan_sql_sha256": hashlib.sha256(plan_sql.encode()).hexdigest(),
                        "receipt_sql_sha256": hashlib.sha256(receipt_sql.encode()).hexdigest(),
                        "finished_unix_nanoseconds": time.time_ns()}
                    write_new_json(directory / "measurement.json", measurement)
                    return measurement
                if rejected:
                    measured = {field: resources[field] for field in RESOURCE_FIELDS}
                    raise RepairProtocolError("repair resource envelope rejected: " + "; ".join(rejected)
                        + "; measured=" + json.dumps(measured, sort_keys=True)
                        + "; resources_receipt=" + str(directory / "resources.json"))
                tx.max_line_bytes = max_line_bytes
                plan_marker = f"RECEIPT_{token}"
                tx.send(receipt_sql + f"\n\\echo {plan_marker}\n")
            with (directory / "plan.jsonl").open("xb") as receipt:
                for raw in tx.lines(plan_marker):
                    total_bytes += len(raw) + 1
                    largest_line = max(largest_line, len(raw))
                    if total_bytes > max_bytes:
                        raise RepairProtocolError("repair receipt exceeds byte bound")
                    record = parse_record(raw)
                    kind = record.get("kind")
                    if summary is not None:
                        raise RepairProtocolError("repair plan emitted rows after its summary")
                    if context is None:
                        if kind != "context":
                            raise RepairProtocolError("repair context must precede its rows")
                        context = record
                    elif kind == "native-input":
                        native_inputs += 1
                        if native_inputs > max_native_inputs:
                            raise RepairProtocolError("repair native input evidence exceeds row bound")
                        if not all(key in record for key in ("entity_id", "entity", "content")):
                            raise RepairProtocolError("repair native input lacks identity or exact content snapshot")
                    elif kind == "physicality":
                        count += 1
                        if count > max_rows:
                            raise RepairProtocolError("repair plan exceeds row bound")
                        if not all(key in record for key in ("original", "proposed", "evidence")):
                            raise RepairProtocolError("repair row lacks original, proposed, or evidence")
                    elif kind == "plan":
                        summary = record
                    else:
                        raise RepairProtocolError("unexpected repair record kind")
                    receipt.write(raw + b"\n")
                    plan_hash.update(raw + b"\n")
                if context is None or summary is None or summary.get("count") != count \
                        or summary.get("native_input_count", 0) != native_inputs:
                    raise RepairProtocolError("repair plan count does not match retained rows")
                if resources is not None and (resources["physicality_rows"] != count
                        or resources["native_input_rows"] != native_inputs
                        or resources["plan_bytes"] != total_bytes
                        or resources["max_line_bytes"] != largest_line):
                    raise RepairProtocolError("repair receipt does not match its retained resource summary")
                persistence_deadline = time.monotonic() + persistence_timeout
                receipt.flush()
                os.fsync(receipt.fileno())
            sync_directory(directory)
            if time.monotonic() >= persistence_deadline:
                raise RepairProtocolError("repair receipt persistence timed out before submission")
            manifest = {"schema": "laplace.legacy-content-repair-plan/v1",
                        "source_sha": source_sha, "started_unix_nanoseconds": started,
                        "plan_sha256": plan_hash.hexdigest(), "plan_bytes": total_bytes,
                        "planned_rows": count, "max_rows": max_rows,
                        "native_input_rows": native_inputs, "max_native_inputs": max_native_inputs,
                        "max_bytes": max_bytes, "max_line_bytes": max_line_bytes,
                        "timeout_seconds": timeout, "psql_fetch_rows": fetch_rows,
                        "persistence_timeout_seconds": persistence_timeout,
                        "idle_timeout_seconds": timeout + persistence_timeout,
                        "auxiliary_reserve_bytes": auxiliary_reserve_bytes,
                        "plan_sql_sha256": hashlib.sha256(plan_sql.encode()).hexdigest(),
                        "apply_sql_sha256": hashlib.sha256(apply_sql.encode()).hexdigest()}
            if resources is not None:
                manifest.update(resources_sha256=resources_hash, resources_disk_free_bytes=disk_free,
                    receipt_sql_sha256=hashlib.sha256(receipt_sql.encode()).hexdigest())
            write_new_json(directory / "manifest.json", manifest)
            if time.monotonic() >= persistence_deadline:
                raise RepairProtocolError("repair receipt persistence timed out before submission")
            if summary.get("unresolved") != 0:
                raise RepairProtocolError("repair has unresolved evidence; original rows retained")

            # This durable marker deliberately precedes sending SQL. If the client
            # dies during/after COMMIT, its absence/presence distinguishes never
            # submitted from unknown outcome. The original plan remains replayable
            # as evidence, not as permission to blindly rerun an unknown commit.
            write_new_json(directory / "submission.json", {
                "plan_sha256": plan_hash.hexdigest(), "planned_rows": count,
                "disposition": "submission-starting", "at_unix_nanoseconds": time.time_ns()})
            if time.monotonic() >= persistence_deadline:
                raise RepairProtocolError("repair receipt persistence timed out before submission")
            tx.begin_phase()
            submitted = True
            tx.send(apply_sql + f"\nCOMMIT;\n\\echo {commit_marker}\n")
            applied = None
            for raw in tx.lines(commit_marker):
                if applied is not None:
                    raise RepairProtocolError("committed repair emitted more than one applied record")
                applied = parse_record(raw)
                if applied.get("kind") != "applied" or applied.get("count") != count:
                    raise RepairProtocolError("committed repair output does not match retained plan")
            if applied is None:
                raise RepairProtocolError("committed repair output does not match retained plan")
            confirmed = True
            outcome = {**manifest, "disposition": "commit-confirmed", "applied": applied,
                       "finished_unix_nanoseconds": time.time_ns()}
            write_new_json(directory / "outcome.json", outcome)
            return outcome
    except BaseException as error:
        # A missing COMMIT echo is never reported as a proven rollback: connection
        # loss can happen after PostgreSQL committed but before the echo arrived.
        outcome = {"schema": "laplace.legacy-content-repair-outcome/v1",
                   "source_sha": source_sha, "plan_sha256": plan_hash.hexdigest(),
                   "disposition": "commit-confirmed-receipt-failed" if confirmed else
                       "submission-outcome-unknown" if submitted else "not-submitted",
                   "error_type": type(error).__name__,
                   "finished_unix_nanoseconds": time.time_ns()}
        if resources_hash is not None:
            outcome.update(resources_sha256=resources_hash,
                plan_sql_sha256=hashlib.sha256(plan_sql.encode()).hexdigest(),
                receipt_sql_sha256=hashlib.sha256(receipt_sql.encode()).hexdigest())
        try:
            write_new_json(directory / "failure.json", outcome)
        except OSError:
            pass  # Keep the original failure and any partial durable artifacts.
        raise
    finally:
        if tx is not None:
            tx.close()
