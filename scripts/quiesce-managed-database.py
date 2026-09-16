#!/usr/bin/env python3
"""Run database maintenance using the already installed, fixed service controls.

This is unprivileged orchestration. It never installs a root helper or edits a
service marker. Each temporary stop is receipted before submission; the installed
managed transaction excludes competing service controls while maintenance runs.
"""
from __future__ import annotations

import argparse
import fcntl
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import stat
import subprocess
import time
import uuid

from lib.repair_transaction import write_new_json, sync_directory

DEPLOY = "/usr/local/libexec/laplace-managed-deploy"
CONTROL = "/usr/local/libexec/laplace-service-control"
STATE = Path("/var/lib/laplace-managed")
PROC = Path("/proc")
RECEIPTS = Path("/build/laplace/recovery/legacy-content-service-quiescence")
REPAIR_RECEIPTS = Path("/build/laplace/recovery/legacy-content-repair")
CHESS_OBSERVATION_RECEIPTS = Path("/build/laplace/recovery/chess-position-outcomes")
CHESS_APPLICATION_ENV = "LAPLACE_CHESS_OBSERVATION_APPLICATION_NAME"
MAX_METADATA_BYTES = 64 * 1024
MAX_BYTES = 512 * 1024 * 1024
MAX_LINE_BYTES = 2 * 1024 * 1024
TIMEOUT_SECONDS = 180
RESOURCE_ENV = "LAPLACE_REPAIR_RESOURCE_RECEIPT"


def positive_integer(value: object) -> int:
    if type(value) is not int or value <= 0:
        raise ValueError("maintenance resource bounds must be positive integers")
    return value


def positive_argument(value: str) -> int:
    try:
        return positive_integer(int(value))
    except ValueError as error:
        raise argparse.ArgumentTypeError(str(error)) from error


def bounded_metadata(path: Path) -> dict:
    if path.is_symlink():
        raise ValueError("maintenance resource metadata cannot be a symlink")
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(descriptor, "rb") as source:
        if not stat.S_ISREG(os.fstat(source.fileno()).st_mode):
            raise ValueError("maintenance resource metadata must be a regular file")
        raw = source.read(MAX_METADATA_BYTES + 1)
    if len(raw) > MAX_METADATA_BYTES:
        raise ValueError("maintenance resource metadata exceeds byte bound")
    value = json.loads(raw)
    if not isinstance(value, dict):
        raise ValueError("maintenance resource metadata must be an object")
    return value


def boot_identity() -> str:
    return (PROC / "sys/kernel/random/boot_id").read_text().strip()


