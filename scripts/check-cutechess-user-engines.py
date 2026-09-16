#!/usr/bin/env python3
"""Verify installed CuteChess engine configuration and real UCI substrate access.

This opt-in Linux observer uses a fresh permanent XDG configuration as the named
operator. It does not play a game, modify the operator's usual GUI settings, or
measure corpus throughput. Its one starting-position search is a functionality
check, separate from interactive GUI and complete recorded-game acceptance.
"""
from __future__ import annotations

import argparse
import contextlib
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import pwd
import re
import selectors
import signal
import stat
import subprocess
import sys
import time

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
SCHEMA = "laplace.cutechess-user-engine-acceptance/v1"
MAX_OUTPUT = 1024 * 1024
START_MOVES = {f + "2" + f + rank for f in "abcdefgh" for rank in "34"} | {
    "b1a3", "b1c3", "g1f3", "g1h3"}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(path):
    value = hashlib.sha256()
    with Path(path).open("rb") as source:
        for block in iter(lambda: source.read(1 << 20), b""):
            value.update(block)
    return value.hexdigest()


def read_json(path):
    with Path(path).open("rb") as source:
        value = source.read(4 * 1024 * 1024 + 1)
    require(len(value) <= 4 * 1024 * 1024, "receipt exceeds its byte envelope")
    return json.loads(value)


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


@contextlib.contextmanager
def wall_limit(seconds):
    def expired(_signal, _frame):
        raise TimeoutError("engine acceptance exceeded its aggregate deadline")
    def interrupted(_signal, _frame):
        raise KeyboardInterrupt("engine acceptance interrupted")
    old_alarm, old_term = signal.getsignal(signal.SIGALRM), signal.getsignal(signal.SIGTERM)
    prior = signal.getitimer(signal.ITIMER_REAL)
    require(prior == (0.0, 0.0), "acceptance requires its own wall deadline")
    signal.signal(signal.SIGALRM, expired)
    signal.signal(signal.SIGTERM, interrupted)
    signal.setitimer(signal.ITIMER_REAL, seconds)
    try:
        yield
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0)
        signal.signal(signal.SIGALRM, old_alarm)
        signal.signal(signal.SIGTERM, old_term)


def stop_group(process):
    """Drain this invocation's group even if its original wrapper already exited."""
    for sig, wait in ((signal.SIGTERM, 0.5), (signal.SIGKILL, 2)):
        try:
            os.killpg(process.pid, sig)
        except ProcessLookupError:
            break
        try:
            process.wait(timeout=wait)
        except subprocess.TimeoutExpired:
            pass
    process.wait(timeout=2)


