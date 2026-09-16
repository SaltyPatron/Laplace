#!/usr/bin/env python3
"""Refusal/ownership controls for the X11 collector; fixtures are not Qt GUI proof."""
import importlib.util
import json
import os
from pathlib import Path
import selectors
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location(
    "cutechess_session", Path(__file__).with_name("check-cutechess-gui-session.py"))
session = importlib.util.module_from_spec(spec)
spec.loader.exec_module(session)


class Clock:
    value = 0.0
    def now(self):
        return self.value
    def advance(self, seconds):
        self.value += seconds


class Protocol:
    """Controlled X11 command replies, never reported as a real Qt session."""
    def __init__(self):
        self.pid = 9123
        self.visible = [101]
        self.focused = None
        self.owner = self.pid
        self.window_class = '"cutechess", "cutechess"'
        self.viewable = True
        self.width = 900
        self.dialog_title = "New Game"
        self.transient = 101
        self.cancel_works = True
        self.calls = []
        self.checkpoints = []

    def run(self, arguments, **kwargs):
        self.calls.append(tuple(arguments))
        self.assert_runtime_options(kwargs)
        if arguments[:2] == ("xdotool", "search"):
            value = "\n".join(map(str, self.visible))
            return subprocess.CompletedProcess(arguments, 0 if self.visible else 1, value, "")
        if arguments[:2] == ("xprop", "-id"):
            identity = int(arguments[2])
            transient = (f"WM_TRANSIENT_FOR(WINDOW): window id # {hex(self.transient)}"
                         if identity == 202 else "WM_TRANSIENT_FOR:  not found.")
            value = (f"_NET_WM_PID(CARDINAL) = {self.owner}\n"
                     f"WM_CLASS(STRING) = {self.window_class}\n{transient}\n")
        elif arguments[:2] == ("xwininfo", "-id"):
            state = "IsViewable" if self.viewable else "IsUnMapped"
            value = f"  Width: {self.width}\n  Height: 700\n  Map State: {state}\n"
        elif arguments[:2] == ("xdotool", "getwindowname"):
            value = "player vs player\n" if int(arguments[2]) == 101 else self.dialog_title + "\n"
        elif arguments[:2] == ("xdotool", "windowfocus"):
            self.focused = int(arguments[2])
            value = ""
        elif arguments[:2] == ("xdotool", "getwindowfocus"):
            value = str(self.focused) + "\n"
        elif arguments[:3] == ("xdotool", "key", "--clearmodifiers"):
            key = arguments[3]
            if key == "ctrl+n":
                self.visible.append(202)
            elif key == "Escape":
                if self.cancel_works:
                    self.visible.remove(202)
            elif key != "ctrl+q":
                raise AssertionError("unexpected key " + key)
            value = ""
        else:
            raise AssertionError("unexpected X11 command " + repr(arguments))
        return subprocess.CompletedProcess(arguments, 0, value, "")

    @staticmethod
    def assert_runtime_options(kwargs):
        assert kwargs["stdin"] == subprocess.DEVNULL
        assert kwargs["capture_output"] is True
        assert kwargs["text"] is True
        assert 0 < kwargs["timeout"] <= 5

    def checkpoint(self, name, value):
        self.checkpoints.append((name, value))