class MaintenanceResources:
    """One durable read budget shared by the wrapper and its repair child.

    Every full-journal authentication reserves its entire declared byte count
    before reading. A crash can overcharge that reservation, never erase it.
    The current attempt has a separate explicit readback allowance; all other
    journals, including earlier attempts in this service transaction, are prior
    evidence. Resuming creates a new invocation with new explicit bounds.
    """

    def __init__(self, path: Path):
        if not re.fullmatch(r"maintenance-resources-[0-9a-f]{32}\.json", path.name):
            raise ValueError("maintenance resource receipt has an invalid identity")
        self.path = path
        self.config = bounded_metadata(path)
        if self.config.get("schema") != "laplace.legacy-content-maintenance-resources/v1":
            raise ValueError("unknown maintenance resource schema")
        for key in ("max_bytes", "max_line_bytes", "max_prior_bytes", "max_current_readback_bytes", "timeout_seconds"):
            positive_integer(self.config.get(key))
        self.deadline = self.config.get("deadline_monotonic")
        if type(self.deadline) not in (int, float) or not math.isfinite(self.deadline) \
                or self.config.get("boot_id") != boot_identity():
            raise ValueError("maintenance deadline has no current boot identity")
        self.repair_root = Path(self.config["repair_root"])
        if not self.repair_root.is_absolute() or self.repair_root.is_symlink() \
                or str(self.repair_root.resolve()) != str(self.repair_root):
            raise ValueError("maintenance resource estate has an invalid identity")
        self.usage_path = path.with_name(path.stem + "-usage.json")
        self.lock_path = path.with_name(path.stem + ".lock")
        self.max_bytes = self.config["max_prior_bytes"]

    @classmethod
    def create(cls, directory: Path, *, repair_root: Path, max_bytes: int,
               max_line_bytes: int, max_prior_bytes: int,
               max_current_readback_bytes: int, timeout_seconds: int):
        bounds = {"max_bytes": max_bytes, "max_line_bytes": max_line_bytes,
                  "max_prior_bytes": max_prior_bytes,
                  "max_current_readback_bytes": max_current_readback_bytes,
                  "timeout_seconds": timeout_seconds}
        for value in bounds.values():
            positive_integer(value)
        path = directory / ("maintenance-resources-" + uuid.uuid4().hex + ".json")
        write_new_json(path, {"schema": "laplace.legacy-content-maintenance-resources/v1",
            **bounds, "deadline_monotonic": time.monotonic() + timeout_seconds,
            "boot_id": boot_identity(), "repair_root": str(repair_root.resolve())})
        write_new_json(path.with_name(path.stem + "-usage.json"), {
            "schema": "laplace.legacy-content-maintenance-usage/v1",
            "resource_receipt": path.name, "current_receipt": None,
            "prior_bytes_read": 0, "current_bytes_read": 0, "reservations": 0})
        return cls(path)

    def check_deadline(self) -> None:
        if time.monotonic() >= self.deadline:
            raise TimeoutError("legacy repair maintenance deadline exceeded")

    def usage(self) -> dict:
        value = bounded_metadata(self.usage_path)
        if value.get("schema") != "laplace.legacy-content-maintenance-usage/v1" \
                or value.get("resource_receipt") != self.path.name:
            raise ValueError("maintenance usage does not identify its resource receipt")
        for key in ("prior_bytes_read", "current_bytes_read", "reservations"):
            if type(value.get(key)) is not int or value[key] < 0:
                raise ValueError("maintenance usage counter is invalid")
        if value["prior_bytes_read"] > self.config["max_prior_bytes"] \
                or value["current_bytes_read"] > self.config["max_current_readback_bytes"]:
            raise ValueError("maintenance usage exceeds its admitted byte bounds")
        current = value.get("current_receipt")
        if current is not None and (not isinstance(current, str) or self.receipt_path(Path(current)) != current):
            raise ValueError("maintenance current receipt identity is invalid")
        return value

    def receipt_path(self, directory: Path) -> str:
        if directory.is_symlink() or directory.resolve().parent != self.repair_root:
            raise ValueError("maintenance receipt does not belong to the admitted estate")
        return str(directory.resolve())

    def change_usage(self, update) -> None:
        self.check_deadline()
        # The orchestration lock serializes wrapper invocations. This small lock
        # also prevents an overlapping child from losing a charged reservation.
        descriptor = os.open(self.lock_path, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o660)
        with os.fdopen(descriptor, "r+") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            self.check_deadline()
            value = self.usage()
            update(value)
            temporary = self.usage_path.with_name(self.usage_path.name + "." + uuid.uuid4().hex)
            try:
                write_new_json(temporary, value)
                os.replace(temporary, self.usage_path)
                sync_directory(self.path.parent)
            finally:
                temporary.unlink(missing_ok=True)
            self.check_deadline()

    def bind_current(self, directory: Path) -> None:
        current = self.receipt_path(directory)
        def update(value):
            if value["current_receipt"] not in (None, current):
                raise ValueError("maintenance invocation already owns a different current receipt")
            # Existing journals are historical; binding cannot relabel them to
            # evade the aggregate prior-read budget.
            if value["current_receipt"] is None and directory.exists():
                raise ValueError("current repair receipt already exists")
            value["current_receipt"] = current
        self.change_usage(update)

    def reserve(self, size: int, *, directory: Path | None = None) -> None:
        positive_integer(size)
        if size > self.config["max_bytes"]:
            raise ValueError("maintenance receipt exceeds its individual byte bound")
        if directory is None:
            raise ValueError("maintenance authentication requires its receipt identity")
        target = self.receipt_path(directory)
        def update(value):
            current = target == value["current_receipt"]
            counter = "current_bytes_read" if current else "prior_bytes_read"
            limit = self.config["max_current_readback_bytes" if current else "max_prior_bytes"]
            if size > limit - value[counter]:
                raise ValueError(("current receipt readback" if current else "prior repair receipts")
                                 + " exceed aggregate byte bound")
            value[counter] += size
            value["reservations"] += 1
        self.change_usage(update)

    def readback_rejections(self, directory: Path, measured_bytes: int, reads: int) -> list[str]:
        """Admit the complete current-journal read schedule before its APPLY."""
        positive_integer(measured_bytes)
        positive_integer(reads)
        current = self.receipt_path(directory)
        rejections = []
        def update(value):
            if value["current_receipt"] != current:
                raise ValueError("current readback admission has no matching bound repair receipt")
            required = measured_bytes * reads
            limit = self.config["max_current_readback_bytes"]
            if measured_bytes > self.config["max_bytes"]:
                rejections.append("measured current receipt exceeds individual byte bound")
            if required > limit - value["current_bytes_read"]:
                rejections.append("complete current receipt readback exceeds aggregate byte bound")
            write_new_json(self.path.with_name(self.path.stem + "-current-readback-admission.json"), {
                "schema": "laplace.legacy-content-current-readback-admission/v1",
                "resource_receipt": self.path.name, "current_receipt": current,
                "measured_plan_bytes": measured_bytes, "read_multiplicity": reads,
                "required_readback_bytes": required, "already_consumed_bytes": value["current_bytes_read"],
                "max_current_readback_bytes": limit, "rejections": rejections})
        self.change_usage(update)
        return rejections


def inherited_repair_resources(receipt: Path, *, receipt_root: Path, max_bytes: int,
                              max_line_bytes: int, max_prior_bytes: int,
                              timeout_seconds: int) -> MaintenanceResources:
    path = Path(os.environ.get(RESOURCE_ENV, "/missing"))
    if receipt.is_symlink() or path.is_symlink() or path.resolve().parent != receipt.resolve():
        raise ValueError("repair has no resource receipt in its owned service transaction")
    resources = MaintenanceResources(path)
    if resources.repair_root != receipt_root.resolve():
        raise ValueError("repair resource receipt identifies another journal estate")
    for key, value in (("max_bytes", max_bytes), ("max_line_bytes", max_line_bytes),
                       ("max_prior_bytes", max_prior_bytes), ("timeout_seconds", timeout_seconds)):
        if resources.config[key] != value:
            raise ValueError("repair resource bound differs from its maintenance invocation: " + key)
    resources.check_deadline()
    return resources