class Process:
    """Bounded separate stdout/stderr, no background reader or unbounded communicate."""
    def __init__(self, argv, environment, cwd, log, deadline, maximum=MAX_OUTPUT):
        self.deadline, self.maximum = deadline, maximum
        self.bytes = 0
        self.buffers = {"stdout": bytearray(), "stderr": bytearray()}
        self.stdout_lines = []
        self.log = Path(log).open("xb")
        os.fchmod(self.log.fileno(), 0o600)
        self.selector = selectors.DefaultSelector()
        self.process = None
        try:
            self.process = subprocess.Popen(
                [str(item) for item in argv], cwd=cwd, env=environment,
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                start_new_session=True, bufsize=0)
            for name in ("stdout", "stderr"):
                stream = getattr(self.process, name)
                os.set_blocking(stream.fileno(), False)
                self.selector.register(stream, selectors.EVENT_READ, name)
        except BaseException:
            self.close()
            raise

    def remaining(self):
        remaining = self.deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("engine command exceeded its absolute deadline")
        return remaining

    def send(self, value):
        self.remaining()
        # These fixed protocol commands are far below PIPE_BUF; each phase waits
        # for its acknowledgement before issuing more input.
        self.process.stdin.write((value + "\n").encode("ascii"))

    def pump(self):
        events = self.selector.select(min(self.remaining(), 0.25))
        for key, _ in events:
            data = os.read(key.fileobj.fileno(), 8192)
            if not data:
                self.selector.unregister(key.fileobj)
                remainder = self.buffers[key.data]
                if remainder:
                    self.line(key.data, bytes(remainder))
                    remainder.clear()
                continue
            self.bytes += len(data)
            require(self.bytes <= self.maximum, "engine output exceeded its byte envelope")
            buffer = self.buffers[key.data]
            buffer.extend(data)
            require(len(buffer) <= 65536, "engine line exceeded its byte envelope")
            while b"\n" in buffer:
                line, _, tail = buffer.partition(b"\n")
                buffer[:] = tail
                self.line(key.data, bytes(line))
        if not events and self.process.poll() is not None and not self.selector.get_map():
            return False
        return bool(self.selector.get_map())

    def line(self, channel, value):
        decoded = value.decode("utf-8", errors="strict").rstrip("\r")
        self.log.write(channel.encode("ascii") + b": " + value + b"\n")
        self.log.flush()
        if channel == "stdout":
            require(len(self.stdout_lines) < 8192, "engine line count exceeded its envelope")
            self.stdout_lines.append(decoded)

    def until(self, predicate):
        offset = len(self.stdout_lines)
        while True:
            for line in self.stdout_lines[offset:]:
                if predicate(line):
                    return line
            offset = len(self.stdout_lines)
            if not self.pump():
                for line in self.stdout_lines[offset:]:
                    if predicate(line):
                        return line
                raise ValueError("engine exited before its protocol acknowledgement")

    def finish(self):
        while self.selector.get_map():
            self.pump()
        require(self.process.wait(timeout=self.remaining()) == 0,
                "engine command exited unsuccessfully")
        return list(self.stdout_lines)

    def close(self):
        # A deadline or cancellation must not interrupt draining our owned group.
        blocked = signal.pthread_sigmask(signal.SIG_BLOCK, {signal.SIGALRM, signal.SIGTERM})
        try:
            if self.process is not None:
                stop_group(self.process)
        finally:
            self.selector.close()
            if self.process is not None:
                for stream in (self.process.stdin, self.process.stdout, self.process.stderr):
                    stream.close()
            self.log.close()
            signal.pthread_sigmask(signal.SIG_SETMASK, blocked)

    def __enter__(self):
        return self

    def __exit__(self, *_error):
        self.close()


def command(argv, environment, cwd, log, deadline):
    with Process(argv, environment, cwd, log, deadline) as running:
        running.process.stdin.close()
        return running.finish()


def process_identity(process, expected):
    require(process.poll() is None, "selected engine exited before identity observation")
    root = Path("/proc") / str(process.pid)
    actual = (root / "exe").resolve(strict=True)
    require(actual == Path(expected).resolve(strict=True),
            "running engine executable differs from selected installed executable")
    status = (root / "status").read_text()
    uid = re.search(r"^Uid:\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)$", status, re.M)
    require(uid is not None and all(int(item) == os.geteuid() for item in uid.groups()),
            "running engine does not belong to the invoking operator")
    fields = (root / "stat").read_text().rsplit(")", 1)[1].split()
    return {"pid": process.pid, "start_ticks": int(fields[19]), "uid": os.geteuid(),
            "executable": str(actual), "sha256": digest(actual)}


def mapped_closure(process, directory):
    rows = (Path("/proc") / str(process.pid) / "maps").read_text().splitlines()
    result = {}
    for name in ("core", "dynamics", "synthesis", "syzygy"):
        filename = "liblaplace_" + name + ".so"
        selected = (directory / filename).resolve(strict=True)
        info = selected.stat()
        matches = []
        for line in rows:
            fields = line.split(maxsplit=5)
            if len(fields) == 6 and Path(fields[5].removesuffix(" (deleted)")).name == filename:
                require(fields[5] == str(selected) and int(fields[4]) == info.st_ino
                        and tuple(int(part, 16) for part in fields[3].split(":"))
                        == (os.major(info.st_dev), os.minor(info.st_dev)),
                        "running engine mapped a different native closure")
                matches.append(fields[0])
        result[name] = {"path": str(selected), "sha256": digest(selected),
                        "mapped": bool(matches)}
    require(result["core"]["mapped"], "searched engine has no selected native core mapping")
    return result


