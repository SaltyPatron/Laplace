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
import os
from pathlib import Path
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
    write_new_json(quiescence_directory / ("repair-attempt-" + uuid.uuid4().hex + ".json"), {
        "schema": "laplace.quiescence-repair-attempt/v1", "transaction_identity": identity,
        "repair_directory": str(repair_directory.resolve()), "repair_process": owner,
        "repair_source_sha": source_sha, "at_unix_nanoseconds": time.time_ns()})


def database_submission_statuses(paths: list[Path], *, command: list[str] | None = None,
                                deadline: float | None = None) -> list[dict]:
    """Native outcome signal for unknown commits; exact row reconciliation follows.

    A committed/aborted xid permits reconciliation, never a blind replay or a
    writer restart. Status is queried only on the exact retained cluster/database.
    """
    if not paths:
        return []
    deadline = time.monotonic() + 1800 if deadline is None else deadline
    repair = repair_module()
    requests = []
    for path in paths:
        manifest, context = repair.verified_plan(path.parent, deadline=deadline)
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


class Quiescence:
    def __init__(self, directory: Path, *, state: Path = STATE, proc: Path = PROC,
                 repair_root: Path = REPAIR_RECEIPTS, execute=run):
        self.directory, self.state, self.proc, self.execute = directory, state, proc, execute
        self.prior: dict[str, dict] = {}
        self.stopped: set[str] = set()
        self.begin_submitted = self.begin_confirmed = False
        self.transaction = None
        self.repair_root = repair_root

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
        pending = repair_module().unresolved_submissions(self.repair_root) if self.repair_root.exists() else []
        statuses = database_submission_statuses(pending)
        self.record("database-status-" + uuid.uuid4().hex, {"transaction_identity": self.transaction,
            "unknown_submissions": [str(path) for path in pending], "native_transaction_statuses": statuses})
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
        if not any(self.directory.glob("repair-attempt-*.json")) and not (self.directory / "repair-estate.json").exists():
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
        if pending:
            directory = pending[0]
            invoked = json.loads((directory / "maintenance-command.json").read_text())
            if not repair_lifecycle_command(command) or invoked.get("repair_lifecycle") is not True \
                    or invoked.get("argv_sha256") != hashlib.sha256(json.dumps(command).encode()).hexdigest():
                raise ValueError("held repair must resume through its original measurement-lane lifecycle command")
            boundary = Quiescence(directory)
            boundary.resume()  # A rejected resume must never enter restore().
        else:
            directory = RECEIPTS / (str(time.time_ns()) + "-" + uuid.uuid4().hex)
            directory.mkdir(mode=0o770)
            sync_directory(RECEIPTS)
            write_new_json(directory / "maintenance-command.json", {
                "repair_lifecycle": repair_lifecycle_command(command),
                "argv_sha256": hashlib.sha256(json.dumps(command).encode()).hexdigest()})
            boundary = Quiescence(directory)
        try:
            if not pending:
                boundary.enter()
                if repair_lifecycle_command(command):
                    boundary.record("repair-estate", {"transaction_identity": boundary.transaction,
                        "repair_root": str(boundary.repair_root.resolve())})
            return subprocess.run(command, check=False, env={**os.environ,
                "LAPLACE_DATABASE_QUIESCENCE_RECEIPT": str(directory.resolve())}).returncode
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
