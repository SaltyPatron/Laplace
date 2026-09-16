#!/usr/bin/env python3
"""Play one complete game through the installed CuteChess GUI on owned X11/DBus.

The existing direct-engine acceptance must already pass for these exact installed
bytes. AT-SPI reads and operates the real named Qt controls; neither a CLI tournament
nor independently launched engines can satisfy this observer. PGN validation uses
the existing native grammar/legal replay through ChessCatalogSurfaces.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import math
import os
from pathlib import Path
import pwd
import re
import shutil
import signal
import subprocess
import sys
import time

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
SCHEMA = "laplace.cutechess-gui-game/v1"
WHITE, BLACK, CLOCK = "Stockfish (official)", "Laplace (substrate)", "60+1"
MAX_TEXT = 64 * 1024 * 1024


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


engine = module("gui_game_engine_owner", ROOT / "scripts/check-cutechess-user-engines.py")
x11 = module("gui_game_x11_owner", ROOT / "scripts/check-cutechess-gui-session.py")
require, digest, save = engine.require, engine.digest, x11.save


def prior_acceptance(path, prefix, uid):
    value = engine.read_json(path)
    require(value.get("schema") == "laplace.cutechess-user-engine-acceptance/v1"
            and value.get("status") == "passed" and value.get("uid") == uid,
            "same operator must first pass the installed direct-engine acceptance")
    require([row["name"] for row in value["engines"]] == [WHITE, BLACK],
            "direct acceptance did not qualify the selected pair")
    selected = value["selected_files"]
    require(0 < len(selected) <= 512 and str(prefix / "share/laplace/cutechess-desktop.json") in selected,
            "direct acceptance has no bounded installed selection")
    for path, expected in selected.items():
        require(Path(path).is_absolute() and re.fullmatch(r"[0-9a-f]{64}", expected)
                and digest(path) == expected, "installed selection changed since direct acceptance")
    return value


def settings(pgn):
    # The selected upstream TimeControl::readSettings owns these public QSettings
    # keys. The GUI button and emitted PGN independently read back the clock.
    require(pgn.is_absolute() and not any(c in str(pgn) for c in '\\\n\r"@,;'),
            "PGN path cannot be represented in the isolated Qt INI settings")
    rows = ["[games]", "variant=standard", "default_pgn_output_file=" + str(pgn),
            "pondering=false", "use_tb=false",
            r"game_length\max_moves=0", r"draw_adjudication\move_number=0",
            r"draw_adjudication\move_count=0", r"resign_adjudication\move_count=0"]
    values = {"moves_per_tc": 0, "time_per_tc": 60000, "time_per_move": 0,
              "increment": 1000, "ply_limit": 0, "node_limit": 0,
              "expiry_margin": 0, "infinite": "false", "hourglass": "false"}
    for prefix in ("time_control", r"second_time_control\time_control"):
        rows.extend(prefix + "\\" + key + "=" + str(value) for key, value in values.items())
    return "\n".join(rows) + "\n"


class Accessibility:
    """Small bounded adapter over the maintained, typed libatspi GI API."""
    def __init__(self, api, pid, session):
        self.api, self.pid, self.session = api, pid, session

    def walk(self, root):
        pending, count = [(root, 0)], 0
        while pending:
            self.session.remaining()
            item, depth = pending.pop()
            require(depth <= 32 and count < 2048, "accessible widget tree exceeds its envelope")
            count += 1
            require(item is not None, "accessible child disappeared")
            item.clear_cache_single()
            yield item
            children = item.get_child_count()
            require(0 <= children <= 512, "accessible child count exceeds its envelope")
            pending.extend((item.get_child_at_index(i), depth + 1)
                           for i in reversed(range(children)))

    def application(self):
        desktop = self.api.get_desktop(0)
        count = desktop.get_child_count()
        require(0 <= count <= 32, "isolated accessibility bus has too many applications")
        values = [desktop.get_child_at_index(i) for i in range(count)]
        matches = [item for item in values if item.get_process_id() == self.pid]
        require(len(matches) <= 1, "multiple accessibility applications claim the GUI PID")
        return matches[0] if matches else None

    def one(self, root, *, role=None, name=None):
        matches = [item for item in self.walk(root)
                   if (role is None or item.get_role() == role)
                   and (name is None or item.get_name().replace("&", "") == name)]
        require(len(matches) == 1, "named GUI control is absent or ambiguous: " + str(name))
        return matches[0]

    def act(self, item, names=("press", "click", "toggle")):
        require(item.get_state_set().contains(self.api.StateType.ENABLED),
                "named GUI control is disabled")
        action = item.get_action_iface()
        require(action is not None, "named GUI control has no accessible action")
        matches = [i for i in range(action.get_n_actions())
                   if action.get_action_name(i) in names]
        require(len(matches) == 1 and action.do_action(matches[0]),
                "named GUI action was not acknowledged")

    def checked(self, item):
        item.clear_cache_single()
        return item.get_state_set().contains(self.api.StateType.CHECKED)

    def name(self, item):
        item.clear_cache_single()
        return item.get_name()

    def choose_engine(self, dialog, side, name):
        group = self.one(dialog, name=side, role=self.api.Role.PANEL)
        radio = self.one(group, name="CPU", role=self.api.Role.RADIO_BUTTON)
        self.act(radio)
        self.session.wait_for(lambda: self.checked(radio),
                              "CPU radio was not selected")
        combo = self.one(group, role=self.api.Role.COMBO_BOX)
        # Qt's actual combo popup receives keyboard selection. Read its real item
        # order first; do not assume sorted indices or replace its configuration.
        self.act(combo, ("showMenu",))
        def menu():
            lists = [item for item in self.walk(combo)
                     if item.get_role() == self.api.Role.LIST
                     and item.get_state_set().contains(self.api.StateType.SHOWING)]
            require(len(lists) <= 1, "engine combo exposes ambiguous lists")
            return lists[0] if lists else None
        popup = self.session.wait_for(menu, "engine combo popup did not appear")
        names = [popup.get_child_at_index(i).get_name() for i in range(popup.get_child_count())]
        require(len(names) == 2 and set(names) == {WHITE, BLACK},
                "GUI engine popup differs from the installed pair")
        self.session.key("Home")
        for _ in range(names.index(name)):
            self.session.key("Down")
        self.session.key("Return")
        self.session.wait_for(lambda: self.name(combo) == name,
                              "GUI engine selection readback differs")
        return {"side": side, "cpu_checked": True, "engine": combo.get_name(), "popup": names}

    def show_debug(self, app):
        view = self.one(app, name="View", role=self.api.Role.MENU_ITEM)
        self.act(view, ("showMenu",))
        item = self.one(app, name="Engine Debug", role=self.api.Role.MENU_ITEM)
        if not item.get_state_set().contains(self.api.StateType.CHECKED):
            self.act(item)
        else:
            self.session.key("Escape")
        dock = self.one(app, name="Engine Debug", role=self.api.Role.PANEL)
        texts = [item for item in self.walk(dock) if item.is_text()
                 and item.get_role() in (self.api.Role.TEXT, self.api.Role.ENTRY)]
        require(len(texts) == 1, "Engine Debug does not expose one actual text document")
        return texts[0]

    def text(self, item):
        text = item.get_text_iface()
        count = text.get_character_count()
        require(0 <= count <= MAX_TEXT, "GUI engine log exceeds its character envelope")
        value = text.get_text(0, count)
        require(len(value.encode("utf-8")) <= MAX_TEXT, "GUI engine log exceeds its byte envelope")
        return value


class GuiOutput:
    def __init__(self, process, target):
        self.process, self.target, self.bytes = process, target, 0
        os.set_blocking(process.stdout.fileno(), False)

    def drain(self):
        while True:
            try:
                data = os.read(self.process.stdout.fileno(), 65536)
            except BlockingIOError:
                return
            if not data:
                return
            self.bytes += len(data)
            require(self.bytes <= MAX_TEXT, "Qt diagnostics exceeded their byte envelope")
            self.target.write(data)
            self.target.flush()


class GuiSession(x11.X11Session):
    def __init__(self, process, binary, environment, deadline, output):
        self.output = output
        super().__init__(process, binary, environment, deadline)

    def remaining(self):
        self.output.drain()
        return super().remaining()


class ObservedProcess:
    def __init__(self, pid):
        self.pid = pid
    def poll(self):
        try:
            fields = Path(f"/proc/{self.pid}/stat").read_text().rsplit(")", 1)[1].split()
            return 0 if fields[0] == "Z" else None
        except FileNotFoundError:
            return 0


def children(pid):
    paths = list(Path(f"/proc/{pid}/task").glob("*/children"))
    require(len(paths) <= 256, "GUI thread inventory exceeds its envelope")
    result = set()
    for path in paths:
        try:
            text = path.read_text()
        except FileNotFoundError:
            continue
        require(len(text) <= 16384, "GUI child inventory exceeds its envelope")
        result.update(int(part) for part in text.split())
    require(len(result) <= 64, "GUI child count exceeds its envelope")
    return result


def observe_engines(gui, selected, found):
    for pid in children(gui.pid):
        try:
            actual = Path(f"/proc/{pid}/exe").resolve(strict=True)
            status = Path(f"/proc/{pid}/status").read_text()
        except FileNotFoundError:
            continue
        parent = re.search(r"^PPid:\s+(\d+)$", status, re.M)
        require(parent is not None and int(parent[1]) == gui.pid,
                "enumerated engine is not a direct GUI child")
        for name, expected in selected.items():
            if actual != expected:
                continue
            process = ObservedProcess(pid)
            identity = engine.process_identity(process, expected)
            if name in found:
                require(found[name]["process"] == identity, "GUI replaced a selected engine process")
                continue
            row = {"process": identity, "parent_pid": gui.pid}
            if name == BLACK:
                # The engine may have exec'd before loading Core; retry next poll.
                try:
                    row["native_closure"] = engine.mapped_closure(process, expected.parent)
                except ValueError as error:
                    if str(error) == "searched engine has no selected native core mapping":
                        continue
                    raise
            found[name] = row


def verify_protocol(log, pgn):
    """Compare accepted native-replayed moves to the GUI's real engine traffic."""
    evidence = pgn["evidence"]
    require(evidence["Schema"] == "laplace.cutechess-gui-pgn/v1"
            and evidence["Status"] == "passed" and evidence["CompleteSourceVerified"]
            and not evidence["DatabaseRecordingProven"]
            and evidence["White"] == WHITE and evidence["Black"] == BLACK
            and evidence["TimeControl"] == CLOCK
            and evidence["Termination"] in ("", "time forfeit")
            and evidence["Result"] in ("1-0", "0-1", "1/2-1/2"),
            "native PGN verification does not describe the selected full game")
    rows = []
    providers = []
    prepared = False
    sent_clock = {WHITE: False, BLACK: False}
    for line in log.splitlines():
        match = re.fullmatch(r"([<>])(.+)\(([0-9]+)\): (.*)", line)
        if not match:
            continue
        direction, name, _identifier, body = match.groups()
        if name not in sent_clock:
            continue
        if direction == ">" and body.startswith("go "):
            require(not re.search(r"(?:^| )(?:depth|nodes|movetime|infinite)(?: |$)", body)
                    and re.search(r"(?:^| )wtime \d+(?: |$)", body)
                    and re.search(r"(?:^| )btime \d+(?: |$)", body),
                    "GUI search did not use the uncapped game clock")
            sent_clock[name] = True
        if direction == "<" and body.startswith("bestmove "):
            parts = body.split()
            require(len(parts) in (2, 4), "GUI engine bestmove is malformed")
            rows.append((name, parts[1]))
        if direction == "<" and name == BLACK:
            if body.startswith("info string substrate provider stack prepared ("):
                prepared = True
            if body.startswith("info string providers ") and not body.startswith("info string providers depth "):
                providers.append(engine.provider_receipt([body]))
    accepted = [(WHITE if i % 2 == 0 else BLACK, move)
                for i, move in enumerate(evidence["MovesUci"])]
    require(len(accepted) == evidence["Plies"] and len(accepted) >= 2
            and rows[:len(accepted)] == accepted and all(sent_clock.values()),
            "GUI engine traffic does not reproduce every accepted PGN move")
    extra = rows[len(accepted):]
    require(not extra or (evidence["Termination"] == "time forfeit"
                          and len(extra) == 1 and extra[0][0] == (WHITE if len(accepted) % 2 == 0 else BLACK)),
            "GUI emitted unexplained extra engine moves")
    require(providers, "GUI Laplace process emitted no actual substrate search receipt")
    return {"accepted_engine_moves": len(accepted), "unaccepted_after_clock": extra,
            "substrate_search_receipts": providers, "prepared_message_observed": prepared,
            "scope": "GUI child protocol and complete native PGN replay; no durable recording claim"}