def provider_receipt(lines):
    rows = [line.removeprefix("info string providers ") for line in lines
            if line.startswith("info string providers ")
            and not line.startswith("info string providers depth ")]
    require(len(rows) == 1, "Laplace must emit one final search provider receipt")
    value = rows[0]
    root = re.search(r"(?:^| )root=(\d+)/(\d+)(?: |$)", value)
    position = re.search(r"(?:^| )position=(\d+)/(\d+)\(atoms:(\d+)\)(?: |$)", value)
    require(root is not None and position is not None
            and int(root[1]) > 0 and int(position[1]) > 0
            and "root-work-scope=provider-instance-deltas" in value,
            "Laplace search did not exercise its selected substrate providers")
    return {"summary": value, "root_reads": int(root[1]), "position_reads": int(position[1]),
            "scope": "one fresh UCI process; provider-instance deltas, not recorded games"}


def uci(entry, environment, output, deadline, expected, substrate=False, depth=2):
    started = time.monotonic()
    with Process([entry["command"]], environment, entry["workingDirectory"],
                 output, deadline) as running:
        running.send("uci")
        running.until(lambda line: line == "uciok")
        identity = process_identity(running.process, expected)
        if substrate:
            require(any(line.startswith("option name Substrate type combo default substrate ")
                        for line in running.stdout_lines),
                    "Laplace did not select its substrate default")
        running.send("isready")
        running.until(lambda line: line == "readyok")
        initialized = time.monotonic()
        if substrate:
            require(any(line.startswith("info string substrate provider stack prepared (")
                        for line in running.stdout_lines),
                    "readyok did not establish a prepared substrate provider stack")
        offset = len(running.stdout_lines)
        running.send("position startpos")
        running.send("go depth " + str(depth))
        best = running.until(lambda line: line.startswith("bestmove "))
        searched = time.monotonic()
        move = best.split()[1]
        require(move in START_MOVES, "engine returned no legal starting-position move")
        search_lines = running.stdout_lines[offset:]
        require(any(re.match(r"^info depth " + str(depth) + r"(?: |$)", line)
                    for line in search_lines), "engine did not complete the requested depth")
        result = {"name": entry["name"], "command": entry["command"],
                  "working_directory": entry["workingDirectory"], "process": identity,
                  "bestmove": move, "completed_depth": depth,
                  "initialization_seconds": initialized - started,
                  "search_seconds": searched - initialized,
                  "cold_boot_proven": False, "complete_game_proven": False}
        if substrate:
            result["providers"] = provider_receipt(search_lines)
            result["native_closure"] = mapped_closure(running.process, Path(expected).parent)
        require(process_identity(running.process, expected) == identity,
                "engine process identity changed during the search")
        running.send("quit")
        running.finish()
        result["elapsed_seconds"] = time.monotonic() - started
        return result


