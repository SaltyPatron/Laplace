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
import subprocess
import time
import uuid


RECEIPT_METADATA_RESERVE_BYTES = 1024 * 1024


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
        self.process = subprocess.Popen(command, stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=errors, bufsize=0)
        self.timeout = timeout
        self.deadline = time.monotonic() + timeout if deadline_monotonic is None else deadline_monotonic
        self.max_line_bytes = max_line_bytes
        self.pending = bytearray()
        os.set_blocking(self.process.stdin.fileno(), False)

    def send(self, sql: str) -> None:
        assert self.process.stdin is not None
        pending = memoryview(sql.encode())
        with selectors.DefaultSelector() as selector:
            selector.register(self.process.stdin, selectors.EVENT_WRITE)
            while pending:
                remaining = self.deadline - time.monotonic()
                if remaining <= 0 or not selector.select(remaining):
                    raise RepairProtocolError("repair transaction submission timed out")
                try:
                    sent = os.write(self.process.stdin.fileno(), pending[:65536])
                except BlockingIOError:
                    continue
                if not sent:
                    raise RepairProtocolError("database connection stopped accepting repair SQL")
                pending = pending[sent:]

    def lines(self, marker: str):
        """Read bounded newline-delimited JSONB output until a psql echo barrier."""
        assert self.process.stdout is not None
        deadline = self.deadline
        with selectors.DefaultSelector() as selector:
            selector.register(self.process.stdout, selectors.EVENT_READ)
            while True:
                while b"\n" in self.pending:
                    if time.monotonic() >= deadline:
                        raise RepairProtocolError("repair transaction response timed out")
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
                remaining = deadline - time.monotonic()
                if remaining <= 0 or not selector.select(remaining):
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


def resource_admission(context: dict, context_bytes: int, receipt, directory: Path, *,
                       max_rows: int, max_native_inputs: int | None,
                       max_bytes: int | None, max_line_bytes: int,
                       required: bool) -> dict | None:
    """Reserve the exact locked journal, including its context and final summary.

    SQL measures its actual serialized body once. This is an admission contract,
    not an estimate from sampled owners or the native-input subset of the plan.
    Explicit caller limits remain additional limits on that complete contract.
    """
    inventory = context.get("resource_inventory")
    if inventory is None:
        if required or max_native_inputs is None or max_bytes is None:
            raise RepairProtocolError("repair requires a complete measured resource inventory")
        return None
    keys = ("native_input_rows", "physicality_rows", "body_records", "body_bytes",
            "max_body_line_bytes")
    if not isinstance(inventory, dict) or any(
            type(inventory.get(key)) is not int or inventory[key] < 0 for key in keys):
        raise RepairProtocolError("repair resource inventory contains invalid counts or sizes")
    if inventory["body_records"] != inventory["native_input_rows"] + inventory["physicality_rows"] + 1 \
            or inventory["body_bytes"] < inventory["body_records"] \
            or not 1 <= inventory["max_body_line_bytes"] <= inventory["body_bytes"]:
        raise RepairProtocolError("repair resource inventory is incomplete or inconsistent")
    expected_bytes = context_bytes + inventory["body_bytes"]
    if inventory["physicality_rows"] > max_rows:
        raise RepairProtocolError("repair plan exceeds row bound")
    if max_native_inputs is not None and inventory["native_input_rows"] > max_native_inputs:
        raise RepairProtocolError("repair native input evidence exceeds row bound")
    if max_bytes is not None and expected_bytes > max_bytes:
        raise RepairProtocolError("repair receipt exceeds byte bound")
    # Both inventory sizes include one LF; the streaming line limit excludes it.
    if inventory["max_body_line_bytes"] > max_line_bytes + 1:
        raise RepairProtocolError("repair row exceeds byte bound")
    filesystem = os.fstatvfs(receipt.fileno())
    available_bytes = filesystem.f_bavail * filesystem.f_frsize
    if available_bytes < expected_bytes + RECEIPT_METADATA_RESERVE_BYTES:
        raise RepairProtocolError("insufficient receipt storage for the complete repair plan")
    # A free-space observation alone does not reserve blocks on a shared host.
    # Linux fallocate either reserves the entire journal or fails before apply.
    os.posix_fallocate(receipt.fileno(), 0, expected_bytes)
    admission = {"schema": "laplace.legacy-content-repair-resource-admission/v1",
                 "inventory": inventory, "context_bytes": context_bytes,
                 "expected_plan_bytes": expected_bytes,
                 "available_filesystem_bytes_before_reservation": available_bytes,
                 "metadata_reserve_bytes": RECEIPT_METADATA_RESERVE_BYTES,
                 "journal_preallocation": "posix_fallocate-succeeded",
                 "max_rows": max_rows,
                 "max_native_inputs": inventory["native_input_rows"] if max_native_inputs is None else max_native_inputs,
                 "max_bytes": expected_bytes if max_bytes is None else max_bytes,
                 "max_line_bytes": max_line_bytes}
    write_new_json(directory / "resource-admission.json", admission)
    return admission