class InteractionControls(unittest.TestCase):
    def exercise(self, protocol, process=None):
        process = process or mock.Mock(pid=protocol.pid)
        process.poll.return_value = None
        process.wait.return_value = 0
        clock = Clock()
        identity = {"pid": protocol.pid, "executable": "/selected/cutechess",
                    "sha256": "b" * 64, "start_ticks": 12345}
        with mock.patch.object(session, "process_identity", return_value=identity), \
             mock.patch.object(session.subprocess, "run", side_effect=protocol.run), \
             mock.patch.object(session.time, "monotonic", side_effect=clock.now), \
             mock.patch.object(session.time, "sleep", side_effect=clock.advance):
            selected = session.X11Session(process, Path("/selected/cutechess"), {"DISPLAY": ":117"}, .5)
            selected.exercise(protocol.checkpoint)
        return process

    def test_normal_action_sequence_requires_window_dialog_cancel_and_zero_exit(self):
        protocol = Protocol()
        process = self.exercise(protocol)
        self.assertEqual(["main_window_mapped", "new_game_dialog_mapped", "dialog_cancelled", "normal_quit"],
                         [name for name, _ in protocol.checkpoints])
        self.assertEqual([("xdotool", "key", "--clearmodifiers", key)
                          for key in ("ctrl+n", "Escape", "ctrl+q")],
                         [call for call in protocol.calls if call[:2] == ("xdotool", "key")])
        self.assertFalse(any("windowactivate" in call or "windowclose" in call
                             or "windowkill" in call for call in protocol.calls))
        process.wait.assert_called_once()
        self.assertEqual(9123, protocol.checkpoints[0][1]["pid"])

    def test_wrong_owner_class_map_state_and_geometry_cannot_be_ready(self):
        for field, value, reason in (("owner", 1, "different owner"),
                                     ("window_class", '"other", "other"', "class"),
                                     ("viewable", False, "not viewable"),
                                     ("width", 0, "geometry")):
            with self.subTest(field=field):
                protocol = Protocol()
                setattr(protocol, field, value)
                with self.assertRaisesRegex(ValueError, reason):
                    self.exercise(protocol)
                self.assertEqual([], protocol.checkpoints)

    def test_dialog_title_and_transient_binding_must_both_match(self):
        for field, value in (("dialog_title", "Unrelated Dialog"), ("transient", 999)):
            with self.subTest(field=field):
                protocol = Protocol()
                setattr(protocol, field, value)
                with self.assertRaisesRegex(ValueError, "New Game dialog"):
                    self.exercise(protocol)
                self.assertEqual(["main_window_mapped"], [name for name, _ in protocol.checkpoints])

    def test_ignored_escape_cannot_become_a_successful_quit(self):
        protocol = Protocol()
        protocol.cancel_works = False
        with self.assertRaisesRegex(ValueError, "did not close"):
            self.exercise(protocol)
        self.assertNotIn(("xdotool", "key", "--clearmodifiers", "ctrl+q"), protocol.calls)
        self.assertNotIn("normal_quit", [name for name, _ in protocol.checkpoints])

    def test_quit_timeout_or_nonzero_exit_is_not_readiness(self):
        for side_effect, reason in ((subprocess.TimeoutExpired("fixture", .1), "did not exit normally"),
                                    (None, "unsuccessfully")):
            with self.subTest(reason=reason):
                protocol = Protocol()
                process = mock.Mock(pid=protocol.pid)
                process.wait.side_effect = side_effect if side_effect else lambda **_: 7
                with self.assertRaisesRegex(ValueError, reason):
                    self.exercise(protocol, process)
                self.assertNotIn("normal_quit", [name for name, _ in protocol.checkpoints])

    def test_changed_process_identity_stops_before_any_input(self):
        protocol = Protocol()
        process = mock.Mock(pid=protocol.pid)
        with mock.patch.object(session, "process_identity", side_effect=[{"start_ticks": 1}, {"start_ticks": 2}]), \
             mock.patch.object(session.subprocess, "run", side_effect=protocol.run):
            selected = session.X11Session(process, Path("/selected/cutechess"), {}, time.monotonic() + 1)
            with self.assertRaisesRegex(ValueError, "identity changed"):
                selected.exercise(protocol.checkpoint)
        self.assertEqual([], protocol.calls)

    def test_x11_tool_failure_and_excessive_inventory_are_refused(self):
        protocol = Protocol()
        protocol.visible = list(range(1, 34))
        with self.assertRaisesRegex(ValueError, "excessive"):
            self.exercise(protocol)
        process = mock.Mock(pid=protocol.pid)
        with mock.patch.object(session, "process_identity", return_value={"start_ticks": 1}), \
             mock.patch.object(session.subprocess, "run",
                               return_value=subprocess.CompletedProcess([], 2, "", "fixture failure")):
            selected = session.X11Session(process, Path("/selected/cutechess"), {}, time.monotonic() + 1)
            with self.assertRaisesRegex(ValueError, "X11 command failed"):
                selected.windows()