def authenticate_desktop(prefix, receipt_path, gui_receipt):
    require(receipt_path == prefix / "share/laplace/cutechess-desktop.json",
            "desktop receipt must be the installed prefix receipt")
    value = read_json(receipt_path)
    require(value.get("schema") == "laplace.cutechess-desktop-install/v1",
            "unsupported installed desktop receipt")
    selected = value["launch"]
    bindings = {
        "launcher": prefix / "bin/laplace-cutechess",
        "engine_catalog": prefix / "share/laplace/cutechess-engines.json",
        "session_helper": prefix / "share/laplace/cutechess-user-engines.py",
        "stockfish_launcher": prefix / "bin/laplace-cutechess-stockfish",
        "desktop": prefix / "share/applications/laplace-cutechess.desktop"}
    files = {str(receipt_path): digest(receipt_path), str(gui_receipt): digest(gui_receipt)}
    require(files[str(gui_receipt)] == value["build_receipt_sha256"],
            "desktop selection does not bind the supplied verified GUI build")
    for role, path in bindings.items():
        require(value[role]["path"] == str(path) and not path.is_symlink()
                and digest(path) == value[role]["sha256"],
                "installed desktop selection content changed")
        files[str(path)] = value[role]["sha256"]
    require(selected["argv"] == [value["binary"]]
            and selected["binary_sha256"] == value["binary_sha256"]
            and selected["work_root"] == value["session_work_root"]
            and selected["t0_perfcache"] == value["t0_perfcache"]
            and selected["chess_floor_root"] == str(prefix / "share/laplace/chess-floor")
            and selected["qt_library_path"] == str(Path(value["qt_prefix"]) / "lib")
            and selected["environment"] == {
                "QT_PLUGIN_PATH": str(Path(value["qt_prefix"]) / "plugins"),
                "QT_QPA_PLATFORM_PLUGIN_PATH": str(Path(value["qt_prefix"]) / "plugins/platforms")},
            "desktop public launch fields disagree")
    for role in ("session_helper", "engine_catalog"):
        require(selected[role] == value[role]["path"]
                and selected[role + "_sha256"] == value[role]["sha256"],
                "desktop launcher dependency binding disagrees")
    for path, expected in ((value["binary"], value["binary_sha256"]),
                           (value["stockfish"]["binary"], value["stockfish"]["sha256"])):
        require(digest(path) == expected, "selected engine or GUI binary changed")
        files[path] = expected
    return value, files


def configured_engines(config, catalog, work):
    require(len(config) == 2 and len(catalog) == 2,
            "isolated CuteChess configuration must contain exactly the installed engine pair")
    for actual, selected in zip(config, catalog):
        require(actual == {**selected, "workingDirectory": str(work)},
                "isolated engine configuration differs from installed catalog")
    require([row["name"] for row in config] == ["Stockfish (official)", "Laplace (substrate)"],
            "installed engine names do not match the public catalog")
    return config


def enumeration(lines, entries, cwd):
    require(not (cwd / "engines.json").exists(),
            "CuteChess CLI cwd would shadow the GUI XDG engine configuration")
    require([line for line in lines if line.strip()] == [row["name"] for row in entries],
            "official EngineManager did not enumerate the exact installed engine pair")


