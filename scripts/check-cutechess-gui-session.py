#!/usr/bin/env python3
"""Exercise the installed, unmodified Cute Chess GUI on an owned virtual X11 display.

This proves window creation, real Qt keyboard actions and normal application exit.
It does not establish a user's desktop session, engine play or visual board correctness.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time

SCHEMA = "laplace.cutechess-gui-session/v1"
ROOT = Path(__file__).resolve().parents[1]


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(path):
    value = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            value.update(block)
    return value.hexdigest()


def save(path, value):
    pending = path.with_name(path.name + ".pending")
    pending.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    pending.replace(path)


def stop_group(process):
    """Only the process group created by this invocation is eligible for cleanup."""
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        return
    try:
        process.wait(timeout=3)
    except subprocess.TimeoutExpired:
        pass
    # The wrapper can exit before a child. Reap every remaining member of this
    # owned group even when its initial process already acknowledged SIGTERM.
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    process.wait(timeout=2)


def process_identity(process, binary):
    require(process.poll() is None, "GUI exited before interaction completed")
    actual = Path(f"/proc/{process.pid}/exe").resolve(strict=True)
    require(actual == binary.resolve(strict=True), "GUI process executable differs from selected binary")
    stat = Path(f"/proc/{process.pid}/stat").read_text()
    fields = stat[stat.rfind(")") + 2:].split()
    require(len(fields) > 19, "GUI process start identity is unavailable")
    return {"pid": process.pid, "executable": str(actual),
            "sha256": digest(actual), "start_ticks": int(fields[19])}


class X11Session:
    def __init__(self, process, binary, environment, deadline):
        self.process, self.binary = process, binary
        self.environment, self.deadline = environment, deadline
        self.identity = process_identity(process, binary)

    def remaining(self):
        remaining = self.deadline - time.monotonic()
        require(remaining > 0, "virtual X11 interaction deadline exceeded")
        require(process_identity(self.process, self.binary) == self.identity,
                "GUI process identity changed during interaction")
        return remaining

    def command(self, *arguments, absent_ok=False):
        result = subprocess.run(arguments, env=self.environment, stdin=subprocess.DEVNULL,
                                capture_output=True, text=True, timeout=min(5, self.remaining()))
        require(len(result.stdout) <= 65536 and len(result.stderr) <= 65536,
                "X11 observation exceeded its output envelope")
        if absent_ok and result.returncode == 1:
            return None
        require(result.returncode == 0, "X11 command failed: " + arguments[0])
        return result.stdout

    def windows(self):
        output = self.command("xdotool", "search", "--all", "--onlyvisible",
                              "--pid", str(self.process.pid), "--name", ".*", absent_ok=True)
        if output is None:
            return []
        lines = output.splitlines()
        require(len(lines) <= 32 and all(re.fullmatch(r"[1-9][0-9]*", item) for item in lines),
                "invalid or excessive X11 window inventory")
        return list(dict.fromkeys(int(item) for item in lines))

    def describe(self, identity):
        window = str(identity)
        properties = self.command("xprop", "-id", window,
                                  "_NET_WM_PID", "WM_CLASS", "WM_TRANSIENT_FOR")
        info = self.command("xwininfo", "-id", window)
        title = self.command("xdotool", "getwindowname", window).rstrip("\n")
        pid = re.search(r"^_NET_WM_PID\(CARDINAL\) = ([0-9]+)$", properties, re.M)
        require(pid is not None and int(pid[1]) == self.process.pid, "X11 window has a different owner")
        require(re.search(r'^WM_CLASS\(STRING\) = .*"cutechess"', properties, re.M | re.I),
                "X11 window class does not identify Cute Chess")
        require("Map State: IsViewable" in info, "X11 window is not viewable")
        width = re.search(r"^\s*Width: ([0-9]+)$", info, re.M)
        height = re.search(r"^\s*Height: ([0-9]+)$", info, re.M)
        require(width is not None and height is not None
                and 0 < int(width[1]) <= 1280 and 0 < int(height[1]) <= 1024,
                "X11 window geometry is outside the owned display")
        transient = re.search(r"^WM_TRANSIENT_FOR\(WINDOW\): window id # (0x[0-9a-f]+)", properties, re.M | re.I)
        return {"id": identity, "pid": int(pid[1]), "title": title,
                "width": int(width[1]), "height": int(height[1]),
                "map_state": "IsViewable", "transient_for": int(transient[1], 16) if transient else None,
                "properties": properties, "geometry": info}

    def wait_for(self, predicate, message):
        while True:
            require(time.monotonic() < self.deadline, message)
            self.remaining()
            value = predicate()
            if value:
                return value
            time.sleep(min(.05, self.remaining()))

    def select_main(self):
        def find():
            candidates = [self.describe(window) for window in self.windows()]
            selected = [window for window in candidates
                        if window["transient_for"] is None and " vs " in window["title"]]
            require(len(selected) <= 1, "multiple unexpected main game windows")
            return selected[0] if selected else None
        return self.wait_for(find, "main game window was not mapped")

    def focus(self, identity):
        self.command("xdotool", "windowfocus", str(identity))
        self.wait_for(lambda: self.command("xdotool", "getwindowfocus", "-f").strip() == str(identity),
                      "selected window did not acquire keyboard focus")

    def key(self, value):
        # A separate invocation without --window uses XTEST on the actual focused window.
        self.command("xdotool", "key", "--clearmodifiers", value)

    def exercise(self, checkpoint):
        main = self.select_main()
        checkpoint("main_window_mapped", main)
        before = set(self.windows())
        self.focus(main["id"])
        self.key("ctrl+n")
        def opened():
            candidates = [self.describe(window) for window in self.windows() if window not in before]
            selected = [window for window in candidates if window["title"] == "New Game"
                        and window["transient_for"] == main["id"]]
            require(len(selected) <= 1, "multiple new-game dialogs were mapped")
            return selected[0] if selected else None
        dialog = self.wait_for(opened, "New Game dialog did not respond to Ctrl+N")
        checkpoint("new_game_dialog_mapped", dialog)
        self.focus(dialog["id"])
        self.key("Escape")
        self.wait_for(lambda: dialog["id"] not in self.windows(),
                      "New Game dialog did not close after Escape")
        require(main["id"] in self.windows(), "main game window disappeared with cancelled dialog")
        checkpoint("dialog_cancelled", {"id": dialog["id"], "main_id": main["id"]})
        self.focus(main["id"])
        self.key("ctrl+q")
        # Normal exit is the acknowledgement of the application's Quit action.
        try:
            returncode = self.process.wait(timeout=min(10, max(.001, self.deadline - time.monotonic())))
        except subprocess.TimeoutExpired:
            raise ValueError("GUI did not exit normally after Ctrl+Q") from None
        require(returncode == 0, "GUI exited unsuccessfully after Ctrl+Q")
        checkpoint("normal_quit", {"returncode": returncode})


def worker(args):
    report = {"schema": SCHEMA, "status": "failed", "scope": "virtual-x11-interactive",
              "operator_desktop_tested": False, "visual_board_correctness_verified": False,
              "engine_game_tested": False, "checks": []}
    started = time.monotonic()
    deadline = started + args.timeout_seconds
    receipt = args.output_dir / "receipt.json"
    gui = None
    def checkpoint(name, evidence):
        report["checks"].append({"name": name, "evidence": evidence})
        save(receipt, report)
    try:
        spec = importlib.util.spec_from_file_location("cutechess_source_owner",
                                                       ROOT / "scripts/provision-cutechess.py")
        owner = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(owner)
        lock = json.loads(args.lock.read_text(encoding="utf-8"))
        verified = owner.verify_gui_install(args.binary, args.receipt, lock, work=args.session_root)
        runtime = verified["runtime"]
        qt = Path(runtime["qt"]["prefix"]).resolve(strict=True)
        binary = Path(runtime["path"]).resolve(strict=True)
        require(binary == args.binary.resolve(strict=True), "verified GUI path differs from request")
        require(runtime["binary_sha256"] == digest(binary), "GUI bytes changed after source verification")
        plugin = qt / "plugins/platforms/libqxcb.so"
        require(plugin.is_file(), "selected Qt installation has no X11 xcb platform plugin")
        plugin_hash = digest(plugin)
        report.update(source={"repository": verified["repository"], "commit": verified["commit"],
                              "build_receipt": verified["build_receipt"],
                              "build_receipt_sha256": verified["build_receipt_sha256"]},
                      binary={"path": str(binary), "sha256": runtime["binary_sha256"]},
                      qt={"prefix": str(qt), "version": runtime["qt_version"],
                          "platform_plugin": str(plugin), "platform_plugin_sha256": plugin_hash})
        environment = dict(os.environ)
        environment.update(runtime["direct_launch"]["environment"])
        for name in ("QT_QPA_PLATFORMTHEME", "QT_STYLE_OVERRIDE", "QT_QPA_GENERIC_PLUGINS",
                     "WAYLAND_DISPLAY", "QT_QPA_PLATFORM", "QT_QPA_PLATFORM_PLUGIN_PATH"):
            environment.pop(name, None)
        environment.update({"QT_QPA_PLATFORM_PLUGIN_PATH": str(qt / "plugins/platforms"),
                            "QT_PLUGIN_PATH": str(qt / "plugins"),
                            "QT_DEBUG_PLUGINS": "1", "QT_MESSAGE_PATTERN": "%{category}: %{message}",
                            "QT_LOGGING_RULES": "qt.core.library.debug=true",
                            "LC_ALL": "C", "LANG": "C"})
        for name, leaf in (("XDG_CONFIG_HOME", "config"), ("XDG_CONFIG_DIRS", "config-dirs"),
                           ("XDG_DATA_HOME", "data"), ("XDG_DATA_DIRS", "data-dirs"),
                           ("XDG_CACHE_HOME", "cache"), ("XDG_RUNTIME_DIR", "runtime")):
            path = args.session_root / leaf
            path.mkdir(mode=0o700, exist_ok=True)
            environment[name] = str(path)
        require(bool(environment.get("DISPLAY")) and bool(environment.get("XAUTHORITY")),
                "owned virtual display environment was not established")
        log = args.output_dir / "gui.log"
        with log.open("w", encoding="utf-8") as stream:
            gui = subprocess.Popen([str(binary), "-platform", "xcb"], env=environment,
                                   stdin=subprocess.DEVNULL, stdout=stream, stderr=subprocess.STDOUT)
            session = X11Session(gui, binary, environment, deadline)
            report["process"] = session.identity
            checkpoint("source_and_process_bound", {"gui_sha256": session.identity["sha256"]})
            session.exercise(checkpoint)
        diagnostics = log.read_text(encoding="utf-8", errors="replace")
        loaded = []
        for line in diagnostics.splitlines():
            match = re.fullmatch(r'qt\.core\.library: ("(?:[^"\\]|\\.)*") loaded library', line)
            if match:
                path = Path(json.loads(match[1]))
                if path.name == "libqxcb.so":
                    loaded.append(str(path.resolve(strict=True)))
        require(set(loaded) == {str(plugin.resolve(strict=True))},
                "GUI did not load the selected Qt X11 platform plugin")
        require(digest(plugin) == plugin_hash and digest(binary) == runtime["binary_sha256"],
                "GUI executable or X11 plugin changed during interaction")
        checkpoint("selected_xcb_plugin_verified", {"path": str(plugin), "sha256": plugin_hash})
        report.update(status="passed", qapplication_event_loop_verified=True,
                      virtual_x11_interaction_verified=True, clean_exit_verified=True,
                      gui_log_sha256=digest(log))
    except (OSError, ValueError, RuntimeError, KeyError, TypeError, subprocess.SubprocessError, KeyboardInterrupt) as error:
        report["error"] = str(error) if isinstance(error, (ValueError, RuntimeError)) else type(error).__name__
    finally:
        if gui is not None and gui.poll() is None:
            gui.terminate()
            try:
                gui.wait(timeout=2)
            except subprocess.TimeoutExpired:
                gui.kill()
                gui.wait(timeout=2)
            report["cleanup_termination_required"] = True
        report["elapsed_seconds"] = time.monotonic() - started
        save(receipt, report)
    return 0 if report["status"] == "passed" else 1


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--binary", type=Path, required=True)
    parser.add_argument("--receipt", type=Path, required=True, help="Existing GUI source/build receipt")
    parser.add_argument("--lock", type=Path, default=ROOT / "deploy/cutechess-release.json")
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--work", type=Path, default=Path(os.environ.get("TMPDIR", "/build/laplace/work")))
    parser.add_argument("--timeout-seconds", type=float, default=60)
    parser.add_argument("--internal-worker", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("--session-root", type=Path, help=argparse.SUPPRESS)
    args = parser.parse_args(argv)
    require(sys.platform.startswith("linux"), "virtual X11 acceptance requires Linux")
    require(math.isfinite(args.timeout_seconds) and 10 <= args.timeout_seconds <= 120,
            "timeout must be finite and within 10..120 seconds")
    for name in ("binary", "receipt", "lock", "output_dir", "work"):
        setattr(args, name, getattr(args, name).absolute())
    if args.internal_worker:
        require(args.session_root is not None, "internal session directory is missing")
        return worker(args)
    args.output_dir.mkdir(parents=True, exist_ok=False)
    receipt = args.output_dir / "receipt.json"
    started = time.monotonic()
    result = {"schema": SCHEMA, "status": "failed", "scope": "virtual-x11-interactive",
              "operator_desktop_tested": False, "checks": []}
    process = None
    previous_term = signal.getsignal(signal.SIGTERM)
    def interrupted(_signal, _frame):
        raise KeyboardInterrupt("virtual X11 collector interrupted")
    signal.signal(signal.SIGTERM, interrupted)
    try:
        require(args.work.is_dir(), "existing owned work directory is required")
        required = ("xvfb-run", "Xvfb", "xauth", "xdotool", "xprop", "xwininfo")
        missing = [name for name in required if shutil.which(name) is None]
        require(not missing, "missing virtual X11 tools: " + ", ".join(missing))
        result["tools"] = {name: shutil.which(name) for name in required}
        save(receipt, result)
        with tempfile.TemporaryDirectory(prefix="cutechess-x11-", dir=args.work) as private:
            private = Path(private)
            environment = dict(os.environ, TMPDIR=str(private), TMP=str(private), TEMP=str(private))
            for name in ("DISPLAY", "XAUTHORITY", "WAYLAND_DISPLAY"):
                environment.pop(name, None)
            command = ["xvfb-run", "--auto-servernum", "--error-file=" + str(args.output_dir / "xvfb.log"),
                       "--server-args=-screen 0 1280x1024x24 -nolisten tcp -noreset",
                       sys.executable, str(Path(__file__).resolve()), "--internal-worker",
                       "--binary", str(args.binary), "--receipt", str(args.receipt),
                       "--lock", str(args.lock), "--output-dir", str(args.output_dir),
                       "--session-root", str(private), "--timeout-seconds", str(args.timeout_seconds)]
            with (args.output_dir / "session.log").open("w", encoding="utf-8") as stream:
                process = subprocess.Popen(command, env=environment, stdin=subprocess.DEVNULL,
                                           stdout=stream, stderr=subprocess.STDOUT, start_new_session=True)
                try:
                    returncode = process.wait(timeout=max(.001, args.timeout_seconds - (time.monotonic() - started)))
                finally:
                    stop_group(process)
                    process = None
            result = json.loads(receipt.read_text(encoding="utf-8"))
            require(returncode == 0 and result.get("status") == "passed",
                    result.get("error", "virtual X11 session failed"))
            result["tools"] = {name: shutil.which(name) for name in required}
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError, KeyboardInterrupt) as error:
        if receipt.is_file():
            try:
                result = json.loads(receipt.read_text(encoding="utf-8"))
            except (ValueError, OSError):
                pass
        result["status"] = "failed"
        result["error"] = ("whole virtual X11 session deadline exceeded"
                           if isinstance(error, subprocess.TimeoutExpired) else
                           str(error) if isinstance(error, ValueError) else type(error).__name__)
    finally:
        signal.signal(signal.SIGTERM, previous_term)
        try:
            if process is not None:
                stop_group(process)
        except (OSError, subprocess.SubprocessError, KeyboardInterrupt) as cleanup_error:
            result["status"] = "failed"
            result["cleanup_error"] = {"type": type(cleanup_error).__name__}
        result["whole_session_seconds_including_cleanup"] = time.monotonic() - started
        result["deadline_seconds"] = args.timeout_seconds
        result["cleanup_allowance_seconds"] = 5
        save(receipt, result)
    print(json.dumps({"schema": SCHEMA, "status": result["status"], "receipt": str(receipt),
                      "scope": "virtual-x11-interactive", "operator_desktop_tested": False}), flush=True)
    return 0 if result["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
