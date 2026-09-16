#!/usr/bin/env python3
"""Protocol/ownership controls; fixture children are not installed engine evidence."""
import importlib.util
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("cutechess_engine_acceptance",
                                             ROOT / "scripts/check-cutechess-user-engines.py")
OWNER = importlib.util.module_from_spec(spec)
spec.loader.exec_module(OWNER)

PROVIDERS = ("root=1/0 position=21/0(atoms:0) learned-pst=21/0(cells:0) "
             "tactics=21/0(patterns:0) syzygy=0/0(0-men) "
             "root-work-scope=provider-instance-deltas root-backend=1 root-cache=0 "
             "root-frontiers=1 root-transitions=20/0/0 root-ms-inclusive=1.000 "
             "frontier-ms=0.200 evidence-read-ms=0.300 snapshot-prepare=1/0.400ms")


class UserEngineAcceptanceTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix="cutechess-user-engine-controls-")
        self.addCleanup(self.scratch.cleanup)
        self.root = Path(self.scratch.name)
        self.environment = dict(os.environ, PYTHONDONTWRITEBYTECODE="1")

    def engine(self, name, *, prepared=True, provider=PROVIDERS, best="e2e4", depth=2):
        path = self.root / name
        path.write_text("#!" + sys.executable + "\n" + (
            "import sys\n"
            "for command in sys.stdin:\n"
            " command=command.strip()\n"
            " if command=='uci':\n"
            "  print('id name protocol fixture')\n"
            "  print('option name Substrate type combo default substrate var substrate var off')\n"
            "  print('uciok',flush=True)\n"
            " elif command=='isready':\n"
            + ("  print('info string substrate provider stack prepared (selected:no-delta, syzygy 0-men)')\n"
               if prepared else "  print('info string substrate provider initialization failed (fixture)')\n") +
            "  print('readyok',flush=True)\n"
            " elif command.startswith('go '):\n"
            "  print('info depth " + str(depth) + " score cp 0 nodes 21 time 1 pv e2e4')\n"
            "  print(" + repr("info string providers " + provider) + ")\n"
            "  print(" + repr("bestmove " + best) + ",flush=True)\n"
            " elif command=='quit': break\n"))
        path.chmod(0o700)
        return {"name": name, "command": str(path), "workingDirectory": str(self.root)}

    def check(self, entry, *, substrate=True):
        # Only mapped native closure is replaced in these protocol fixtures.
        # Executable, uid, process lifetime and pipe transport are real.
        with mock.patch.object(OWNER, "mapped_closure", return_value={"fixture_only": True}):
            return OWNER.uci(entry, self.environment, self.root / (entry["name"] + ".log"),
                             time.monotonic() + 5, Path(sys.executable), substrate=substrate)

    def test_public_default_route_refuses_ambient_redirection_and_credentials(self):
        expected = OWNER.default_database_route({})
        self.assertEqual(5432, expected["port"])
        matching = {"PGHOST": "/var/run/postgresql", "PGUSER": "laplace_admin",
                    "PGPORT": "5432", "PGDATABASE": "laplace"}
        self.assertEqual(expected, OWNER.default_database_route(matching))
        for key, value in {"PGHOST": "/elsewhere", "PGUSER": "other",
                           "PGPORT": "55433", "PGDATABASE": "other",
                           "LAPLACE_DB": "Host=elsewhere", "PGPASSWORD": "fixture-secret",
                           "PGPASSFILE": "/fixture-passwords"}.items():
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, "public default") as caught:
                OWNER.default_database_route({**matching, key: value})
            self.assertNotIn(value, str(caught.exception))

    def test_real_child_complete_protocol_requires_prepared_and_searched_providers(self):
        result = self.check(self.engine("prepared"))
        self.assertEqual("e2e4", result["bestmove"])
        self.assertEqual(2, result["completed_depth"])
        self.assertEqual(1, result["providers"]["root_reads"])
        self.assertEqual(21, result["providers"]["position_reads"])
        self.assertEqual(os.geteuid(), result["process"]["uid"])
        self.assertFalse(result["complete_game_proven"])
        self.assertFalse(result["cold_boot_proven"])
        with self.assertRaises(ProcessLookupError):
            os.kill(result["process"]["pid"], 0)

    def test_readyok_does_not_hide_failed_substrate_initialization(self):
        with self.assertRaisesRegex(ValueError, "prepared substrate"):
            self.check(self.engine("not-prepared", prepared=False))

    def test_classical_receipt_and_zero_provider_work_are_refused(self):
        for index, provider in enumerate(("classical-only", PROVIDERS.replace("root=1/", "root=0/"),
                                         PROVIDERS.replace("position=21/", "position=0/"))):
            with self.subTest(provider=index), self.assertRaisesRegex(ValueError, "substrate providers"):
                self.check(self.engine("provider-" + str(index), provider=provider))

    def test_illegal_move_and_uncompleted_depth_are_refused(self):
        with self.assertRaisesRegex(ValueError, "legal starting"):
            self.check(self.engine("illegal", best="e2e5"))
        with self.assertRaisesRegex(ValueError, "requested depth"):
            self.check(self.engine("shallow", depth=1))

    def test_starting_search_without_substrate_does_not_claim_provider_access(self):
        result = self.check(self.engine("stockfish-protocol", prepared=False, provider="classical-only"),
                            substrate=False)
        self.assertNotIn("providers", result)

    def test_real_pipe_output_is_bounded_before_it_can_fill_memory(self):
        child = [sys.executable, "-c", "import os,time;os.write(1,b'x'*9000);time.sleep(30)"]
        with self.assertRaisesRegex(ValueError, "byte envelope"):
            with OWNER.Process(child, self.environment, self.root, self.root / "flood.log",
                               time.monotonic() + 3, maximum=4096) as running:
                process = running.process
                running.until(lambda _line: False)
        self.assertIsNotNone(process.poll())
        self.assertLessEqual((self.root / "flood.log").stat().st_size, 4096)

    def test_partial_line_stall_has_absolute_deadline_and_drains_only_owned_group(self):
        sibling = subprocess.Popen([sys.executable, "-c", "import time;time.sleep(30)"],
                                   start_new_session=True)
        self.addCleanup(OWNER.stop_group, sibling)
        child = [sys.executable, "-c",
                 "import os,signal,time;signal.signal(signal.SIGTERM,signal.SIG_IGN);"
                 "os.write(1,b'partial');time.sleep(30)"]
        before = time.monotonic()
        with self.assertRaises(TimeoutError):
            with OWNER.Process(child, self.environment, self.root, self.root / "stalled.log",
                               before + 0.15) as running:
                process = running.process
                running.until(lambda _line: False)
        self.assertLess(time.monotonic() - before, 3)
        self.assertIsNotNone(process.poll())
        self.assertIsNone(sibling.poll())

    def test_running_executable_is_checked_not_an_argument_naming_expected_binary(self):
        child = subprocess.Popen([sys.executable, "-c", "import time;time.sleep(30)", "/bin/sh"],
                                 start_new_session=True)
        self.addCleanup(OWNER.stop_group, child)
        with self.assertRaisesRegex(ValueError, "executable differs"):
            OWNER.process_identity(child, Path("/bin/sh"))
        self.assertEqual(str(Path(sys.executable).resolve()),
                         OWNER.process_identity(child, Path(sys.executable))["executable"])

    def test_engine_manager_requires_exact_config_and_unshadowed_cwd(self):
        catalog = [{"name": "Stockfish (official)", "command": "/bin/sf", "protocol": "uci"},
                   {"name": "Laplace (substrate)", "command": "/bin/laplace-uci", "protocol": "uci"}]
        entries = [{**row, "workingDirectory": str(self.root)} for row in catalog]
        self.assertEqual(entries, OWNER.configured_engines(entries, catalog, self.root))
        OWNER.enumeration([row["name"] for row in entries], entries, self.root)
        with self.assertRaisesRegex(ValueError, "exact installed"):
            OWNER.enumeration(["Stockfish (official)"], entries, self.root)
        (self.root / "engines.json").write_text("[]")
        with self.assertRaisesRegex(ValueError, "shadow"):
            OWNER.enumeration([row["name"] for row in entries], entries, self.root)
        changed = [{**row} for row in entries]
        changed[1]["command"] = "/bin/other"
        with self.assertRaisesRegex(ValueError, "differs from installed"):
            OWNER.configured_engines(changed, catalog, self.root)

    def test_wall_signal_cleanup_restores_signal_owners_and_reaps_child(self):
        old_alarm, old_term = signal.getsignal(signal.SIGALRM), signal.getsignal(signal.SIGTERM)
        with self.assertRaises(TimeoutError):
            with OWNER.wall_limit(0.15):
                with OWNER.Process([sys.executable, "-c",
                                    "import signal,time;signal.signal(signal.SIGTERM,signal.SIG_IGN);time.sleep(30)"],
                                   self.environment, self.root, self.root / "signal.log",
                                   time.monotonic() + 30) as running:
                    process = running.process
                    running.until(lambda _line: False)
        self.assertIsNotNone(process.poll())
        self.assertEqual(old_alarm, signal.getsignal(signal.SIGALRM))
        self.assertEqual(old_term, signal.getsignal(signal.SIGTERM))
        self.assertEqual((0.0, 0.0), signal.getitimer(signal.ITIMER_REAL))


if __name__ == "__main__":
    unittest.main()