def default_database_route(environment):
    # ChessEngineService -> LaplaceDataSource -> LaplaceInstall supplies the
    # explicit Unix host/user/database. Refuse conflicting libpq-style hints too;
    # this observer is not a credentialed alternative-connection acceptance.
    defaults = {"PGHOST": "/var/run/postgresql", "PGUSER": "laplace_admin",
                "PGPORT": "5432", "PGDATABASE": "laplace"}
    require(not environment.get("LAPLACE_DB", "").strip()
            and all(environment.get(key, expected) in ("", expected)
                    for key, expected in defaults.items())
            and not environment.get("PGPASSWORD") and not environment.get("PGPASSFILE"),
            "acceptance requires the public default local database route without credential overrides")
    return {"host": defaults["PGHOST"], "port": 5432, "database": defaults["PGDATABASE"],
            "role": defaults["PGUSER"], "source": "public UCI defaults; conflicting ambient hints refused"}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prefix", type=Path, default=Path("/opt/laplace"))
    parser.add_argument("--expected-user", required=True)
    parser.add_argument("--gui-build-receipt", type=Path, required=True)
    parser.add_argument("--cli-binary", type=Path, required=True)
    parser.add_argument("--cli-build-receipt", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--timeout-seconds", type=float, default=300)
    args = parser.parse_args(argv)
    require(sys.platform.startswith("linux"), "engine acceptance requires Linux")
    require(math.isfinite(args.timeout_seconds) and 10 <= args.timeout_seconds <= 900,
            "acceptance deadline must be finite and within 10..900 seconds")
    operator = pwd.getpwnam(args.expected_user)
    require(operator.pw_uid == os.getuid() == os.geteuid() and os.geteuid() != 0,
            "run acceptance as the intended non-root mapped operator")
    database_route = default_database_route(os.environ)
    for name in ("prefix", "gui_build_receipt", "cli_binary", "cli_build_receipt", "output_dir"):
        path = getattr(args, name)
        require(path.is_absolute(), "acceptance paths must be absolute")
    require(not args.output_dir.exists() and args.output_dir.parent.is_dir()
            and args.output_dir.parent.resolve().is_relative_to(Path("/build/laplace")),
            "output must be a fresh directory below permanent /build/laplace")
    args.output_dir.mkdir(mode=0o700)
    output = args.output_dir.resolve()
    result = {"schema": SCHEMA, "status": "failed", "uid": os.geteuid(),
              "operator": args.expected_user, "scope": "installed-configuration-and-direct-uci",
              "gui_engine_game_proven": False, "operator_desktop_tested": False,
              "recorded_games_proven": False,
              "database_route": database_route,
              "engines": []}
    started, lock = time.monotonic(), None
    old_environment = dict(os.environ)
    try:
        with wall_limit(args.timeout_seconds):
            deadline = started + args.timeout_seconds
            xdg, cwd, scratch = output / "xdg", output / "enumeration", output / "work"
            for path in (xdg, cwd, scratch):
                path.mkdir(mode=0o700)
            os.environ["XDG_CONFIG_HOME"] = str(xdg)
            os.environ["PYTHONDONTWRITEBYTECODE"] = "1"
            os.environ.update(TMPDIR=str(scratch), TMP=str(scratch), TEMP=str(scratch))
            desktop, files = authenticate_desktop(
                args.prefix, args.prefix / "share/laplace/cutechess-desktop.json",
                args.gui_build_receipt)
            result["desktop_receipt_sha256"] = files[str(args.prefix / "share/laplace/cutechess-desktop.json")]
            # Use the existing retained-source/Qt verifier, under the same bounded
            # owned subprocess protocol as the public launcher and EngineManager.
            command([sys.executable, ROOT / "scripts/provision-cutechess.py", "--gui",
                     "--binary", desktop["binary"], "--verify-receipt", args.gui_build_receipt,
                     "--work", output], dict(os.environ), cwd, output / "gui-verification.log", deadline)
            provision = load_module("selected_cutechess_provision", ROOT / "scripts/provision-cutechess.py")
            cli_environment = dict(os.environ)
            cli_environment.update(provision.gui_environment(Path(desktop["qt_prefix"])))
            cli = read_json(args.cli_build_receipt)
            release = read_json(ROOT / "deploy/cutechess-release.json")
            require(cli.get("schema") == "laplace.cutechess-source-build.v1"
                    and cli["commit"] == release["commit"] and cli["repository"] == release["repository"]
                    and digest(args.cli_binary) == cli["binary_sha256"],
                    "official CLI build receipt does not select this binary")
            command([sys.executable, ROOT / "scripts/provision-cutechess.py",
                     "--verify-source", cli["source"], "--binary", args.cli_binary,
                     "--qt-version", release["qt_version"]],
                    cli_environment, cwd, output / "cli-verification.log", deadline)
            files[str(args.cli_build_receipt)] = digest(args.cli_build_receipt)
            files[str(args.cli_binary)] = digest(args.cli_binary)
            launch_started = time.monotonic()
            command([desktop["launcher"]["path"], "-platform", "offscreen", "--version"],
                    dict(os.environ), cwd, output / "public-launcher.log", deadline)
            result["public_launcher_seconds"] = time.monotonic() - launch_started
            helper = load_module("installed_cutechess_session", desktop["session_helper"]["path"])
            lock, private, config_receipt = helper.prepare(
                desktop["engine_catalog"]["path"], Path(desktop["binary"]), desktop["session_work_root"])
            environment = helper.launch_environment(desktop["launch"], private)
            require(environment["LAPLACE_UCI_SUBSTRATE"] == "substrate",
                    "public launch environment did not select substrate")
            config_path = xdg / "cutechess/engines.json"
            work = Path(private["TMPDIR"])
            entries = configured_engines(read_json(config_path),
                                         read_json(desktop["engine_catalog"]["path"]), work)
            require(entries[0]["command"] == desktop["stockfish_launcher"]["path"]
                    and entries[1]["command"] == str(args.prefix / "app/laplace-uci"),
                    "configured commands differ from installed selected engines")
            require(config_receipt["config_sha256"] == digest(config_path),
                    "GUI config receipt differs from its actual file")
            require(not (cwd / "engines.json").exists(), "enumeration cwd shadows XDG config")
            lines = command([args.cli_binary, "--engines"], environment, cwd,
                            output / "engine-manager.log", deadline)
            enumeration(lines, entries, cwd)
            result["engine_manager"] = {"names": lines, "config": str(config_path),
                                       "sha256": digest(config_path)}
            laplace_wrapper = Path(entries[1]["command"]).resolve(strict=True)
            native = Path(str(laplace_wrapper) + ".native")
            require(native.is_file(), "installed Laplace UCI generation lease has no native executable")
            closure = [laplace_wrapper, native, *native.parent.glob("*.dll"),
                       *native.parent.glob("*.json"), *native.parent.glob("liblaplace_*.so")]
            generation = Path(environment["LAPLACE_CHESS_PERFCACHE_BIN"]).parent
            closure += [Path(environment["LAPLACE_PERFCACHE_BIN"]), generation / "receipt.json",
                        Path(environment["LAPLACE_CHESS_PERFCACHE_BIN"]),
                        Path(environment["LAPLACE_CHESS_TRANSITION_BIN"])]
            files.update({str(path): digest(path) for path in closure})
            result["selected_files"] = files
            result["floor_generation"] = str(generation)
            result["engines"].append(uci(entries[0], environment, output / "stockfish-uci.log",
                                         deadline, Path(desktop["stockfish"]["binary"])))
            result["engines"].append(uci(entries[1], environment, output / "laplace-uci.log",
                                         deadline, native, substrate=True))
            require(Path(entries[1]["command"]).resolve(strict=True) == laplace_wrapper
                    and (Path(desktop["launch"]["chess_floor_root"]) / "current").resolve(strict=True) == generation,
                    "installed application or floor generation changed during acceptance")
            require(all(digest(path) == expected for path, expected in files.items()),
                    "selected installed bytes changed during acceptance")
            require(digest(config_path) == result["engine_manager"]["sha256"],
                    "CuteChess configuration changed during acceptance")
            result["status"] = "passed"
    except BaseException as error:
        # Public protocol logs are bounded and private. Do not serialize inherited
        # environment, service secrets, arbitrary subprocess exception argv, or DB strings.
        result["failure_type"] = type(error).__name__
        result["failure"] = str(error) if isinstance(error, (ValueError, TimeoutError, KeyboardInterrupt)) else "installed engine acceptance failed; inspect private bounded phase logs"
    finally:
        if lock is not None:
            os.close(lock)
        os.environ.clear()
        os.environ.update(old_environment)
        result["elapsed_seconds"] = time.monotonic() - started
        pending = output / "receipt.json.pending"
        pending.write_text(json.dumps(result, indent=2, allow_nan=False) + "\n")
        pending.chmod(0o600)
        pending.replace(output / "receipt.json")
    print(json.dumps({"status": result["status"], "receipt": str(output / "receipt.json")}))
    return 0 if result["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
