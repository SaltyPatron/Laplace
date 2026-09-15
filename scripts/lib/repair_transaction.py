"""Durable pre-mutation receipts for a bounded, caller-owned PostgreSQL repair.

The caller supplies fixed reviewed SQL, including evidence eligibility and locks.
This module owns only the psql transaction/receipt boundary. It cannot select rows,
calculate identities, or activate a repair on its own.
"""
from __future__ import annotations

from collections.abc import Callable
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
RECEIPT_METADATA_RESERVE_BYTES = 1024 * 1024
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
    def __init__(self, command: list[str], errors, *, timeout: int, max_line_bytes: int,
                 deadline_monotonic: float | None = None):
        self.deadline = time.monotonic() + timeout if deadline_monotonic is None else deadline_monotonic
        require_before_deadline(self.deadline, "maintenance deadline")
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=errors, bufsize=0)
        self.timeout = timeout
        self.max_line_bytes = max_line_bytes
        self.pending = bytearray()
        assert self.process.stdin is not None
        os.set_blocking(self.process.stdin.fileno(), False)

    def remaining(self) -> float:
        remaining = self.deadline - time.monotonic()
        if remaining <= 0:
            raise RepairProtocolError("repair transaction maintenance deadline timed out")
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
                    sent = os.write(self.process.stdin.fileno(), remaining[:65536])
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
            or resources["max_line_bytes"] < 2 \
            or resources["max_jsonl_line_bytes"] != resources["max_line_bytes"] + 1:
        raise RepairProtocolError("repair resource summary has inconsistent record structure")
    records = sum(resources[field] for field in (
        "physicality_rows", "native_input_rows", "context_rows", "summary_rows"))
    longest = resources["max_jsonl_line_bytes"]
    # Each stream record is a JSON object followed by LF: even {} takes three
    # bytes. One record attains the declared maximum and no record exceeds it.
    if not longest + 3 * (records - 1) <= resources["plan_bytes"] <= longest * records:
        raise RepairProtocolError("repair resource summary has inconsistent serialized byte counts")


def resource_rejections(resources: dict, *, max_rows: int, max_native_inputs: int,
                        max_bytes: int, max_line_bytes: int, disk_free_bytes: int,
                        auxiliary_reserve_bytes: int) -> list[str]:
    rejected = []
    for field, limit in (("physicality_rows", max_rows), ("native_input_rows", max_native_inputs),
                         ("plan_bytes", max_bytes), ("max_line_bytes", max_line_bytes)):
        if resources[field] > limit:
            rejected.append(f"{field}={resources[field]} exceeds declared limit {limit}")
    required = resources["plan_bytes"] + auxiliary_reserve_bytes + RECEIPT_METADATA_RESERVE_BYTES
    if required > disk_free_bytes:
        rejected.append(f"receipt and auxiliary reserve require {required} bytes; {disk_free_bytes} available")
    return rejected


def require_before_deadline(deadline: float, phase: str) -> None:
    if time.monotonic() >= deadline:
        raise RepairProtocolError(f"repair {phase} timed out")


