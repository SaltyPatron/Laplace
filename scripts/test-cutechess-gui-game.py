#!/usr/bin/env python3
"""Local protocol/ownership controls, not remote GUI or completed-game evidence."""
import copy
import importlib.util
import io
import json
import os
from pathlib import Path
import shlex
import signal
import subprocess
import sys
import tempfile
import time
import types
import unittest
from unittest import mock

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("gui_game", ROOT / "scripts/check-cutechess-gui-game.py")
OWNER = importlib.util.module_from_spec(spec)
spec.loader.exec_module(OWNER)
PROVIDER = ("info string providers root=1/0 position=20/0(atoms:0) "
            "root-work-scope=provider-instance-deltas")
MOVES = ["f2f3", "e7e5", "g2g4", "d8h4"]


def document(moves=MOVES, termination=""):
    return {"evidence": {"Schema": "laplace.cutechess-gui-pgn/v1", "Status": "passed",
            "CompleteSourceVerified": True, "DatabaseRecordingProven": False,
            "White": OWNER.WHITE, "Black": OWNER.BLACK, "TimeControl": OWNER.CLOCK,
            "MovesUci": list(moves), "Plies": len(moves), "Termination": termination, "Result": "0-1"}}


def traffic(moves=MOVES):
    rows = []
    for index, move in enumerate(moves):
        name = OWNER.WHITE if index % 2 == 0 else OWNER.BLACK
        rows.append(f">{name}({index % 2}): go wtime 60000 btime 60000 winc 1000 binc 1000")
        if name == OWNER.BLACK:
            rows.append(f"<{name}(1): " + PROVIDER)
        rows.append(f"<{name}({index % 2}): bestmove {move}")
    return "\n".join(rows)


