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


def published_application_generation(*, root: Path | None = None) -> dict | None:
    source_sha = os.environ.get("LAPLACE_REPAIR_PUBLISHED_SOURCE")
    if not source_sha:
        return None
    root = root or Path(__file__).resolve().parents[1]
    current = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    if source_sha != current:
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
    verified = root / "build/.applications-verified.json"
    if not verified.is_file():
        raise ValueError("successful installed application verification receipt is missing")
    return {"schema": "laplace.repair-producer-generation/v1", "source_sha": source_sha,
            "publication": "completed-before-quiescence", "assemblies": files,
            "application_verification_sha256": hashlib.sha256(verified.read_bytes()).hexdigest()}


class Quiescence:
    def __init__(self, directory: Path, *, state: Path = STATE, proc: Path = PROC, execute=run):
        self.directory, self.state, self.proc, self.execute = directory, state, proc, execute
        self.prior: dict[str, dict] = {}
        self.stopped: set[str] = set()
        self.begin_submitted = self.begin_confirmed = False
        self.transaction = None

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
                                     "producer_generation": published_application_generation()})
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

    def restore(self) -> None:
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
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if not command:
        parser.error("a database maintenance command is required")
    if args.database != "laplace":
        return subprocess.run(command, check=False).returncode
    if os.environ.get("PGHOST", "/var/run/postgresql") != "/var/run/postgresql" or os.environ.get("PGPORT", "5432") != "5432":
        parser.error("managed service maintenance targets the fixed local PostgreSQL socket and port")
    RECEIPTS.mkdir(parents=True, mode=0o770, exist_ok=True)
    with (RECEIPTS / "orchestration.lock").open("a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        for old in RECEIPTS.iterdir():
            if old.is_dir() and (old / "prior-services.json").exists() and not (old / "restored.json").exists():
                raise ValueError("unresolved prior service quiescence receipt: " + str(old))
        directory = RECEIPTS / (str(time.time_ns()) + "-" + uuid.uuid4().hex)
        directory.mkdir(mode=0o770)
        sync_directory(RECEIPTS)
        boundary = Quiescence(directory)
        try:
            boundary.enter()
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