def require_before_deadline(deadline: float, phase: str) -> None:
    if time.monotonic() >= deadline:
        raise RepairProtocolError(f"repair {phase} timed out")


def preserve_and_apply(command: list[str], plan_sql: str, apply_sql: str,
                       directory: Path, *, source_sha: str, max_rows: int,
                       max_bytes: int | None = 512 * 1024 * 1024,
                       max_line_bytes: int = 2 * 1024 * 1024,
                       max_native_inputs: int | None = 100000,
                       timeout: int = 180, durability_timeout: int | None = None,
                       deadline_monotonic: float | None = None,
                       require_resource_inventory: bool = False) -> dict:
    """Keep the same database transaction open while its complete plan is fsynced.

    plan_sql yields JSONB records: context, zero or more physicality records,
    then plan(count, unresolved). apply_sql must validate/update that exact locked
    plan and yield one applied(count) record. This function supplies BEGIN/COMMIT.
    There is no arbitrary SQL command-line entry point.
    """
    if max_rows < 0 or (max_native_inputs is not None and max_native_inputs < 0) \
            or (max_bytes is not None and max_bytes <= 0) or max_line_bytes <= 0:
        raise ValueError("repair bounds must be nonnegative rows and positive bytes")
    if durability_timeout is None:
        durability_timeout = timeout
    if type(timeout) is not int or timeout <= 0 \
            or type(durability_timeout) is not int or durability_timeout <= 0:
        raise ValueError("repair timeouts must be positive whole seconds")
    entered_monotonic = time.monotonic()
    deadline = entered_monotonic + timeout if deadline_monotonic is None else deadline_monotonic
    if not math.isfinite(deadline):
        raise ValueError("repair deadline must be finite")
    require_before_deadline(deadline, "maintenance deadline")
    directory.mkdir(mode=0o770, parents=False, exist_ok=False)
    sync_directory(directory.parent)
    token = uuid.uuid4().hex
    plan_marker, commit_marker = f"PLAN_{token}", f"COMMITTED_{token}"
    plan_hash = hashlib.sha256()
    count = native_inputs = total_bytes = retained_bytes = body_records = max_body_line_bytes = 0
    context = summary = None
    admission = None
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
                                 max_line_bytes=max_line_bytes, deadline_monotonic=deadline)
            tx.send("\\set ON_ERROR_STOP on\nBEGIN ISOLATION LEVEL SERIALIZABLE;\n"
                    "SET LOCAL lock_timeout='10s';\n"
                    f"SET LOCAL statement_timeout='{database_timeout_ms}ms';\n"
                    f"SET LOCAL idle_in_transaction_session_timeout='{database_timeout_ms}ms';\n"
                    + plan_sql + f"\n\\echo {plan_marker}\n")
            with (directory / "plan.jsonl").open("xb") as receipt:
                try:
                    for raw in tx.lines(plan_marker):
                        total_bytes += len(raw) + 1
                        if max_bytes is not None and total_bytes > max_bytes:
                            raise RepairProtocolError("repair receipt exceeds byte bound")
                        record = parse_record(raw)
                        kind = record.get("kind")
                        if summary is not None:
                            raise RepairProtocolError("repair plan emitted rows after its summary")
                        if context is None:
                            if kind != "context":
                                raise RepairProtocolError("repair context must precede its rows")
                            context = record
                            admission = resource_admission(context, len(raw) + 1, receipt, directory,
                                max_rows=max_rows, max_native_inputs=max_native_inputs,
                                max_bytes=max_bytes, max_line_bytes=max_line_bytes,
                                required=require_resource_inventory)
                            require_before_deadline(deadline, "resource admission")
                            if admission is not None:
                                max_bytes = admission["max_bytes"]
                                max_native_inputs = admission["max_native_inputs"]
                        else:
                            body_records += 1
                            max_body_line_bytes = max(max_body_line_bytes, len(raw) + 1)
                            if kind == "native-input":
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
                    if admission is not None:
                        actual_inventory = {"native_input_rows": native_inputs, "physicality_rows": count,
                            "body_records": body_records,
                            "body_bytes": total_bytes - admission["context_bytes"],
                            "max_body_line_bytes": max_body_line_bytes}
                        if actual_inventory != admission["inventory"]:
                            raise RepairProtocolError("repair stream differs from its complete measured inventory")
                finally:
                    # Remove unused reservation on failure. Historical evidence
                    # remains the actual received prefix, without a zero-filled tail.
                    receipt.truncate(receipt.tell())
                captured_monotonic = time.monotonic()
                phase_timings["planning_and_capture_seconds"] = captured_monotonic - connected_monotonic
                phase = "durability"
                phase_started = captured_monotonic
                durability_deadline = min(deadline, captured_monotonic + durability_timeout)
                require_before_deadline(durability_deadline, "receipt durability")
                # The server's idle protection uses the remaining shared budget
                # at the point it waits for local fsync, not a fresh allowance.
                idle_timeout_ms = max(1, math.ceil((durability_deadline - time.monotonic()) * 1000))
                durable_marker = f"DURABILITY_{token}"
                tx.send(f"SET LOCAL idle_in_transaction_session_timeout='{idle_timeout_ms}ms';\n"
                        f"\\echo {durable_marker}\n")
                for _ in tx.lines(durable_marker):
                    raise RepairProtocolError("database emitted unexpected durability barrier output")
                receipt.flush()
                os.fsync(receipt.fileno())
                require_before_deadline(durability_deadline, "receipt durability")
            sync_directory(directory)
            require_before_deadline(durability_deadline, "receipt durability")
            manifest = {"schema": "laplace.legacy-content-repair-plan/v1",
                        "source_sha": source_sha, "started_unix_nanoseconds": started,
                        "plan_sha256": plan_hash.hexdigest(), "plan_bytes": total_bytes,
                        "planned_rows": count, "max_rows": max_rows,
                        "native_input_rows": native_inputs, "max_native_inputs": max_native_inputs,
                        "max_bytes": max_bytes, "max_line_bytes": max_line_bytes,
                        "timeout_seconds": timeout, "durability_timeout_seconds": durability_timeout,
                        "remaining_seconds_at_connection_start": remaining_at_connection,
                        "initial_database_timeout_milliseconds": database_timeout_ms,
                        "durability_idle_timeout_milliseconds": idle_timeout_ms,
                        "phase_timings": dict(phase_timings),
                        "resource_admission": admission,
                        "plan_sql_sha256": hashlib.sha256(plan_sql.encode()).hexdigest(),
                        "apply_sql_sha256": hashlib.sha256(apply_sql.encode()).hexdigest()}
            write_new_json(directory / "manifest.json", manifest)
            require_before_deadline(durability_deadline, "receipt durability")
            if summary.get("unresolved") != 0:
                raise RepairProtocolError("repair has unresolved evidence; original rows retained")

            # This durable marker deliberately precedes sending SQL. If the client
            # dies during/after COMMIT, its absence/presence distinguishes never
            # submitted from unknown outcome. The original plan remains replayable
            # as evidence, not as permission to blindly rerun an unknown commit.
            write_new_json(directory / "submission.json", {
                "plan_sha256": plan_hash.hexdigest(), "planned_rows": count,
                "disposition": "submission-starting", "at_unix_nanoseconds": time.time_ns()})
            require_before_deadline(durability_deadline, "receipt durability")
            submitted_monotonic = time.monotonic()
            phase_timings["durability_seconds"] = submitted_monotonic - captured_monotonic
            phase = "apply_and_commit_confirmation"
            phase_started = submitted_monotonic
            submitted = True
            tx.send(apply_sql + f"\nCOMMIT;\n\\echo {commit_marker}\n")
            applied = []
            for raw in tx.lines(commit_marker):
                if applied:
                    raise RepairProtocolError("committed repair emitted excess confirmation records")
                applied.append(parse_record(raw))
            if len(applied) != 1 or applied[0].get("kind") != "applied" \
                    or applied[0].get("count") != count:
                raise RepairProtocolError("committed repair output does not match retained plan")
            confirmed = True
            confirmed_monotonic = time.monotonic()
            phase_timings["apply_and_commit_confirmation_seconds"] = confirmed_monotonic - submitted_monotonic
            phase_timings["protocol_seconds_through_commit_confirmation"] = confirmed_monotonic - entered_monotonic
            phase = "outcome_durability"
            phase_started = confirmed_monotonic
            outcome = {**manifest, "disposition": "commit-confirmed", "applied": applied[0],
                       "phase_timings": phase_timings,
                       "finished_unix_nanoseconds": time.time_ns()}
            write_new_json(directory / "outcome.json", outcome)
            outcome_durable_monotonic = time.monotonic()
            completion_timing = {
                "plan_sha256": plan_hash.hexdigest(),
                "measured_through": "outcome.json file and directory fsync",
                "outcome_durability_seconds": outcome_durable_monotonic - confirmed_monotonic,
                "protocol_seconds_through_durable_outcome": outcome_durable_monotonic - entered_monotonic,
                "remaining_maintenance_seconds": deadline - outcome_durable_monotonic,
                "maintenance_deadline_exceeded": outcome_durable_monotonic >= deadline}
            write_new_json(directory / "completion-timing.json", completion_timing)
            require_before_deadline(deadline, "maintenance deadline")
            return outcome
    except BaseException as error:
        # A missing COMMIT echo is never reported as a proven rollback: connection
        # loss can happen after PostgreSQL committed but before the echo arrived.
        outcome = {"schema": "laplace.legacy-content-repair-outcome/v1",
                   "source_sha": source_sha, "plan_sha256": plan_hash.hexdigest(),
                   "disposition": ("commit-confirmed-deadline-exceeded" if time.monotonic() >= deadline
                                    else "commit-confirmed-receipt-failed") if confirmed else
                       "submission-outcome-unknown" if submitted else "not-submitted",
                   "error_type": type(error).__name__,
                   "phase_timings": phase_timings,
                   "phase_at_failure": phase,
                   "phase_elapsed_seconds": time.monotonic() - phase_started,
                   "protocol_elapsed_seconds": time.monotonic() - entered_monotonic,
                   "received_bytes": total_bytes,
                   "retained_bytes": retained_bytes,
                   "finished_unix_nanoseconds": time.time_ns()}
        try:
            write_new_json(directory / "failure.json", outcome)
        except OSError:
            pass  # Keep the original failure and any partial durable artifacts.
        raise
    finally:
        if tx is not None:
            tx.close()