def run(argv: list[str], *, timeout: int = 75) -> str:
    return subprocess.run(argv, check=True, stdin=subprocess.DEVNULL,
                          capture_output=True, text=True, timeout=timeout).stdout


def transaction_identity(state: Path) -> dict | None:
    path = state / "transaction.json"
    try:
        value = path.lstat()
    except FileNotFoundError:
        return None
    if not stat.S_ISREG(value.st_mode) or value.st_uid != 0 or value.st_mode & 0o022:
        raise ValueError("installed managed transaction has an untrusted file identity")
    return {"device": value.st_dev, "inode": value.st_ino, "size": value.st_size,
            "modified_ns": value.st_mtime_ns, "changed_ns": value.st_ctime_ns}


def service_status(name: str, execute=run) -> dict:
    if name == "api":
        values = dict(line.split("=", 1) for line in execute([
            "/usr/bin/systemctl", "show", "laplace-api.service", "--no-pager",
            "--property=LoadState,ActiveState,MainPID"]).splitlines() if "=" in line)
        return {"unit": "laplace-api.service", "load_state": values.get("LoadState"),
                "active_state": values.get("ActiveState"),
                "main_pid": int(values.get("MainPID", "0")), "operator_stopped": False}
    result = json.loads(execute(["sudo", "-n", CONTROL, name, "status"]))
    if result.get("unit") != "laplace-" + name + ".service" or not isinstance(result.get("operator_stopped"), bool):
        raise ValueError("installed service status does not identify the selected unit and stop intent")
    return result


def laplace_component(argv: list[bytes]) -> str | None:
    names = {"laplace", "Laplace.Cli", "Laplace.Cli.dll", "laplace-mcp", "laplace-lichess"}
    for component in ("OpenAICompat", "Mcp", "Lichess"):
        names.update({"Laplace.Endpoints." + component, "Laplace.Endpoints." + component + ".dll"})
    if not argv:
        return None
    executable = Path(os.fsdecode(argv[0])).name
    if executable in names:
        return executable
    if executable == "dotnet":
        index = 2 if argv[1:2] == [b"exec"] else 1
        if len(argv) > index:
            assembly = Path(os.fsdecode(argv[index])).name
            return assembly if assembly in names else None
    return None


def standalone_writers(proc: Path, ignored_pids: set[int]) -> list[dict]:
    writers = []
    for process in proc.iterdir():
        if not process.name.isdecimal() or int(process.name) in ignored_pids:
            continue
        try:
            argv = (process / "cmdline").read_bytes().split(b"\0")
            component = laplace_component(argv)
            if component is None:
                continue
            environment = dict(item.split(b"=", 1) for item in
                               (process / "environ").read_bytes().split(b"\0") if b"=" in item)
        except (FileNotFoundError, ProcessLookupError):
            continue
        except PermissionError as error:
            raise ValueError("cannot establish standalone process scope for pid " + process.name) from error
        connection = os.fsdecode(environment.get(b"LAPLACE_DB", b""))
        fields = {}
        unresolved = any(character in connection for character in "\"'\n\r")
        if not unresolved:
            for item in connection.split(";"):
                if "=" in item:
                    key, value = item.split("=", 1)
                    fields[key.strip().lower()] = value.strip()
                elif item.strip():
                    unresolved = True
        database = fields.get("database", fields.get("initial catalog")) or os.fsdecode(environment.get(b"PGDATABASE", b"")) or "laplace"
        if not unresolved and database != "laplace":
            continue
        host = fields.get("host", fields.get("server")) or os.fsdecode(environment.get(b"PGHOST", b"")) or "/var/run/postgresql"
        port = fields.get("port") or os.fsdecode(environment.get(b"PGPORT", b"")) or "5432"
        local = host in ("/var/run/postgresql", "localhost", "127.0.0.1", "::1") and port == "5432"
        # Another explicit Unix socket/port identifies a separate local cluster.
        # DNS/multihost aliases remain unresolved: they might resolve to this one.
        separate = (host.startswith("/") and host != "/var/run/postgresql") or (port.isdecimal() and port != "5432")
        if separate and not unresolved:
            continue
        writers.append({"pid": int(process.name), "component": component,
                        "scope": "local-laplace" if local and not unresolved else "unresolved"})
    return sorted(writers, key=lambda item: item["pid"])