def catalog_closure(catalog, laplace_native):
    require(catalog.is_file() and os.access(catalog, os.X_OK), "native PGN verifier executable is missing")
    result = {str(catalog): digest(catalog)}
    for name in ("Laplace.Chess.dll", "Laplace.Core.dll", "Laplace.Engine.Core.dll",
                 "liblaplace_core.so", "liblaplace_dynamics.so",
                 "liblaplace_synthesis.so", "liblaplace_syzygy.so"):
        actual, expected = catalog.parent / name, laplace_native.parent / name
        require(actual.is_file() and expected.is_file() and digest(actual) == digest(expected),
                "PGN verifier closure differs from the installed game engine")
        result[str(actual)] = digest(actual)
    for path in catalog.parent.glob("ChessCatalogSurfaces.*"):
        if path.is_file():
            result[str(path)] = digest(path)
    return result


def worker(args):
    output, deadline = args.output_dir, time.monotonic() + args.timeout_seconds
    receipt = output / "game.json"
    result = {"schema": SCHEMA, "status": "failed", "scope": "actual-GUI-game-on-owned-virtual-X11",
              "operator_desktop_tested": False, "database_recording_proven": False,
              "game_completed": False, "checks": [], "engine_processes": {}}
    gui = None
    def checkpoint(name, value):
        result["checks"].append({"name": name, "evidence": value})
        save(receipt, result)
    try:
        prior = prior_acceptance(args.engine_acceptance_receipt, args.prefix, os.geteuid())
        desktop = engine.read_json(args.prefix / "share/laplace/cutechess-desktop.json")
        files = dict(prior["selected_files"])
        helper = engine.load_module("gui_game_selected_session", desktop["session_helper"]["path"])
        require(os.environ.get("DBUS_SESSION_BUS_ADDRESS") and os.environ.get("DISPLAY")
                and os.environ.get("XAUTHORITY"), "owned X11 and DBus sessions are missing")
        import gi
        gi.require_version("Atspi", "2.0")
        from gi.repository import Atspi
        Atspi.set_timeout(1000, 3000)
        require(Atspi.init() == 0, "AT-SPI could not initialize on the isolated bus")
        pgn = output / "game.pgn"
        config = Path(os.environ["XDG_CONFIG_HOME"]) / "cutechess"
        config.mkdir(mode=0o700)
        ini = config / "cutechess.conf"
        ini.write_text(settings(pgn), encoding="utf-8")
        result["initial_settings_sha256"] = digest(ini)
        gui_binary = Path(desktop["binary"]).resolve(strict=True)
        environment = dict(os.environ)
        environment.update(QT_LINUX_ACCESSIBILITY_ALWAYS_ON="1", QT_DEBUG_PLUGINS="1",
                           QT_QPA_PLATFORM="xcb", QT_STYLE_OVERRIDE="Fusion", LC_ALL="C", LANG="C")
        with (output / "gui.log").open("xb") as log:
            gui = subprocess.Popen([desktop["launcher"]["path"], "-platform", "xcb"],
                                   env=environment, cwd=output, stdin=subprocess.DEVNULL,
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, bufsize=0)
            gui_output = GuiOutput(gui, log)
            # The public Python launcher verifies/configures then execs Qt in place.
            while True:
                gui_output.drain()
                require(gui.poll() is None and time.monotonic() < deadline, "public GUI launcher failed")
                try:
                    if Path(f"/proc/{gui.pid}/exe").resolve(strict=True) == gui_binary:
                        break
                except FileNotFoundError:
                    pass
                time.sleep(.05)
            session = GuiSession(gui, gui_binary, environment, deadline, gui_output)
            main = session.select_main()
            checkpoint("mapped_public_gui", {"process": session.identity, "window": main})
            config_receipt = engine.read_json(config / "laplace-engines.json")
            entries = engine.read_json(config / "engines.json")
            private = {"TMPDIR": entries[0]["workingDirectory"],
                       "TMP": entries[0]["workingDirectory"], "TEMP": entries[0]["workingDirectory"],
                       "LAPLACE_OPS_LOG_DIR": str(Path(entries[0]["workingDirectory"]) / "logs")}
            engine.configured_engines(entries, engine.read_json(desktop["engine_catalog"]["path"]),
                                      Path(private["TMPDIR"]))
            require(config_receipt["config_sha256"] == digest(config / "engines.json"),
                    "public launcher did not publish the exact GUI engine configuration")
            selected_env = helper.launch_environment(desktop["launch"], private)
            laplace_wrapper = Path(entries[1]["command"]).resolve(strict=True)
            native = Path(str(laplace_wrapper) + ".native")
            selected = {WHITE: Path(desktop["stockfish"]["binary"]).resolve(strict=True),
                        BLACK: native.resolve(strict=True)}
            files.update(catalog_closure(args.catalog, native))
            a11y = Accessibility(Atspi, gui.pid, session)
            app = session.wait_for(a11y.application, "selected GUI has no AT-SPI application")
            before = set(session.windows())
            session.focus(main["id"])
            session.key("ctrl+n")
            def new_window():
                found = [session.describe(w) for w in session.windows() if w not in before]
                found = [w for w in found if w["title"] == "New Game" and w["transient_for"] == main["id"]]
                require(len(found) <= 1, "GUI opened ambiguous New Game dialogs")
                return found[0] if found else None
            dialog_window = session.wait_for(new_window, "GUI did not open New Game")
            dialog = a11y.one(app, name="New Game", role=Atspi.Role.DIALOG)
            choices = [a11y.choose_engine(dialog, "White", WHITE),
                       a11y.choose_engine(dialog, "Black", BLACK)]
            a11y.one(dialog, name="1 min, 1 sec increment", role=Atspi.Role.PUSH_BUTTON)
            checkpoint("named_new_game_choices", {"window": dialog_window, "choices": choices,
                                                 "time_control": CLOCK, "move_limit": None})
            a11y.act(a11y.one(dialog, name="OK", role=Atspi.Role.PUSH_BUTTON))
            session.wait_for(lambda: dialog_window["id"] not in session.windows(), "GUI did not accept New Game")
            began = time.monotonic()
            result["game_start_offset_seconds"] = args.timeout_seconds - session.remaining()
            while True:
                session.remaining()
                observe_engines(gui, selected, result["engine_processes"])
                require((output / "gui.log").stat().st_size <= MAX_TEXT,
                        "Qt diagnostics exceeded their byte envelope")
                windows = [session.describe(w) for w in session.windows()]
                complete = [w for w in windows if w["transient_for"] is None
                            and re.search(r"\((1-0|0-1|1/2-1/2)\)", w["title"])
                            and WHITE in w["title"] and BLACK in w["title"]]
                if complete and pgn.is_file() and pgn.stat().st_size:
                    require(len(complete) == 1, "multiple completed game windows are visible")
                    result["completed_window"] = complete[0]
                    break
                time.sleep(min(.1, session.remaining()))
            result["game_elapsed_seconds"] = time.monotonic() - began
            require(set(result["engine_processes"]) == {WHITE, BLACK},
                    "actual selected GUI child engine identities were not observed")
            debug = a11y.text(a11y.show_debug(app))
            (output / "engine-debug.log").write_text(debug, encoding="utf-8")
            checkpoint("gui_game_finished", {"window": result["completed_window"],
                                             "engine_debug_sha256": digest(output / "engine-debug.log")})
            session.focus(main["id"])
            session.key("ctrl+q")
            require(gui.wait(timeout=min(10, max(.001, deadline - time.monotonic()))) == 0,
                    "GUI did not exit normally after its complete game")
            gui_output.drain()
        qt_plugin = Path(desktop["qt_prefix"]) / "plugins/platforms/libqxcb.so"
        loaded = []
        for line in (output / "gui.log").read_text(encoding="utf-8", errors="replace").splitlines():
            match = re.fullmatch(r'qt\.core\.library: ("(?:[^"\\]|\\.)*") loaded library', line)
            if match and Path(json.loads(match[1])).name == "libqxcb.so":
                loaded.append(str(Path(json.loads(match[1])).resolve(strict=True)))
        require(set(loaded) == {str(qt_plugin.resolve(strict=True))},
                "GUI did not load its selected Qt X11 plugin")
        verified = output / "pgn-verification.json"
        engine.command([args.catalog, "verify-gui-game", pgn, WHITE, BLACK, CLOCK, verified],
                       selected_env, output, output / "pgn-verification.log", deadline)
        pgn_result = engine.read_json(verified)
        require(pgn_result["input"]["sha256"] == digest(pgn), "verified PGN identity changed")
        require("(" + pgn_result["evidence"]["Result"] + ")" in result["completed_window"]["title"],
                "GUI window result disagrees with its PGN")
        result["protocol"] = verify_protocol(debug, pgn_result)
        result["pgn"] = pgn_result
        require(all(digest(path) == expected for path, expected in files.items()),
                "selected installed/verification bytes changed during GUI game")
        require(Path(entries[1]["command"]).resolve(strict=True) == laplace_wrapper
                and (Path(desktop["launch"]["chess_floor_root"]) / "current").resolve(strict=True)
                    == Path(selected_env["LAPLACE_CHESS_PERFCACHE_BIN"]).parent,
                "installed UCI or floor generation changed during GUI game")
        result.update(status="passed", game_completed=True, normal_gui_exit=True,
                      selected_files=files, direct_acceptance_sha256=digest(args.engine_acceptance_receipt))
    except BaseException as error:
        result["failure_type"] = type(error).__name__
        result["failure"] = str(error) if isinstance(error, (ValueError, TimeoutError)) else "GUI game failed; inspect retained private evidence"
    finally:
        if gui is not None and gui.poll() is None:
            gui.terminate()
            try:
                gui.wait(timeout=2)
            except subprocess.TimeoutExpired:
                gui.kill()
                gui.wait(timeout=2)
            result["cleanup_termination_required"] = True
        if gui is not None and gui.stdout is not None:
            gui.stdout.close()
        save(receipt, result)
    return 0 if result["status"] == "passed" else 1


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prefix", type=Path, default=Path("/opt/laplace"))
    parser.add_argument("--expected-user", required=True)
    parser.add_argument("--engine-acceptance-receipt", type=Path, required=True)
    parser.add_argument("--catalog", type=Path, required=True)
    parser.add_argument("--x11-runtime-receipt", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--timeout-seconds", type=float, default=900)
    parser.add_argument("--worker", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args(argv)
    require(sys.platform.startswith("linux") and math.isfinite(args.timeout_seconds)
            and 30 <= args.timeout_seconds <= 3600, "GUI game requires a finite Linux execution window")
    require(os.getuid() == os.geteuid() == pwd.getpwnam(args.expected_user).pw_uid
            and os.geteuid() != 0, "run as the intended non-root mapped operator")
    engine.default_database_route(os.environ)
    for value in (args.prefix, args.catalog, args.engine_acceptance_receipt,
                  args.x11_runtime_receipt, args.output_dir):
        require(value.is_absolute(), "GUI game paths must be absolute")
    if args.worker:
        return worker(args)
    require(not args.output_dir.exists() and args.output_dir.parent.is_dir()
            and args.output_dir.parent.resolve().is_relative_to(Path("/build/laplace")),
            "use a fresh permanent GUI game evidence directory")
    args.output_dir.mkdir(mode=0o700)
    output = args.output_dir.resolve()
    started = time.monotonic()
    result = {"schema": SCHEMA, "status": "failed", "game_completed": False,
              "operator_desktop_tested": False, "database_recording_proven": False}
    try:
        with engine.wall_limit(args.timeout_seconds):
            deadline = started + args.timeout_seconds
            prior_acceptance(args.engine_acceptance_receipt, args.prefix, os.geteuid())
            environment, runtime = x11.bind_x11(args.x11_runtime_receipt, dict(os.environ), deadline)
            for name in ("DISPLAY", "XAUTHORITY", "WAYLAND_DISPLAY", "DBUS_SESSION_BUS_ADDRESS", "AT_SPI_BUS_ADDRESS"):
                environment.pop(name, None)
            # Keep the standard system D-Bus service directory for org.a11y.Bus
            # activation, while isolating all writable operator configuration.
            for name, leaf in (("XDG_CONFIG_HOME", "config"), ("XDG_CACHE_HOME", "cache"),
                               ("XDG_DATA_HOME", "data"), ("XDG_RUNTIME_DIR", "runtime"),
                               ("TMPDIR", "work")):
                directory = output / leaf
                directory.mkdir(mode=0o700)
                environment[name] = str(directory)
            environment.update(TMP=environment["TMPDIR"], TEMP=environment["TMPDIR"],
                               PYTHONDONTWRITEBYTECODE="1", XDG_CONFIG_DIRS=str(output / "empty-config"),
                               XDG_DATA_DIRS="/usr/local/share:/usr/share")
            dbus = shutil.which("dbus-run-session", path=environment.get("PATH"))
            require(dbus is not None, "install the optional GUI acceptance accessibility tools")
            arguments = [runtime["tools"]["xvfb-run"], "--auto-servernum",
                         "--error-file=" + str(output / "xvfb.log"),
                         "--server-args=-screen 0 1280x1024x24 -nolisten tcp -noreset",
                         dbus, "--", "/usr/bin/python3", Path(__file__).resolve(), "--worker",
                         "--prefix", args.prefix, "--expected-user", args.expected_user,
                         "--engine-acceptance-receipt", args.engine_acceptance_receipt,
                         "--catalog", args.catalog, "--x11-runtime-receipt", args.x11_runtime_receipt,
                         "--output-dir", output, "--timeout-seconds", str(max(1, deadline - time.monotonic()))]
            engine.command(arguments, environment, output, output / "session.log", deadline)
            result = engine.read_json(output / "game.json")
            require(result.get("status") == "passed" and result.get("game_completed"),
                    "GUI game worker did not complete acceptance")
            _, after = x11.bind_x11(args.x11_runtime_receipt, environment, deadline)
            require(after == runtime, "selected X11 runtime changed during GUI game")
            result["x11_runtime"] = runtime
            result["accessibility_python"] = {"path": "/usr/bin/python3", "sha256": digest("/usr/bin/python3")}
            result["dbus_runner"] = {"path": str(Path(dbus).resolve()), "sha256": digest(dbus)}
            result["operator"] = args.expected_user
            result["uid"] = os.geteuid()
    except BaseException as error:
        if (output / "game.json").is_file():
            result = engine.read_json(output / "game.json")
        result.update(status="failed", game_completed=False, failure_type=type(error).__name__)
        result["failure"] = str(error) if isinstance(error, (ValueError, TimeoutError)) else "GUI game did not complete; inspect private retained logs"
    finally:
        result["elapsed_seconds_including_cleanup"] = time.monotonic() - started
        result["deadline_seconds"] = args.timeout_seconds
        result["cleanup_allowance_seconds"] = 5
        save(output / "receipt.json", result)
    print(json.dumps({"status": result["status"], "receipt": str(output / "receipt.json"),
                      "game_completed": result["game_completed"]}))
    return 0 if result["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