def preserve_and_apply(command: list[str], plan_sql: str, apply_sql: str,
                       directory: Path, *, source_sha: str, max_rows: int,
                       max_bytes: int = 512 * 1024 * 1024,
                       max_line_bytes: int = 2 * 1024 * 1024,
                       max_native_inputs: int = 100000,
                       timeout: int = 180,
                       persistence_timeout: int = 60,
                       receipt_sql: str | None = None,
                       measurement_only: bool = False,
                       auxiliary_reserve_bytes: int = 0,
                       deadline_monotonic: float | None = None,
                       resource_validator: Callable[[dict], list[str]] | None = None) -> dict:
    """Durably retain an admitted locked plan before submitting its mutation.

    With receipt_sql, plan_sql emits one complete resource summary. Only a
    durable, accepted summary and exact journal disk reservation permit the
    separately submitted receipt stream. measurement_only confirms ROLLBACK at
    this barrier without reserving or streaming the body. The caller's shared
    maintenance deadline includes work before entry; send, capture, fsync, apply
    and durable outcome never start a fresh maintenance budget. Final pre-apply
    persistence is also bounded by persistence_timeout within that deadline.
    Legacy fixed bounded callers may provide their complete stream as plan_sql.
    The caller owns row selection, SQL recipes, locks and semantic eligibility.
    """
    if any(type(value) is not int or value < 0 for value in (max_rows, max_native_inputs)) \
            or any(type(value) is not int or value <= 0 for value in (max_bytes, max_line_bytes)):
        raise ValueError("repair bounds must be nonnegative rows and positive bytes")
    if any(type(value) not in (int, float) or not math.isfinite(value) or value <= 0
           for value in (timeout, persistence_timeout)):
        raise ValueError("repair maintenance and persistence timeouts must be finite and positive")
    if type(auxiliary_reserve_bytes) is not int or auxiliary_reserve_bytes < 0:
        raise ValueError("repair auxiliary reserve must be nonnegative integer bytes")
    if measurement_only and receipt_sql is None:
        raise ValueError("repair measurement requires the separate resource barrier")
    if receipt_sql is not None and not receipt_sql.strip():
        raise ValueError("repair receipt SQL must be nonempty")
    if resource_validator is not None and (not callable(resource_validator) or receipt_sql is None):
        raise ValueError("repair dependent resource validation requires the resource barrier")
    entered_monotonic = time.monotonic()
    deadline = entered_monotonic + timeout if deadline_monotonic is None else deadline_monotonic
    if type(deadline) not in (int, float) or not math.isfinite(deadline):
        raise ValueError("repair deadline must be finite")
    require_before_deadline(deadline, "maintenance deadline")
    fetch_rows = max(1, min(PSQL_FETCH_MAX_ROWS, PSQL_FETCH_PAYLOAD_BYTES // max_line_bytes))
    directory.mkdir(mode=0o770, parents=False, exist_ok=False)
    sync_directory(directory.parent)
    token = uuid.uuid4().hex
    plan_marker, commit_marker = f"PLAN_{token}", f"COMMITTED_{token}"
    plan_hash = hashlib.sha256()
    count = native_inputs = total_bytes = retained_bytes = largest_line = 0
    context = summary = None
    resources = resources_hash = disk_free = admission = None
    submitted = confirmed = False
    tx = None
    started = time.time_ns()
    phase_timings = {}
    phase = "connection_setup"
    phase_started = entered_monotonic
    try:
        with (directory / "database-errors.log").open("xb") as errors:
            connected_monotonic = time.monotonic()
            remaining_at_connection = deadline - connected_monotonic
            require_before_deadline(deadline, "maintenance deadline")
            database_timeout_ms = max(1, math.ceil(remaining_at_connection * 1000))
            phase = "planning_and_capture"
            phase_started = connected_monotonic
            tx = PsqlTransaction(command, errors, timeout=timeout,
                max_line_bytes=RESOURCE_RECORD_MAX_BYTES if receipt_sql is not None else max_line_bytes,
                deadline_monotonic=deadline)
            tx.send("\\set ON_ERROR_STOP on\n\\set SHOW_ALL_RESULTS on\n"
                    f"\\set FETCH_COUNT {fetch_rows}\n"
                    "BEGIN ISOLATION LEVEL SERIALIZABLE;\n"
                    "SET LOCAL client_encoding='UTF8';\n"
                    "SET LOCAL lock_timeout='10s';\n"
                    f"SET LOCAL statement_timeout='{database_timeout_ms}ms';\n"
                    f"SET LOCAL idle_in_transaction_session_timeout='{database_timeout_ms}ms';\n"
                    + plan_sql + f"\n\\echo {plan_marker}\n")
            if receipt_sql is not None:
                for raw in tx.lines(plan_marker):
                    if resources is not None:
                        raise RepairProtocolError("repair planning emitted more than one resource summary")
                    resources = parse_record(raw)
                if resources is None:
                    raise RepairProtocolError("repair planning omitted its resource summary")
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
                if resource_validator is not None:
                    dependent_rejections = resource_validator(resources)
                    if not isinstance(dependent_rejections, list) or any(
                            not isinstance(reason, str) for reason in dependent_rejections):
                        raise ValueError("repair dependent resource validation must return rejection messages")
                    rejected.extend(dependent_rejections)
                    tx.remaining()
                if measurement_only:
                    rollback_marker = f"ROLLED_BACK_{token}"
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
                        "metadata_reserve_bytes": RECEIPT_METADATA_RESERVE_BYTES,
                        "disk_free_bytes": disk_free, "timeout_seconds": timeout,
                        "remaining_seconds_at_connection_start": remaining_at_connection,
                        "initial_database_timeout_milliseconds": database_timeout_ms,
                        "plan_sql_sha256": hashlib.sha256(plan_sql.encode()).hexdigest(),
                        "receipt_sql_sha256": hashlib.sha256(receipt_sql.encode()).hexdigest(),
                        "finished_unix_nanoseconds": time.time_ns()}
                    write_new_json(directory / "measurement.json", measurement)
                    require_before_deadline(deadline, "maintenance deadline")
                    return measurement
                if rejected:
                    measured = {field: resources[field] for field in RESOURCE_FIELDS}
                    raise RepairProtocolError("repair resource envelope rejected: " + "; ".join(rejected)
                        + "; measured=" + json.dumps(measured, sort_keys=True)
                        + "; resources_receipt=" + str(directory / "resources.json"))
            with (directory / "plan.jsonl").open("xb") as receipt:
                try:
                    if resources is not None:
                        # Reserve the actual journal on its own filesystem before
                        # authorizing PostgreSQL to emit any of its body. A free
                        # space observation alone cannot reserve shared-host blocks.
                        filesystem = os.fstatvfs(receipt.fileno())
                        available_bytes = filesystem.f_bavail * filesystem.f_frsize
                        rejected = resource_rejections(resources, max_rows=max_rows,
                            max_native_inputs=max_native_inputs, max_bytes=max_bytes,
                            max_line_bytes=max_line_bytes, disk_free_bytes=available_bytes,
                            auxiliary_reserve_bytes=auxiliary_reserve_bytes)
                        if rejected:
                            raise RepairProtocolError("repair resource envelope rejected before reservation: "
                                                      + "; ".join(rejected))
                        os.posix_fallocate(receipt.fileno(), 0, resources["plan_bytes"])
                        require_before_deadline(deadline, "resource admission")
                        admission = {"schema": "laplace.legacy-content-repair-resource-admission/v1",
                            "resources_sha256": resources_hash,
                            "expected_plan_bytes": resources["plan_bytes"],
                            "available_filesystem_bytes_before_reservation": available_bytes,
                            "metadata_reserve_bytes": RECEIPT_METADATA_RESERVE_BYTES,
                            "auxiliary_reserve_bytes": auxiliary_reserve_bytes,
                            "journal_preallocation": "posix_fallocate-succeeded",
                            "max_rows": max_rows, "max_native_inputs": max_native_inputs,
                            "max_bytes": max_bytes, "max_line_bytes": max_line_bytes}
                        write_new_json(directory / "resource-admission.json", admission)
                        require_before_deadline(deadline, "resource admission")
                        tx.max_line_bytes = max_line_bytes
                        plan_marker = f"RECEIPT_{token}"
                        tx.send(receipt_sql + f"\n\\echo {plan_marker}\n")
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
                        retained_bytes += len(raw) + 1
                    if context is None or summary is None or summary.get("count") != count \
                            or summary.get("native_input_count", 0) != native_inputs:
                        raise RepairProtocolError("repair plan count does not match retained rows")
                    if resources is not None and (resources["physicality_rows"] != count
                            or resources["native_input_rows"] != native_inputs
                            or resources["plan_bytes"] != total_bytes
                            or resources["max_line_bytes"] != largest_line):
                        raise RepairProtocolError("repair receipt does not match its retained resource summary")
                finally:
                    # Failed capture preserves only the actual prefix. Reserved
                    # unwritten blocks must not appear as zero-filled evidence.
                    receipt.truncate(receipt.tell())
                captured_monotonic = time.monotonic()
                phase_timings["planning_and_capture_seconds"] = captured_monotonic - connected_monotonic
                phase = "durability"
                phase_started = captured_monotonic
                persistence_deadline = min(deadline, captured_monotonic + persistence_timeout)
                require_before_deadline(persistence_deadline, "receipt persistence")
                idle_timeout_ms = max(1, math.ceil((persistence_deadline - time.monotonic()) * 1000))
                durable_marker = f"DURABILITY_{token}"
                tx.deadline = persistence_deadline
                tx.send(f"SET LOCAL idle_in_transaction_session_timeout='{idle_timeout_ms}ms';\n"
                        f"\\echo {durable_marker}\n")
                for raw in tx.lines(durable_marker):
                    raise RepairProtocolError("database emitted unexpected durability barrier output")
                receipt.flush()
                os.fsync(receipt.fileno())
                require_before_deadline(persistence_deadline, "receipt persistence")
            sync_directory(directory)
            require_before_deadline(persistence_deadline, "receipt persistence")
            manifest = {"schema": "laplace.legacy-content-repair-plan/v1",
                        "source_sha": source_sha, "started_unix_nanoseconds": started,
                        "plan_sha256": plan_hash.hexdigest(), "plan_bytes": total_bytes,
                        "planned_rows": count, "max_rows": max_rows,
                        "native_input_rows": native_inputs, "max_native_inputs": max_native_inputs,
                        "max_bytes": max_bytes, "max_line_bytes": max_line_bytes,
                        "timeout_seconds": timeout, "psql_fetch_rows": fetch_rows,
                        "persistence_timeout_seconds": persistence_timeout,
                        "remaining_seconds_at_connection_start": remaining_at_connection,
                        "initial_database_timeout_milliseconds": database_timeout_ms,
                        "durability_idle_timeout_milliseconds": idle_timeout_ms,
                        "phase_timings": dict(phase_timings), "resource_admission": admission,
                        "auxiliary_reserve_bytes": auxiliary_reserve_bytes,
                        "plan_sql_sha256": hashlib.sha256(plan_sql.encode()).hexdigest(),
                        "apply_sql_sha256": hashlib.sha256(apply_sql.encode()).hexdigest()}
            if resources is not None:
                manifest.update(resources_sha256=resources_hash, resources_disk_free_bytes=disk_free,
                    receipt_sql_sha256=hashlib.sha256(receipt_sql.encode()).hexdigest())
            write_new_json(directory / "manifest.json", manifest)
            require_before_deadline(persistence_deadline, "receipt persistence")
            if summary.get("unresolved") != 0:
                raise RepairProtocolError("repair has unresolved evidence; original rows retained")
            write_new_json(directory / "submission.json", {
                "plan_sha256": plan_hash.hexdigest(), "planned_rows": count,
                "disposition": "submission-starting", "at_unix_nanoseconds": time.time_ns()})
            require_before_deadline(persistence_deadline, "receipt persistence")
            submitted_monotonic = time.monotonic()
            phase_timings["durability_seconds"] = submitted_monotonic - captured_monotonic
            phase = "apply_and_commit_confirmation"
            phase_started = submitted_monotonic
            # Restore only the existing overall deadline, never a fresh budget.
            tx.deadline = deadline
            submitted = True
            # Planning and durability consume the same maintenance allowance.
            apply_timeout_ms = max(1, math.floor((deadline - time.monotonic()) * 1000))
            tx.send(f"SET LOCAL statement_timeout='{apply_timeout_ms}ms';\n"
                    + apply_sql + f"\nCOMMIT;\n\\echo {commit_marker}\n")
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
            confirmed_monotonic = time.monotonic()
            phase_timings["apply_and_commit_confirmation_seconds"] = confirmed_monotonic - submitted_monotonic
            phase_timings["protocol_seconds_through_commit_confirmation"] = confirmed_monotonic - entered_monotonic
            phase = "outcome_durability"
            phase_started = confirmed_monotonic
            outcome = {**manifest, "disposition": "commit-confirmed", "applied": applied,
                       "apply_statement_timeout_milliseconds": apply_timeout_ms,
                       "phase_timings": phase_timings, "finished_unix_nanoseconds": time.time_ns()}
            write_new_json(directory / "outcome.json", outcome)
            outcome_durable_monotonic = time.monotonic()
            write_new_json(directory / "completion-timing.json", {
                "plan_sha256": plan_hash.hexdigest(),
                "measured_through": "outcome.json file and directory fsync",
                "outcome_durability_seconds": outcome_durable_monotonic - confirmed_monotonic,
                "protocol_seconds_through_durable_outcome": outcome_durable_monotonic - entered_monotonic,
                "remaining_maintenance_seconds": deadline - outcome_durable_monotonic,
                "maintenance_deadline_exceeded": outcome_durable_monotonic >= deadline})
            require_before_deadline(deadline, "maintenance deadline")
            return outcome
    except BaseException as error:
        # Missing COMMIT confirmation is never reported as proven rollback.
        # PostgreSQL may have committed before the client lost its acknowledgement.
        outcome = {"schema": "laplace.legacy-content-repair-outcome/v1",
                   "source_sha": source_sha, "plan_sha256": plan_hash.hexdigest(),
                   "disposition": ("commit-confirmed-deadline-exceeded" if time.monotonic() >= deadline
                                   else "commit-confirmed-receipt-failed") if confirmed else
                       "submission-outcome-unknown" if submitted else "not-submitted",
                   "error_type": type(error).__name__, "phase_timings": phase_timings,
                   "phase_at_failure": phase, "phase_elapsed_seconds": time.monotonic() - phase_started,
                   "protocol_elapsed_seconds": time.monotonic() - entered_monotonic,
                   "received_bytes": total_bytes, "retained_bytes": retained_bytes,
                   "finished_unix_nanoseconds": time.time_ns()}
        if resources_hash is not None:
            outcome.update(resources_sha256=resources_hash,
                plan_sql_sha256=hashlib.sha256(plan_sql.encode()).hexdigest(),
                receipt_sql_sha256=hashlib.sha256(receipt_sql.encode()).hexdigest())
        try:
            write_new_json(directory / "failure.json", outcome)
        except OSError:
            pass
        raise
    finally:
        if tx is not None:
            tx.close()