def published_application_generation(*, root: Path | None = None, retained: dict | None = None,
                                     verification_receipt: Path | None = None,
                                     retain_to: Path | None = None) -> dict | None:
    source_sha = retained.get("source_sha") if retained is not None else os.environ.get("LAPLACE_REPAIR_PUBLISHED_SOURCE")
    if not source_sha:
        return None
    root = root or Path(__file__).resolve().parents[1]
    current = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    if retained is None and source_sha != current:
        raise ValueError("published application source does not match the repair checkout")
    prefix = Path(os.environ.get("LAPLACE_INSTALL_PREFIX", "/opt/laplace")) / "app"
    files = {}
    entries = {"api": prefix / "Laplace.Endpoints.OpenAICompat.dll",
               "mcp": Path(str((prefix / "laplace-mcp").resolve()) + ".dll"),
               "lichess": Path(str((prefix / "laplace-lichess").resolve()) + ".dll")}
    for role, entry in entries.items():
        if not entry.is_file():
            raise ValueError("published managed entry assembly is missing: " + role)
        selected = [entry, entry.parent / "Laplace.Core.dll", entry.parent / "Laplace.Chess.dll"]
        for path in selected:
            if path != entry and not path.is_file():
                continue  # A service need not reference every domain assembly.
            digest = hashlib.sha256()
            with path.open("rb") as stream:
                while block := stream.read(1024 * 1024): digest.update(block)
            files[role + "/" + path.name] = {"path": str(path.resolve()), "bytes": path.stat().st_size,
                                              "sha256": digest.hexdigest()}
    verified = verification_receipt or root / "build/.applications-verified.json"
    if not verified.is_file():
        raise ValueError("successful installed application verification receipt is missing")
    verification_bytes = verified.read_bytes()
    observed = {"schema": "laplace.repair-producer-generation/v1", "source_sha": source_sha,
            "publication": "completed-before-quiescence", "assemblies": files,
            "application_verification_sha256": hashlib.sha256(verification_bytes).hexdigest()}
    if retained is not None and observed != retained:
        raise ValueError("held quiescence producer generation changed")
    if retain_to is not None:
        with (retain_to / "producer-application-verification.json").open("xb") as target:
            target.write(verification_bytes)
            target.flush()
            os.fsync(target.fileno())
        sync_directory(retain_to)
    return observed


