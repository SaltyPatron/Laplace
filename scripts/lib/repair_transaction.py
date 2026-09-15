"""Durable pre-mutation receipts for a bounded, caller-owned PostgreSQL repair.

The caller supplies fixed reviewed SQL, including evidence eligibility and locks.
This module owns only the psql transaction/receipt boundary. It cannot select rows,
calculate identities, or activate a repair on its own.
"""
from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import selectors
import subprocess
import time
import uuid


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

    def send(self, sql: str) -> None:
        assert self.process.stdin is not None
        remaining = memoryview(sql.encode())
        while remaining:
            sent = self.process.stdin.write(remaining)
            if not sent:
                raise RepairProtocolError("database connection stopped accepting repair SQL")
            remaining = remaining[sent:]
        self.process.stdin.flush()

    def lines(self, marker: str):
        """Read bounded newline-delimited JSONB output until a psql echo barrier."""
        assert self.process.stdout is not None
        deadline = time.monotonic() + self.timeout
        with selectors.DefaultSelector() as selector:
            selector.register(self.process.stdout, selectors.EVENT_READ)
            while True:
                while b"\n" in self.pending:
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


def preserve_and_apply(command: list[str], plan_sql: str, apply_sql: str,
                       directory: Path, *, source_sha: str, max_rows: int,
                       max_bytes: int = 512 * 1024 * 1024,
                       max_line_bytes: int = 2 * 1024 * 1024,
                       max_native_inputs: int = 100000,
                       timeout: int = 180) -> dict:
    """Keep the same database transaction open while its complete plan is fsynced.

    plan_sql yields JSONB records: context, zero or more physicality records,
    then plan(count, unresolved). apply_sql must validate/update that exact locked
    plan and yield one applied(count) record. This function supplies BEGIN/COMMIT.
    There is no arbitrary SQL command-line entry point.
    """
    if max_rows < 0 or max_native_inputs < 0 or max_bytes <= 0 or max_line_bytes <= 0:
        raise ValueError("repair bounds must be nonnegative rows and positive bytes")
    directory.mkdir(mode=0o770, parents=False, exist_ok=False)
    sync_directory(directory.parent)
    token = uuid.uuid4().hex
    plan_marker, commit_marker = f"PLAN_{token}", f"COMMITTED_{token}"
    plan_hash = hashlib.sha256()
    count = native_inputs = total_bytes = 0
    context = summary = None
    submitted = confirmed = False
    tx = None
    started = time.time_ns()
    try:
        with (directory / "database-errors.log").open("xb") as errors:
            tx = PsqlTransaction(command, errors, timeout=timeout,
                                 max_line_bytes=max_line_bytes)
            tx.send("\\set ON_ERROR_STOP on\nBEGIN ISOLATION LEVEL SERIALIZABLE;\n"
                    "SET LOCAL lock_timeout='10s';\n"
                    f"SET LOCAL statement_timeout='{timeout}s';\n"
                    "SET LOCAL idle_in_transaction_session_timeout='60s';\n"
                    + plan_sql + f"\n\\echo {plan_marker}\n")
            with (directory / "plan.jsonl").open("xb") as receipt:
                for raw in tx.lines(plan_marker):
                    total_bytes += len(raw) + 1
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
                receipt.flush()
                os.fsync(receipt.fileno())
            sync_directory(directory)
            manifest = {"schema": "laplace.legacy-content-repair-plan/v1",
                        "source_sha": source_sha, "started_unix_nanoseconds": started,
                        "plan_sha256": plan_hash.hexdigest(), "plan_bytes": total_bytes,
                        "planned_rows": count, "max_rows": max_rows,
                        "native_input_rows": native_inputs, "max_native_inputs": max_native_inputs,
                        "plan_sql_sha256": hashlib.sha256(plan_sql.encode()).hexdigest(),
                        "apply_sql_sha256": hashlib.sha256(apply_sql.encode()).hexdigest()}
            write_new_json(directory / "manifest.json", manifest)
            if summary.get("unresolved") != 0:
                raise RepairProtocolError("repair has unresolved evidence; original rows retained")

            # This durable marker deliberately precedes sending SQL. If the client
            # dies during/after COMMIT, its absence/presence distinguishes never
            # submitted from unknown outcome. The original plan remains replayable
            # as evidence, not as permission to blindly rerun an unknown commit.
            write_new_json(directory / "submission.json", {
                "plan_sha256": plan_hash.hexdigest(), "planned_rows": count,
                "disposition": "submission-starting", "at_unix_nanoseconds": time.time_ns()})
            submitted = True
            tx.send(apply_sql + f"\nCOMMIT;\n\\echo {commit_marker}\n")
            applied = [parse_record(raw) for raw in tx.lines(commit_marker)]
            if len(applied) != 1 or applied[0].get("kind") != "applied" \
                    or applied[0].get("count") != count:
                raise RepairProtocolError("committed repair output does not match retained plan")
            confirmed = True
            outcome = {**manifest, "disposition": "commit-confirmed", "applied": applied[0],
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
        try:
            write_new_json(directory / "failure.json", outcome)
        except OSError:
            pass  # Keep the original failure and any partial durable artifacts.
        raise
    finally:
        if tx is not None:
            tx.close()