class GuiGameControls(unittest.TestCase):
    def setUp(self):
        directory = os.environ.get("TMPDIR")
        if not directory or not Path(directory).is_absolute() or not Path(directory).is_dir():
            raise RuntimeError("controls require an explicit permanent TMPDIR")
        self.scratch = tempfile.TemporaryDirectory(prefix="gui-game-controls-", dir=directory)
        self.addCleanup(self.scratch.cleanup)
        self.root = Path(self.scratch.name)

    def test_complete_protocol_requires_every_native_replayed_move_and_both_clock_searches(self):
        proof = OWNER.verify_protocol(traffic(), document())
        self.assertEqual(4, proof["accepted_engine_moves"])
        self.assertEqual(2, len(proof["substrate_search_receipts"]))
        self.assertFalse(proof["prepared_message_observed"])
        # A real search receipt is stronger evidence than a readiness acknowledgement.
        for changed in (traffic().replace("bestmove g2g4", "bestmove g2g3"),
                        traffic().replace("bestmove d8h4", "bestmove d8h5"),
                        traffic().replace("go wtime 60000", "go depth 4 wtime 60000"),
                        traffic().replace(PROVIDER, "readyok"),
                        traffic().rsplit(PROVIDER, 1)[0] + "readyok" + traffic().rsplit(PROVIDER, 1)[1],
                        traffic().replace("root=1/0", "root=0/0")):
            with self.subTest(changed=changed[-80:]), self.assertRaises(ValueError):
                OWNER.verify_protocol(changed, document())


    def test_actual_protocol_options_and_button_actions_are_retained(self):
        commands = (f">{OWNER.WHITE}(0): setoption name Threads value 4\n"
                    f">{OWNER.WHITE}(0): setoption name Hash value 256\n"
                    f">{OWNER.WHITE}(0): setoption name Clear Hash\n")
        proof = OWNER.verify_protocol(commands + traffic(), document())
        self.assertEqual({"Threads": "4", "Hash": "256", "Clear Hash": None},
                         proof["applied_uci_options"][OWNER.WHITE])
        self.assertEqual({}, proof["applied_uci_options"][OWNER.BLACK])

    def test_clock_finish_never_records_a_late_unaccepted_move(self):
        accepted = document(MOVES[:2], "time forfeit")
        proof = OWNER.verify_protocol(traffic(MOVES[:3]), accepted)
        self.assertEqual(2, proof["accepted_engine_moves"])
        self.assertEqual([(OWNER.WHITE, "g2g4")], proof["unaccepted_after_clock"])
        for bad in (document(MOVES[:2]), document(MOVES[:1], "time forfeit")):
            with self.assertRaises(ValueError):
                OWNER.verify_protocol(traffic(MOVES[:3]), bad)
        with self.assertRaises(ValueError):
            OWNER.verify_protocol(traffic(), accepted)

    def test_verifier_receipt_cannot_substitute_wrong_pair_clock_or_recording_claim(self):
        for key, value in (("White", "other"), ("TimeControl", "1/move"),
                           ("CompleteSourceVerified", False), ("DatabaseRecordingProven", True),
                           ("Plies", 99), ("Status", "failed")):
            changed = copy.deepcopy(document())
            changed["evidence"][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                OWNER.verify_protocol(traffic(), changed)

    def test_real_gui_thread_child_inventory_excludes_unrelated_process(self):
        child_file = self.root / "child.pid"
        code = ("import pathlib,subprocess,threading,time\n"
                "def launch():\n"
                " child=subprocess.Popen(['/bin/sleep','30'])\n"
                " pathlib.Path(" + repr(str(child_file)) + ").write_text(str(child.pid))\n"
                " child.wait()\n"
                "threading.Thread(target=launch).start()\n"
                "time.sleep(30)\n")
        parent = subprocess.Popen([sys.executable, "-c", code], start_new_session=True)
        unrelated = subprocess.Popen(["/bin/sleep", "30"], start_new_session=True)
        try:
            deadline = time.monotonic() + 5
            while not child_file.exists() and time.monotonic() < deadline:
                time.sleep(.02)
            self.assertTrue(child_file.exists())
            child = int(child_file.read_text())
            self.assertIn(child, OWNER.children(parent.pid))
            self.assertNotIn(unrelated.pid, OWNER.children(parent.pid))
            observed = OWNER.engine.process_identity(OWNER.ObservedProcess(child), Path("/bin/sleep"))
            self.assertEqual(child, observed["pid"])
            self.assertEqual(os.geteuid(), observed["uid"])
            with self.assertRaises(ValueError):
                OWNER.engine.process_identity(OWNER.ObservedProcess(child), Path(sys.executable))
        finally:
            OWNER.engine.stop_group(parent)
            OWNER.engine.stop_group(unrelated)

    def test_real_qt_output_pipe_enforces_bound_without_unbounded_capture(self):
        process = subprocess.Popen([sys.executable, "-c",
                                    "import os,time;os.write(1,b'x'*8192);time.sleep(30)"],
                                   stdout=subprocess.PIPE, start_new_session=True)
        try:
            output = OWNER.GuiOutput(process, io.BytesIO())
            deadline = time.monotonic() + 5
            with mock.patch.object(OWNER, "MAX_TEXT", 4096):
                while output.bytes == 0 and time.monotonic() < deadline:
                    try:
                        output.drain()
                    except ValueError:
                        break
                    time.sleep(.01)
                else:
                    self.fail("real child did not cross the output envelope")
            self.assertGreater(output.bytes, 4096)
        finally:
            OWNER.engine.stop_group(process)
            process.stdout.close()

    def test_normal_exit_drains_real_pipe_beyond_kernel_capacity_and_retains_status(self):
        size = 1024 * 1024
        for expected in (0, 7):
            with self.subTest(returncode=expected):
                process = subprocess.Popen(
                    [sys.executable, "-c",
                     "import sys;sys.stdout.buffer.write(b'x'*" + str(size)
                     + ");sys.stdout.buffer.flush();sys.exit(" + str(expected) + ")"],
                    stdout=subprocess.PIPE, start_new_session=True)
                try:
                    target = io.BytesIO()
                    output = OWNER.GuiOutput(process, target)
                    self.assertEqual(expected, output.wait_for_exit(time.monotonic() + 5))
                    self.assertEqual(size, output.bytes)
                    self.assertEqual(b"x" * size, target.getvalue())
                    self.assertEqual(expected, process.returncode)
                finally:
                    OWNER.engine.stop_group(process)
                    process.stdout.close()

    def test_normal_exit_timeout_never_terminates_or_accepts_the_live_process(self):
        process = subprocess.Popen([sys.executable, "-c", "import time;time.sleep(30)"],
                                   stdout=subprocess.PIPE, start_new_session=True)
        try:
            output = OWNER.GuiOutput(process, io.BytesIO())
            began = time.monotonic()
            with self.assertRaisesRegex(TimeoutError, "did not exit normally"):
                output.wait_for_exit(began + .1)
            self.assertLess(time.monotonic() - began, 2)
            self.assertIsNone(process.poll())
        finally:
            OWNER.engine.stop_group(process)
            process.stdout.close()

    def test_debug_menu_releases_popup_after_both_existing_and_new_dock_visibility(self):
        roles = types.SimpleNamespace(MENU_ITEM=1, PANEL=2, TEXT=3, ENTRY=4)
        api = types.SimpleNamespace(Role=roles, StateType=types.SimpleNamespace(CHECKED=5))
        for checked in (False, True):
            with self.subTest(already_checked=checked):
                calls = []
                session = types.SimpleNamespace(key=lambda key: calls.append(("key", key)))
                owner = OWNER.Accessibility(api, 41, session)
                view, dock = object(), object()
                toggle = types.SimpleNamespace(
                    get_state_set=lambda: types.SimpleNamespace(contains=lambda _: checked))
                text = types.SimpleNamespace(get_text_iface=lambda: object(), get_role=lambda: roles.TEXT)
                def one(root, *, name, role):
                    if name == "View":
                        return view
                    if role == roles.MENU_ITEM:
                        return toggle
                    self.assertEqual(("key", "Escape"), calls[-1])
                    return dock
                with mock.patch.object(owner, "one", side_effect=one), \
                     mock.patch.object(owner, "act", side_effect=lambda node, *args: calls.append(
                         ("act", "view" if node is view else "toggle"))), \
                     mock.patch.object(owner, "walk", return_value=iter([text])):
                    self.assertIs(text, owner.show_debug(object()))
                expected = [("act", "view")]
                if not checked:
                    expected.append(("act", "toggle"))
                self.assertEqual(expected + [("key", "Escape")], calls)

    def test_accessibility_refuses_unrelated_pid_and_ambiguous_or_disabled_named_control(self):
        class Node:
            def __init__(self, name, role, children=(), pid=41, enabled=True):
                self.name, self.role, self.children, self.pid = name, role, children, pid
                self.enabled = enabled
            def clear_cache(self): pass
            def get_name(self): return self.name
            def get_role(self): return self.role
            def get_child_count(self): return len(self.children)
            def get_child_at_index(self, i): return self.children[i]
            def get_process_id(self): return self.pid
            def get_state_set(self): return self
            def contains(self, state): return self.enabled
        desktop = Node("", 0, [Node("Cute Chess", 1, pid=99)])
        api = types.SimpleNamespace(get_desktop=lambda _i: desktop,
                                    StateType=types.SimpleNamespace(ENABLED=1))
        session = types.SimpleNamespace(remaining=lambda: 1)
        owner = OWNER.Accessibility(api, 41, session)
        self.assertIsNone(owner.application())
        first = Node("CPU", 2, enabled=False)
        with self.assertRaises(ValueError):
            owner.act(first)
        with self.assertRaises(ValueError):
            owner.one(Node("White", 3, [first, Node("CPU", 2)]), role=2, name="CPU")
        # Cyclic/unbounded remote widget data must not recurse indefinitely.
        first.children = [first]
        with self.assertRaises(ValueError):
            list(owner.walk(first))


    def test_qt_action_names_keep_unique_acknowledged_target(self):
        class Actions:
            def __init__(self, names, ack=True):
                self.names, self.ack, self.invoked = names, ack, []
            def get_n_actions(self): return len(self.names)
            def get_action_name(self, index): return self.names[index]
            def do_action(self, index):
                self.invoked.append(index)
                return self.ack
        class Node:
            def __init__(self, actions, enabled=True):
                self.actions, self.enabled = actions, enabled
            def get_state_set(self): return self
            def contains(self, state): return self.enabled
            def get_action_iface(self): return self.actions
        api = types.SimpleNamespace(StateType=types.SimpleNamespace(ENABLED=1))
        owner = OWNER.Accessibility(api, 41, types.SimpleNamespace(remaining=lambda: 1))
        for available, wanted in ((["Toggle", "SetFocus"], ("press", "click", "toggle")),
                                  (["Press", "SetFocus"], ("press", "click", "toggle")),
                                  (["ShowMenu", "SetFocus"], ("showMenu",)),
                                  (["toggle", "setFocus"], ("press", "click", "toggle"))):
            with self.subTest(available=available):
                action = Actions(available)
                owner.act(Node(action), wanted)
                self.assertEqual([0], action.invoked)
        for names, ack, enabled, fragment in (
                (["Toggle", "toggle"], True, True, "ambiguous"),
                (["SetFocus"], True, True, "available"),
                (["Toggle"], False, True, "acknowledged: Toggle"),
                (["Toggle"], True, False, "disabled")):
            with self.subTest(names=names, ack=ack, enabled=enabled):
                action = Actions(names, ack)
                with self.assertRaisesRegex(ValueError, fragment):
                    owner.act(Node(action, enabled))
                self.assertEqual([0] if not ack and enabled else [], action.invoked)


    def test_accessibility_vanished_sibling_preserves_the_actual_named_control(self):
        class Node:
            def __init__(self, name, role, children=()):
                self.name, self.role, self.children = name, role, list(children)
            def clear_cache(self): pass
            def get_child_count(self): return len(self.children)
            def get_child_at_index(self, index): return self.children[index]
            def get_name(self): return self.name
            def get_role(self): return self.role
        owner = OWNER.Accessibility(None, 41, types.SimpleNamespace(remaining=lambda: 1))
        target = Node("&View", 7)
        root = Node("Cute Chess", 1, [None, target, None])
        self.assertEqual([root, target], list(owner.walk(root)))
        self.assertIs(target, owner.one(root, role=7, name="View"))
        for children in ([None], [target, None, Node("&View", 7)]):
            with self.subTest(children=len(children)), self.assertRaisesRegex(ValueError, "absent or ambiguous"):
                owner.one(Node("Cute Chess", 1, children), role=7, name="View")

    def test_accessibility_missing_slots_preserve_root_and_remote_tree_bounds(self):
        class Node:
            def __init__(self, children=()): self.children = list(children)
            def clear_cache(self): pass
            def get_child_count(self): return len(self.children)
            def get_child_at_index(self, index): return self.children[index]
        owner = OWNER.Accessibility(None, 41, types.SimpleNamespace(remaining=lambda: 1))
        with self.assertRaisesRegex(ValueError, "root is unavailable"):
            list(owner.walk(None))
        with self.assertRaisesRegex(ValueError, "child count exceeds"):
            list(owner.walk(Node([None] * 513)))
        root = Node()
        root.children = [None, root]
        with self.assertRaisesRegex(ValueError, "tree exceeds"):
            list(owner.walk(root))

    def package_tools(self):
        tools = self.root / "bin"
        tools.mkdir()
        state, calls = self.root / "installed.json", self.root / "apt.json"
        state.write_text("[]")
        program = ("#!" + sys.executable + "\nimport json,pathlib,sys\n"
                   "name=pathlib.Path(sys.argv[0]).name\n"
                   "state=pathlib.Path(" + repr(str(state)) + ")\n"
                   "calls=pathlib.Path(" + repr(str(calls)) + ")\n"
                   "if name=='dpkg': print('amd64')\n"
                   "elif name=='id': print('0')\n"
                   "elif name=='dpkg-query':\n"
                   " if sys.argv[-1] not in json.loads(state.read_text()): sys.exit(1)\n"
                   " print('amd64\\tinstall ok installed\\t1.2.3')\n"
                   "elif name=='apt-get':\n"
                   " calls.write_text(json.dumps(sys.argv[1:]))\n"
                   " state.write_text(json.dumps(sys.argv[sys.argv.index('--no-install-recommends')+1:]))\n")
        for name in ("dpkg", "dpkg-query", "apt-get", "id"):
            path = tools / name
            path.write_text(program)
            path.chmod(0o700)
        return tools, state, calls

    def test_optional_accessibility_package_mode_uses_real_missing_only_installer(self):
        tools, state, calls = self.package_tools()
        source = (ROOT / "scripts/bootstrap-laplace-runner.sh").read_text()
        body = source.split("bootstrap_chess_gui_runtime() {", 1)[1].split(
            "\nbootstrap_build_environment() {", 1)[0]
        script = ("say(){ :; }; green(){ :; }; red(){ :; }\n"
                  "bootstrap_chess_gui_runtime() {" + body
                  + "\nbootstrap_chess_gui_runtime accessibility\n")
        environment = dict(os.environ, PATH=str(tools) + os.pathsep + os.environ["PATH"],
                           PYTHONDONTWRITEBYTECODE="1")
        first = subprocess.run(["bash", "-euc", script], env=environment, capture_output=True,
                               text=True, timeout=10)
        self.assertEqual(0, first.returncode, first.stderr)
        expected = ["dbus", "at-spi2-core", "gir1.2-atspi-2.0", "python3-gi"]
        self.assertEqual(expected, json.loads(state.read_text()))
        argv = calls.read_bytes()
        second = subprocess.run(["bash", "-euc", script], env=environment, capture_output=True,
                                text=True, timeout=10)
        self.assertEqual(0, second.returncode, second.stderr)
        self.assertEqual(argv, calls.read_bytes())
        self.assertNotIn("xvfb", json.loads(state.read_text()))


if __name__ == "__main__":
    unittest.main()