def repair_module():
    path = Path(__file__).resolve().with_name("repair-legacy-content.py")
    spec = importlib.util.spec_from_file_location("quiescence_repair_receipts", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def process_identity(pid: int, proc: Path = PROC) -> dict | None:
    try:
        # comm may contain spaces or parentheses; fields after its final ')' are
        # fixed procfs fields. starttime is field22, index19 after state(field3).
        fields = (proc / str(pid) / "stat").read_text().rsplit(")", 1)[1].split()
        return {"pid": pid, "start_ticks": int(fields[19]),
                "boot_id": (proc / "sys/kernel/random/boot_id").read_text().strip()}
    except (FileNotFoundError, ProcessLookupError):
        return None


def bind_repair_attempt(quiescence_directory: Path, repair_directory: Path, *,
                       source_sha: str, state: Path = STATE, proc: Path = PROC) -> None:
    retained = json.loads((quiescence_directory / "quiescence-confirmed.json").read_text())
    identity = transaction_identity(state)
    if identity is None or identity != retained.get("transaction_identity") \
            or (quiescence_directory / "commit-submission.json").exists():
        raise ValueError("repair cannot bind to an unowned service transaction")
    owner = process_identity(os.getpid(), proc)
    if owner is None:
        raise ValueError("repair process identity is unavailable")
    resource_path = Path(os.environ.get(RESOURCE_ENV, "/missing"))
    if resource_path.is_symlink() or resource_path.resolve().parent != quiescence_directory.resolve():
        raise ValueError("repair ownership binding has no maintenance resource receipt")
    resources = MaintenanceResources(resource_path)
    resources.bind_current(repair_directory)
    write_new_json(quiescence_directory / ("repair-attempt-" + uuid.uuid4().hex + ".json"), {
        "schema": "laplace.quiescence-repair-attempt/v1", "transaction_identity": identity,
        "repair_directory": str(repair_directory.resolve()), "repair_process": owner,
        "resource_receipt": str(resource_path.resolve()),
        "repair_source_sha": source_sha, "at_unix_nanoseconds": time.time_ns()})


def database_submission_statuses(paths: list[Path], *, command: list[str] | None = None,
                                deadline: float | None = None, max_bytes: int = MAX_BYTES,
                                max_line_bytes: int = MAX_LINE_BYTES, budget=None) -> list[dict]:
    """Native outcome signal for unknown commits; exact row reconciliation follows.

    A committed/aborted xid permits reconciliation, never a blind replay or a
    writer restart. Status is queried only on the exact retained cluster/database.
    """
    if not paths:
        return []
    deadline = time.monotonic() + TIMEOUT_SECONDS if deadline is None else deadline
    repair = repair_module()
    if budget is None:
        budget = repair.PriorReceiptBudget(max_bytes)
    requests = []
    for path in paths:
        manifest, context = repair.verified_plan(path.parent, deadline=deadline,
            max_bytes=max_bytes, max_line_bytes=max_line_bytes, budget=budget)
        xid = context.get("transaction")
        if not isinstance(xid, str) or not xid.isdecimal():
            raise ValueError("unknown repair has no native transaction identity")
        requests.append({"receipt": str(path.resolve()), "plan_sha256": manifest["plan_sha256"],
            **{key: context.get(key) for key in ("database", "database_oid", "system_identifier", "transaction")}})
    if len({item["database"] for item in requests}) != 1:
        raise ValueError("unknown repairs do not identify one database")
    if command is None:
        prefix = Path(os.environ.get("LAPLACE_PG_PREFIX", "/opt/laplace/pgsql-18"))
        command = [str(prefix / "bin/psql"), "-XqAt", "-w", "-v", "ON_ERROR_STOP=1",
            "-h", "/var/run/postgresql", "-p", "5432", "-U", os.environ.get("PGUSER", "laplace_admin"),
            "-d", requests[0]["database"]]
    payload = json.dumps(requests, sort_keys=True).replace("'", "''")
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise ValueError("repair transaction status deadline expired")
    sql = f"""BEGIN READ ONLY;
SET LOCAL statement_timeout='{max(1, int(remaining * 1000))}ms';
WITH requested AS (
 SELECT * FROM jsonb_to_recordset('{payload}'::jsonb) AS r(
 receipt text,plan_sha256 text,database text,database_oid text,system_identifier text,transaction text)
), compared AS (
 SELECT *,database=current_database()
   AND database_oid=(SELECT oid::text FROM pg_database WHERE datname=current_database())
   AND system_identifier=(SELECT system_identifier::text FROM pg_control_system()) AS database_identity_matches
 FROM requested
)
SELECT jsonb_agg(jsonb_build_object('receipt',receipt,'plan_sha256',plan_sha256,
 'database_identity_matches',database_identity_matches,'transaction',transaction,
 'status',CASE WHEN database_identity_matches THEN pg_xact_status(transaction::xid8) END)
 ORDER BY receipt) FROM compared;
COMMIT;
"""
    result = subprocess.run(command, input=sql, capture_output=True, text=True, check=True, timeout=remaining)
    observed = json.loads(result.stdout)
    expected = {(item["receipt"], item["plan_sha256"], item["transaction"]) for item in requests}
    if not isinstance(observed, list) or len(observed) != len(expected) \
            or {(item.get("receipt"), item.get("plan_sha256"), item.get("transaction")) for item in observed} != expected:
        raise ValueError("native repair transaction status inventory is incomplete")
    return observed


def require_finished_database_submissions(statuses: list[dict]) -> None:
    if any(item.get("database_identity_matches") is not True
           or item.get("status") not in ("committed", "aborted") for item in statuses):
        raise ValueError("repair transaction is in progress, unavailable or on another database; writer quiescence remains held")


def repair_lifecycle_command(command: list[str]) -> bool:
    return len(command) == 3 and Path(command[0]).name == "bash" and command[2] == "laplace" \
        and Path(command[1]).resolve() == Path(__file__).resolve().with_name("repair-legacy-content-lifecycle.sh")


def chess_observation_command(command: list[str]) -> bool:
    return len(command) == 2 and Path(command[0]).name == "bash" \
        and Path(command[1]).resolve() == Path(__file__).resolve().with_name("repair-chess-position-outcomes.sh")


def chess_database_observation(application_name: str) -> dict:
    """Read the exact cluster and any remaining sessions of this source transition.

    The source procedure commits bounded batches, so one xid cannot represent its
    outcome. Its durable pending receipt requires replay/reconciliation; its unique
    transport application name establishes that no old backend is still mutating.
    """
    if re.fullmatch(r"laplace-chess-outcome-[0-9a-f]{32}", application_name) is None:
        raise ValueError("invalid chess transition database session identity")
    prefix = Path(os.environ.get("LAPLACE_PG_PREFIX", "/opt/laplace/pgsql-18"))
    command = [str(prefix / "bin/psql"), "-XqAt", "-w", "-v", "ON_ERROR_STOP=1",
        "-h", "/var/run/postgresql", "-p", "5432", "-U", os.environ.get("PGUSER", "laplace_admin"), "-d", "laplace"]
    sql = f"""BEGIN READ ONLY;
SET LOCAL statement_timeout='5s';
SELECT jsonb_build_object('database',current_database(),
 'database_oid',(SELECT oid::text FROM pg_database WHERE datname=current_database()),
 'system_identifier',(SELECT system_identifier::text FROM pg_control_system()),
 'sessions',coalesce((SELECT jsonb_agg(jsonb_build_object('pid',pid,'backend_start',backend_start,
 'state',state,'query_start',query_start)) FROM
 (SELECT pid,backend_start,state,query_start FROM pg_stat_activity
  WHERE application_name='{application_name}' LIMIT 129) owned),'[]'::jsonb));
COMMIT;
"""
    result = subprocess.run(command, input=sql, capture_output=True, text=True, check=True, timeout=10)
    if len(result.stdout.encode()) > MAX_METADATA_BYTES:
        raise ValueError("chess transition database observation exceeds metadata bound")
    observed = json.loads(result.stdout)
    if not isinstance(observed, dict) or observed.get("database") != "laplace" \
            or not isinstance(observed.get("sessions"), list) \
            or any(not isinstance(observed.get(key), str) or not observed[key].isdecimal()
                   for key in ("database_oid", "system_identifier")):
        raise ValueError("incomplete chess transition database identity")
    return observed


class Quiescence:
    def __init__(self, directory: Path, *, state: Path = STATE, proc: Path = PROC,
                 repair_root: Path = REPAIR_RECEIPTS, resources: MaintenanceResources | None = None,
                 chess_root: Path = CHESS_OBSERVATION_RECEIPTS,
                 execute=run):
        self.directory, self.state, self.proc, self.execute = directory, state, proc, execute
        self.prior: dict[str, dict] = {}
        self.stopped: set[str] = set()
        self.begin_submitted = self.begin_confirmed = False
        self.transaction = None
        self.repair_root = repair_root
        self.resources = resources
        self.chess_root = chess_root

    def record(self, name: str, value: dict) -> None:
        write_new_json(self.directory / (name + ".json"), value)

    def wait_stopped(self, name: str) -> None:
        deadline = time.monotonic() + 60
        while True:
            status = service_status(name, self.execute)
            if status["active_state"] in ("inactive", "failed") and status["main_pid"] == 0:
                return
            if time.monotonic() >= deadline:
                raise ValueError("managed writer did not drain: " + name)
            time.sleep(0.2)

    def wait_started(self, name: str) -> dict:
        deadline = time.monotonic() + 60
        while True:
            status = service_status(name, self.execute)
            if status["active_state"] == "active" and status["main_pid"] > 0:
                return status
            if time.monotonic() >= deadline:
                raise ValueError("managed writer did not restart: " + name)
            time.sleep(0.2)

    def stop(self, name: str) -> None:
        self.record("stop-" + name + "-submission", {"service": name, "prior": self.prior[name]})
        # Track a submission before invoking the helper. A failed acknowledgement
        # may still have stopped the process; restoration uses fresh status.
        self.stopped.add(name)
        if name == "api":
            self.execute(["sudo", "-n", "systemctl", "stop", "laplace-api"])
        else:
            self.execute(["sudo", "-n", CONTROL, name, "stop"])
        self.wait_stopped(name)
        self.record("stop-" + name + "-confirmed", {"service": name})

    def enter(self) -> None:
        if transaction_identity(self.state) is not None:
            raise ValueError("an existing managed deployment owns service exclusion")
        self.prior = {name: service_status(name, self.execute) for name in ("api", "mcp", "lichess")}
        if any(s["active_state"] not in ("active", "inactive", "failed") or
               s["load_state"] not in ("loaded", "not-found") for s in self.prior.values()):
            raise ValueError("managed service is transitioning or has unresolved load state")
        writers = standalone_writers(self.proc, {s["main_pid"] for s in self.prior.values()})
        if writers:
            raise ValueError("standalone database writers remain: " + json.dumps(writers))
        self.record("prior-services", {"schema": "laplace.database-service-quiescence/v1",
                                     "database": "laplace", "services": self.prior,
                                     "producer_generation": published_application_generation(retain_to=self.directory)})
        for name in ("mcp", "lichess"):
            # Inactive services need no stop: their original absent stop marker
            # must remain absent. begin excludes later starts during the repair.
            if self.prior[name]["active_state"] == "active":
                self.stop(name)
        self.record("begin-submission", {"prior_transaction": None})
        self.begin_submitted = True
        self.execute(["sudo", "-n", DEPLOY, "begin"])
        self.begin_confirmed = True
        self.transaction = transaction_identity(self.state)
        if self.transaction is None:
            raise ValueError("installed helper acknowledged begin without a transaction")
        self.record("begin-confirmed", {"transaction_identity": self.transaction})
        # A control could have raced between stop and begin. The installed
        # transaction now blocks controls; independently prove that both stopped.
        for name in ("mcp", "lichess"):
            if self.prior[name]["load_state"] != "not-found":
                self.wait_stopped(name)
        if self.prior["api"]["active_state"] == "active":
            self.stop("api")
        writers = standalone_writers(self.proc, set())
        if writers:
            raise ValueError("standalone database writers remain after drain: " + json.dumps(writers))
        self.record("quiescence-confirmed", {"transaction_identity": self.transaction,
                                            "managed_main_pids": [0, 0, 0]})

    def repair_outcomes(self, *, permit_finished_unknown: bool = False) -> list[Path]:
        chess_estate = self.directory / "chess-observation-estate.json"
        if chess_estate.exists():
            estate = bounded_metadata(chess_estate)
            if estate.get("transaction_identity") != self.transaction \
                    or estate.get("root") != str(self.chess_root.resolve()):
                raise ValueError("chess observation estate does not belong to this maintenance transaction")
            if standalone_writers(self.proc, set()):
                raise ValueError("a chess transition writer is still running; services remain quiesced")
            observed = chess_database_observation(estate.get("application_name", ""))
            self.record("chess-observation-database-" + uuid.uuid4().hex, observed)
            if any(observed.get(key) != estate.get("database_identity", {}).get(key)
                   for key in ("database", "database_oid", "system_identifier")) or observed["sessions"]:
                raise ValueError("chess transition database differs or its backend is still present; services remain quiesced")
            pending = self.chess_root / "pending.json"
            if pending.exists():
                value = bounded_metadata(pending)
                if value.get("Database") != observed["database"]:
                    raise ValueError("pending chess source transition targets another database")
                if not permit_finished_unknown:
                    raise ValueError("chess source transition requires complete retained-evidence reconciliation; services remain quiesced")
                return [pending]
            return []
        bindings = sorted(self.directory.glob("repair-attempt-*.json"))
        estate_path = self.directory / "repair-estate.json"
        if not bindings and not estate_path.exists():
            return []
        if estate_path.exists():
            estate = json.loads(estate_path.read_text())
            if estate_path.is_symlink() or estate.get("transaction_identity") != self.transaction \
                    or estate.get("repair_root") != str(self.repair_root.resolve()):
                raise ValueError("repair receipt estate does not belong to this service transaction")
        for path in bindings:
            if path.is_symlink():
                raise ValueError("repair ownership binding cannot be a symlink")
            binding = json.loads(path.read_text())
            target = Path(binding["repair_directory"])
            if binding.get("schema") != "laplace.quiescence-repair-attempt/v1" \
                    or binding.get("transaction_identity") != self.transaction \
                    or target.is_symlink() or target.resolve().parent != self.repair_root.resolve():
                raise ValueError("repair ownership binding does not match this service transaction")
            owner = binding.get("repair_process")
            if not isinstance(owner, dict) or not isinstance(owner.get("pid"), int):
                raise ValueError("repair ownership binding lacks process identity")
            if process_identity(owner["pid"], self.proc) == owner:
                raise ValueError("bound repair process is still running; writers remain quiesced")
        if self.resources is None or self.resources.repair_root != self.repair_root.resolve():
            raise ValueError("repair outcome verification has no admitted maintenance resources")
        self.resources.check_deadline()
        bounds = {"max_bytes": self.resources.config["max_bytes"],
                  "max_line_bytes": self.resources.config["max_line_bytes"],
                  "budget": self.resources, "deadline": self.resources.deadline}
        pending = repair_module().unresolved_submissions(self.repair_root, **bounds) if self.repair_root.exists() else []
        statuses = database_submission_statuses(pending, **bounds)
        self.record("database-status-" + uuid.uuid4().hex, {"transaction_identity": self.transaction,
            "unknown_submissions": [str(path) for path in pending], "native_transaction_statuses": statuses,
            "maintenance_resources": str(self.resources.path.resolve()), "maintenance_usage": self.resources.usage()})
        self.resources.check_deadline()
        require_finished_database_submissions(statuses)
        if pending and not permit_finished_unknown:
            raise ValueError("repair submission outcome requires exact database reconciliation; writers remain quiesced")
        return pending

    def resume(self) -> None:
        if self.directory.is_symlink() or (self.directory / "restored.json").exists() \
                or (self.directory / "commit-submission.json").exists():
            raise ValueError("service transaction cannot be resumed after an unknown or completed service commit")
        begin = json.loads((self.directory / "begin-confirmed.json").read_text())
        confirmed = json.loads((self.directory / "quiescence-confirmed.json").read_text())
        prior = json.loads((self.directory / "prior-services.json").read_text())
        identity = transaction_identity(self.state)
        if identity is None or identity != begin.get("transaction_identity") \
                or identity != confirmed.get("transaction_identity"):
            raise ValueError("held service transaction identity changed; no transaction was claimed")
        producer = prior.get("producer_generation")
        if not isinstance(producer, dict) or producer != published_application_generation(retained=producer,
                verification_receipt=self.directory / "producer-application-verification.json"):
            raise ValueError("held service transaction has no unchanged published producer generation")
        self.transaction = identity
        self.prior = prior["services"]
        self.stopped = {name for name in ("api", "mcp", "lichess")
                        if (self.directory / ("stop-" + name + "-submission.json")).is_file()}
        self.begin_submitted = self.begin_confirmed = True
        for name in ("api", "mcp", "lichess"):
            status = service_status(name, self.execute)
            if status["active_state"] not in ("inactive", "failed") or status["main_pid"] != 0:
                raise ValueError("held managed writer is no longer stopped: " + name)
        if standalone_writers(self.proc, set()):
            raise ValueError("standalone database writers remain; held transaction was not released")
        if not any(self.directory.glob("repair-attempt-*.json")) and not (self.directory / "repair-estate.json").exists() \
                and not (self.directory / "chess-observation-estate.json").exists():
            raise ValueError("held service transaction has no bound repair attempt")
        pending = self.repair_outcomes(permit_finished_unknown=True)
        self.record("resume-confirmed-" + uuid.uuid4().hex, {"transaction_identity": identity,
            "producer_generation": producer, "requires_locked_database_reconciliation": [str(path) for path in pending]})

    def restore(self) -> None:
        try:
            self.repair_outcomes()
        except (OSError, ValueError, subprocess.SubprocessError) as error:
            self.record("database-quiescence-held-" + uuid.uuid4().hex, {
                "transaction_identity": self.transaction, "reason_type": type(error).__name__,
                "disposition": "managed-writers-held-pending-exact-database-reconciliation"})
            raise
        if self.begin_submitted:
            if not self.begin_confirmed:
                raise ValueError("managed begin acknowledgement is unknown; its transaction was not committed or claimed")
            current = transaction_identity(self.state)
            if current != self.transaction:
                raise ValueError("managed transaction identity changed; no transaction was committed")
            self.record("commit-submission", {"transaction_identity": self.transaction})
            self.execute(["sudo", "-n", DEPLOY, "commit"])
            if transaction_identity(self.state) is not None:
                raise ValueError("managed commit did not release service exclusion")
            self.record("commit-confirmed", {"transaction_identity": self.transaction})
        elif transaction_identity(self.state) is not None:
            raise ValueError("another deployment acquired service exclusion; prior service state retained for recovery")
        failures = []
        for name in ("api", "mcp", "lichess"):
            if name not in self.stopped or self.prior[name]["operator_stopped"]:
                continue
            try:
                if name == "api":
                    self.execute(["sudo", "-n", "systemctl", "start", "laplace-api"])
                else:
                    self.execute(["sudo", "-n", CONTROL, name, "start"])
                status = self.wait_started(name)
                self.record("restore-" + name + "-confirmed", {"service": name, "status": status})
            except (OSError, ValueError, subprocess.SubprocessError):
                failures.append(name)
        if failures:
            raise ValueError("managed writer restoration failed: " + ", ".join(failures))
        self.record("restored", {"services": self.prior})


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", required=True)
    parser.add_argument("--resume-receipt", type=Path)
    parser.add_argument("--resume-if-needed", action="store_true")
    parser.add_argument("--max-bytes", type=positive_argument, default=MAX_BYTES)
    parser.add_argument("--max-line-bytes", type=positive_argument, default=MAX_LINE_BYTES)
    parser.add_argument("--max-prior-bytes", type=positive_argument, default=MAX_BYTES)
    parser.add_argument("--max-current-readback-bytes", type=positive_argument, default=2 * MAX_BYTES)
    parser.add_argument("--timeout-seconds", type=positive_argument, default=TIMEOUT_SECONDS)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if not command:
        parser.error("a database maintenance command is required")
    if args.database != "laplace":
        return 0 if args.resume_if_needed else subprocess.run(command, check=False).returncode
    if os.environ.get("PGHOST", "/var/run/postgresql") != "/var/run/postgresql" or os.environ.get("PGPORT", "5432") != "5432":
        parser.error("managed service maintenance targets the fixed local PostgreSQL socket and port")
    RECEIPTS.mkdir(parents=True, mode=0o770, exist_ok=True)
    with (RECEIPTS / "orchestration.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        pending = [old for old in RECEIPTS.iterdir() if old.is_dir()
                   and (old / "prior-services.json").exists() and not (old / "restored.json").exists()]
        if len(pending) > 1:
            raise ValueError("multiple unresolved service transactions require exact ownership resolution")
        if args.resume_receipt is not None and (args.resume_receipt.is_symlink()
                or args.resume_receipt.resolve().parent != RECEIPTS.resolve()
                or len(pending) != 1 or pending[0].resolve() != args.resume_receipt.resolve()):
            raise ValueError("resume receipt is not the single unresolved owned service transaction")
        if args.resume_if_needed and not pending:
            return 0
        if args.resume_if_needed and chess_observation_command(command) and pending \
                and not (pending[0] / "chess-observation-estate.json").exists():
            # This source-specific probe neither claims nor restores another
            # maintenance owner's retained transaction.
            return 0
        if pending:
            directory = pending[0]
            invoked = json.loads((directory / "maintenance-command.json").read_text())
            if not (repair_lifecycle_command(command) or chess_observation_command(command)) \
                    or invoked.get("repair_lifecycle") is not True \
                    or invoked.get("argv_sha256") != hashlib.sha256(json.dumps(command).encode()).hexdigest():
                raise ValueError("held repair must resume through its original measurement-lane lifecycle command")
            resources = MaintenanceResources.create(directory, repair_root=REPAIR_RECEIPTS,
                max_bytes=args.max_bytes, max_line_bytes=args.max_line_bytes, max_prior_bytes=args.max_prior_bytes,
                max_current_readback_bytes=args.max_current_readback_bytes, timeout_seconds=args.timeout_seconds) \
                if repair_lifecycle_command(command) else None
            boundary = Quiescence(directory, resources=resources)
            boundary.resume()  # A rejected resume must never enter restore().
        else:
            directory = RECEIPTS / (str(time.time_ns()) + "-" + uuid.uuid4().hex)
            directory.mkdir(mode=0o770)
            sync_directory(RECEIPTS)
            write_new_json(directory / "maintenance-command.json", {
                "repair_lifecycle": repair_lifecycle_command(command) or chess_observation_command(command),
                "argv_sha256": hashlib.sha256(json.dumps(command).encode()).hexdigest()})
            resources = MaintenanceResources.create(directory, repair_root=REPAIR_RECEIPTS,
                max_bytes=args.max_bytes, max_line_bytes=args.max_line_bytes, max_prior_bytes=args.max_prior_bytes,
                max_current_readback_bytes=args.max_current_readback_bytes, timeout_seconds=args.timeout_seconds) \
                if repair_lifecycle_command(command) else None
            boundary = Quiescence(directory, resources=resources)
        try:
            if not pending:
                boundary.enter()
                if repair_lifecycle_command(command):
                    boundary.record("repair-estate", {"transaction_identity": boundary.transaction,
                        "repair_root": str(boundary.repair_root.resolve())})
                elif chess_observation_command(command):
                    application_name = "laplace-chess-outcome-" + uuid.uuid4().hex
                    database = chess_database_observation(application_name)
                    if database["sessions"]:
                        raise ValueError("new chess transition identity already has database sessions")
                    boundary.record("chess-observation-estate", {"transaction_identity": boundary.transaction,
                        "root": str(boundary.chess_root.resolve()), "application_name": application_name,
                        "database_identity": {key: database[key] for key in ("database", "database_oid", "system_identifier")}})
            environment = {**os.environ, "LAPLACE_DATABASE_QUIESCENCE_RECEIPT": str(directory.resolve())}
            if chess_observation_command(command):
                environment[CHESS_APPLICATION_ENV] = bounded_metadata(directory / "chess-observation-estate.json")["application_name"]
            if resources is not None:
                resources.check_deadline()
                environment[RESOURCE_ENV] = str(resources.path.resolve())
            else:
                environment.pop(RESOURCE_ENV, None)
            return subprocess.run(command, check=False, env=environment,
                timeout=resources.deadline - time.monotonic() if resources is not None
                    else args.timeout_seconds if chess_observation_command(command) else None).returncode
        finally:
            boundary.restore()


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        # subprocess output may contain incidental credentials. Retain only the
        # controlled domain error or failure class, never raw command output.
        print("database service maintenance failed: " + (str(error) if isinstance(error, ValueError) else type(error).__name__),
              file=__import__("sys").stderr)
        raise SystemExit(1)