@unittest.skipUnless(sys.platform.startswith("linux"), "Linux process ownership contract")
class ProcessOwnershipControls(unittest.TestCase):
    @staticmethod
    def alive(pid):
        try:
            stat = Path(f"/proc/{pid}/stat").read_text()
            return stat[stat.rfind(")") + 2:].split()[0] != "Z"
        except FileNotFoundError:
            return False

    def test_process_binding_uses_actual_executable_and_start_identity(self):
        child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
        try:
            found = session.process_identity(child, Path(sys.executable))
            self.assertEqual(child.pid, found["pid"])
            self.assertEqual(str(Path(sys.executable).resolve()), found["executable"])
            self.assertGreater(found["start_ticks"], 0)
            self.assertEqual(session.digest(sys.executable), found["sha256"])
            with self.assertRaisesRegex(ValueError, "differs"):
                session.process_identity(child, Path("/bin/false"))
        finally:
            child.terminate()
            child.wait(timeout=2)

    def test_repeated_group_cleanup_failure_cannot_leave_a_passed_receipt(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            output = directory / "evidence"
            process = mock.Mock()
            def completed(**_kwargs):
                session.save(output / "receipt.json", {"schema": session.SCHEMA, "status": "passed"})
                return 0
            process.wait.side_effect = completed
            with mock.patch.object(session.shutil, "which", side_effect=lambda tool: "/fixture/" + tool), \
                 mock.patch.object(session.subprocess, "Popen", return_value=process), \
                 mock.patch.object(session, "stop_group", side_effect=PermissionError("fixture cleanup")) as stop, \
                 mock.patch("builtins.print"):
                code = session.main(["--binary", str(directory / "cutechess"),
                                     "--receipt", str(directory / "build.json"),
                                     "--output-dir", str(output), "--work", str(directory)])
            retained = json.loads((output / "receipt.json").read_text())
            self.assertEqual(1, code)
            self.assertEqual("failed", retained["status"])
            self.assertEqual({"type": "PermissionError"}, retained["cleanup_error"])
            self.assertEqual(2, stop.call_count)
            self.assertIn("whole_session_seconds_including_cleanup", retained)

    def test_owned_group_cleanup_reaps_resistant_descendant_and_preserves_other_process(self):
        descendant = "import signal,time; signal.signal(signal.SIGTERM,signal.SIG_IGN); print('ready',flush=True); time.sleep(30)"
        leader = ("import subprocess,sys,time; "
                  "p=subprocess.Popen([sys.executable,'-c'," + repr(descendant) + "],stdout=subprocess.PIPE,text=True); "
                  "p.stdout.readline(); print(p.pid,flush=True); time.sleep(30)")
        unrelated = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"])
        owned = subprocess.Popen([sys.executable, "-c", leader], start_new_session=True,
                                 stdout=subprocess.PIPE, text=True)
        descendant_pid = None
        try:
            with selectors.DefaultSelector() as selector:
                selector.register(owned.stdout, selectors.EVENT_READ)
                self.assertTrue(selector.select(timeout=3), "fixture group failed to start")
                descendant_pid = int(owned.stdout.readline().strip())
            session.stop_group(owned)
            limit = time.monotonic() + 2
            while self.alive(descendant_pid) and time.monotonic() < limit:
                time.sleep(.02)
            self.assertFalse(self.alive(descendant_pid))
            self.assertIsNone(unrelated.poll())
        finally:
            session.stop_group(owned)
            unrelated.terminate()
            unrelated.wait(timeout=2)
            if descendant_pid is not None and self.alive(descendant_pid):
                os.kill(descendant_pid, signal.SIGKILL)
            owned.stdout.close()


class GuiPackageProvisioningControls(unittest.TestCase):
    """Run the actual bootstrap mode with controlled package tools, never apt."""
    def exercise(self, *, uid=1000, missing=(), sudo_failure=False,
                 apt_failure=False, install_changes=True, repeats=1):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            bindir = root / "bin"
            bindir.mkdir()
            state_path = root / "state.json"
            state_path.write_text(json.dumps({"missing": list(missing)}), encoding="utf-8")
            tool_source = r"""import json, os, sys
from pathlib import Path
root = Path(os.environ["GUI_PACKAGE_FIXTURE"])
tool = Path(sys.argv[0]).name
arguments = sys.argv[1:]
with (root / "calls.jsonl").open("a", encoding="utf-8") as stream:
    stream.write(json.dumps({"tool": tool, "arguments": arguments,
                            "frontend": os.environ.get("DEBIAN_FRONTEND")}) + "\n")
state_path = root / "state.json"
state = json.loads(state_path.read_text())
if tool == "dpkg":
    assert arguments == ["--print-architecture"]
    print("amd64")
elif tool == "id":
    assert arguments == ["-u"]
    print(os.environ["GUI_PACKAGE_FIXTURE_UID"])
elif tool == "dpkg-query":
    assert arguments[:2] == ["-W", "-f=${Architecture}\\t${Status}\\t${Version}\\n"]
    package = arguments[2]
    print("i386\tinstall ok installed\tfixture-foreign-" + package)
    if package in state["missing"]:
        print("amd64\tdeinstall ok config-files\tfixture-old")
    else:
        desired = "hold" if package == "xvfb" else "install"
        architecture = "all" if package == "fonts-dejavu-core" else "amd64"
        print(architecture + "\t" + desired + " ok installed\tfixture-1.0-" + package)
elif tool == "sudo":
    assert arguments[:3] == ["-n", "env", "DEBIAN_FRONTEND=noninteractive"]
    if os.environ["GUI_PACKAGE_FIXTURE_SUDO_FAILURE"] == "1":
        raise SystemExit(1)
    os.execvpe(arguments[1], arguments[1:], os.environ)
elif tool == "apt-get":
    assert os.environ.get("DEBIAN_FRONTEND") == "noninteractive"
    if os.environ["GUI_PACKAGE_FIXTURE_APT_FAILURE"] == "1":
        raise SystemExit(100)
    selected = arguments[arguments.index("--no-install-recommends") + 1:]
    if os.environ["GUI_PACKAGE_FIXTURE_INSTALL_CHANGES"] == "1":
        state["missing"] = [name for name in state["missing"] if name not in selected]
        state_path.write_text(json.dumps(state), encoding="utf-8")
else:
    raise AssertionError("unexpected fixture tool: " + tool)
"""
            for name in ("id", "dpkg", "dpkg-query", "sudo", "apt-get"):
                path = bindir / name
                path.write_text("#!" + sys.executable + "\n" + tool_source, encoding="utf-8")
                path.chmod(0o755)
            environment = dict(
                os.environ, PATH=str(bindir) + os.pathsep + os.environ["PATH"],
                LAPLACE_OPERATOR="fixture-operator", GUI_PACKAGE_FIXTURE=str(root),
                GUI_PACKAGE_FIXTURE_UID=str(uid),
                GUI_PACKAGE_FIXTURE_SUDO_FAILURE="1" if sudo_failure else "0",
                GUI_PACKAGE_FIXTURE_APT_FAILURE="1" if apt_failure else "0",
                GUI_PACKAGE_FIXTURE_INSTALL_CHANGES="1" if install_changes else "0")
            script = Path(__file__).with_name("bootstrap-laplace-runner.sh")
            runs = [subprocess.run(["bash", str(script), "chess-gui-runtime"], env=environment,
                                   capture_output=True, text=True, timeout=15)
                    for _ in range(repeats)]
            calls = [json.loads(line) for line in (root / "calls.jsonl").read_text().splitlines()]
            return runs, calls, json.loads(state_path.read_text())

    def test_present_packages_are_reported_without_privilege_or_installation(self):
        runs, calls, _ = self.exercise(repeats=2, sudo_failure=True, apt_failure=True)
        self.assertEqual([0, 0], [run.returncode for run in runs], [run.stderr for run in runs])
        self.assertEqual({"dpkg", "dpkg-query"}, {call["tool"] for call in calls})
        packages = {call["arguments"][-1] for call in calls if call["tool"] == "dpkg-query"}
        self.assertTrue({"xvfb", "xauth", "xdotool", "x11-utils", "libxcb-cursor0",
                         "libxkbcommon-x11-0", "fonts-dejavu-core"}.issubset(packages))
        for run in runs:
            for package in packages:
                self.assertIn("chess-gui-package\t" + package + "\tfixture-1.0-" + package,
                              run.stdout)

    def test_only_missing_packages_are_installed_and_reread_as_root_or_via_sudo(self):
        missing = ["xvfb", "libxcb-cursor0"]
        for uid in (0, 1000):
            with self.subTest(uid=uid):
                runs, calls, state = self.exercise(uid=uid, missing=missing)
                self.assertEqual(0, runs[0].returncode, runs[0].stderr)
                self.assertEqual([], state["missing"])
                apt = [call for call in calls if call["tool"] == "apt-get"]
                self.assertEqual(1, len(apt))
                arguments = apt[0]["arguments"]
                self.assertEqual(
                    ["-o", "DPkg::Lock::Timeout=60", "-o", "Acquire::Retries=1",
                     "-o", "Acquire::http::Timeout=30", "-o", "Acquire::https::Timeout=30",
                     "install", "-y", "--no-install-recommends", *missing], arguments)
                self.assertEqual("noninteractive", apt[0]["frontend"])
                self.assertEqual(0 if uid == 0 else 1,
                                 len([call for call in calls if call["tool"] == "sudo"]))
                installed_at = next(index for index, call in enumerate(calls)
                                    if call["tool"] == "apt-get")
                reread = {call["arguments"][-1] for call in calls[installed_at + 1:]
                          if call["tool"] == "dpkg-query"}
                self.assertTrue(set(missing).issubset(reread))
                for package in missing:
                    self.assertIn("chess-gui-package\t" + package + "\tfixture-1.0-" + package,
                                  runs[0].stdout)

    def test_refused_privilege_or_failed_installation_remains_failure(self):
        for options, apt_count in (({"uid": 1000, "sudo_failure": True}, 0),
                                   ({"uid": 0, "apt_failure": True}, 1)):
            with self.subTest(options=options):
                runs, calls, state = self.exercise(missing=["xvfb"], **options)
                self.assertEqual(1, runs[0].returncode)
                self.assertEqual(["xvfb"], state["missing"])
                self.assertEqual(apt_count, len([call for call in calls if call["tool"] == "apt-get"]))
                self.assertNotIn("X11 host dependencies present", runs[0].stdout)

    def test_zero_exit_from_installer_requires_actual_installed_status_readback(self):
        runs, calls, state = self.exercise(uid=0, missing=["xvfb"], install_changes=False)
        self.assertEqual(1, runs[0].returncode)
        self.assertEqual(["xvfb"], state["missing"])
        self.assertEqual(1, len([call for call in calls if call["tool"] == "apt-get"]))
        self.assertIn("verification failed after installation: xvfb", runs[0].stdout)
        self.assertNotIn("X11 host dependencies present", runs[0].stdout)


class SelectedRuntimeControls(unittest.TestCase):
    def test_worker_reapplies_runtime_after_direct_launch_overlay_with_selected_qt(self):
        runtime = {"qt": {"prefix": "/selected/qt"},
                   "direct_launch": {"environment": {"LD_LIBRARY_PATH": "/selected/qt/lib",
                                                       "QT_PLUGIN_PATH": "/selected/qt/plugins"}}}
        receipt = Path("/selected/runtime.json")
        def bind(path, environment, deadline, *, qt_prefix):
            self.assertEqual(receipt, path)
            self.assertEqual("/selected/qt/lib", environment["LD_LIBRARY_PATH"])
            self.assertEqual(Path("/selected/qt"), qt_prefix)
            return {**environment, "LD_LIBRARY_PATH": "/selected/qt/lib:/private/lib"}, {"mode": "private"}
        with mock.patch.dict(session.os.environ, {"LD_LIBRARY_PATH": "/wrong/lib"}), \
             mock.patch.object(session, "bind_x11", side_effect=bind) as selected:
            environment, observed = session.gui_environment(runtime, receipt, 30)
        selected.assert_called_once()
        self.assertEqual("/selected/qt/lib:/private/lib", environment["LD_LIBRARY_PATH"])
        self.assertEqual({"mode": "private"}, observed)

    def test_explicit_failed_runtime_selection_is_never_replaced_by_host_tools(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            output = directory / "evidence"
            with mock.patch.object(session, "bind_x11", side_effect=RuntimeError("runtime package bytes changed")), \
                 mock.patch.object(session.shutil, "which") as host, \
                 mock.patch.object(session.subprocess, "Popen") as launch, \
                 mock.patch("builtins.print"):
                code = session.main(["--binary", str(directory / "cutechess"),
                                     "--receipt", str(directory / "build.json"),
                                     "--output-dir", str(output), "--work", str(directory),
                                     "--x11-runtime-receipt", str(directory / "runtime.json")])
            host.assert_not_called()
            launch.assert_not_called()
            self.assertEqual(1, code)
            retained = json.loads((output / "receipt.json").read_text())
            self.assertEqual("failed", retained["status"])
            self.assertIn("runtime package bytes changed", retained["error"])

    def test_outer_launch_uses_bound_wrapper_and_passes_selection_to_worker(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            output = directory / "evidence"
            receipt = directory / "runtime.json"
            selected_tools = {"xvfb-run": "/private/usr/bin/xvfb-run", "Xvfb": "/private/usr/bin/Xvfb"}
            process = mock.Mock()
            def complete(**_kwargs):
                session.save(output / "receipt.json", {"schema": session.SCHEMA, "status": "passed"})
                return 0
            process.wait.side_effect = complete
            with mock.patch.object(session, "bind_x11", return_value=(
                    {"PATH": "/private/usr/bin:/usr/bin", "LD_LIBRARY_PATH": "/private/usr/lib"},
                    {"tools": selected_tools})) as selection, \
                 mock.patch.object(session.subprocess, "Popen", return_value=process) as launch, \
                 mock.patch.object(session, "stop_group"), mock.patch("builtins.print"):
                code = session.main(["--binary", str(directory / "cutechess"),
                                     "--receipt", str(directory / "build.json"),
                                     "--output-dir", str(output), "--work", str(directory),
                                     "--x11-runtime-receipt", str(receipt)])
            self.assertEqual(0, code)
            selection.assert_called_once()
            command = launch.call_args.args[0]
            self.assertEqual("/private/usr/bin/xvfb-run", command[0])
            self.assertEqual(str(receipt), command[command.index("--x11-runtime-receipt") + 1])
            self.assertEqual("/private/usr/lib", launch.call_args.kwargs["env"]["LD_LIBRARY_PATH"])
            self.assertTrue(launch.call_args.kwargs["start_new_session"])


if __name__ == "__main__":
    unittest.main()
