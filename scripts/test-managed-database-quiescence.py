#!/usr/bin/env python3
"""Exercise installed helper composition without sudo, services or database writes."""
from __future__ import annotations
import importlib.util
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
spec = importlib.util.spec_from_file_location("quiescence", ROOT / "scripts/quiesce-managed-database.py")
Q = importlib.util.module_from_spec(spec)
spec.loader.exec_module(Q)


class QuiescenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR", "/build/laplace/work"))
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.state, self.proc, self.receipt = [self.base / x for x in ("state", "proc", "receipt")]
        for path in (self.state, self.proc, self.receipt): path.mkdir()
        self.states = {name: {"unit": "laplace-" + name + ".service", "load_state": "loaded",
            "active_state": "active" if name != "lichess" else "inactive", "main_pid": 100 + i if name != "lichess" else 0,
            "operator_stopped": False} for i, name in enumerate(("api", "mcp", "lichess"))}
        self.calls = []
        self.fail_stop = self.fail_start = None
        self.unknown_begin = self.change_transaction = False
        self.boundary = Q.Quiescence(self.receipt, state=self.state, proc=self.proc, execute=self.execute)
        # Fixtures use the current uid; production still requires a root-owned file.
        original = Q.transaction_identity
        def identity(state):
            path = state / "transaction.json"
            if not path.exists(): return None
            v=path.stat()
            return dict(device=v.st_dev,inode=v.st_ino,size=v.st_size,modified_ns=v.st_mtime_ns,changed_ns=v.st_ctime_ns)
        self.patch = patch.object(Q, "transaction_identity", side_effect=identity)
        self.patch.start(); self.addCleanup(self.patch.stop)

    def execute(self, argv):
        self.calls.append(argv)
        if argv[:2] == ["/usr/bin/systemctl", "show"]:
            s=self.states["api"]
            return f'LoadState={s["load_state"]}\nActiveState={s["active_state"]}\nMainPID={s["main_pid"]}\n'
        if Q.DEPLOY in argv:
            action=argv[-1]; path=self.state / "transaction.json"
            if action == "begin":
                self.assertTrue((self.receipt / "prior-services.json").is_file())
                self.assertTrue((self.receipt / "begin-submission.json").is_file())
                if path.exists(): raise subprocess.CalledProcessError(1, argv)
                path.write_text('retained installed transaction')
                if self.unknown_begin: raise subprocess.TimeoutExpired(argv,75)
            elif action == "commit":
                self.assertEqual(self.boundary.transaction,Q.transaction_identity(self.state))
                path.rename(self.state / "committed.json")
            else: self.fail("unexpected managed verb")
            return ""
        if Q.CONTROL in argv:
            name, action=argv[-2:]
            if action == "status": return json.dumps(self.states[name])
            if (self.state / "transaction.json").exists(): raise subprocess.CalledProcessError(3,argv)
        else:
            self.assertEqual(["sudo","-n","systemctl"],argv[:3]);action=argv[-2];name="api"
        s=self.states[name]
        if action == "stop":
            self.assertTrue((self.receipt / ("stop-"+name+"-submission.json")).is_file())
            if name == self.fail_stop: raise subprocess.CalledProcessError(1,argv)
            s.update(active_state="inactive",main_pid=0)
            if name != "api": s["operator_stopped"]=True
        elif action == "start":
            if name == self.fail_start: raise subprocess.CalledProcessError(1,argv)
            s.update(active_state="active",main_pid=500)
            if name != "api": s["operator_stopped"]=False
        else:self.fail("unexpected service verb")
        return json.dumps(s)

    def test_existing_helpers_quiesce_and_restore_only_prior_active_services(self):
        self.boundary.enter()
        self.assertEqual({"inactive"},{s["active_state"] for s in self.states.values()})
        self.assertFalse(self.states["lichess"]["operator_stopped"])
        self.assertTrue((self.receipt / "quiescence-confirmed.json").is_file())
        self.boundary.restore()
        self.assertEqual(["active","active","inactive"],[self.states[x]["active_state"] for x in ("api","mcp","lichess")])
        self.assertFalse(any(s["operator_stopped"] for s in self.states.values()))
        self.assertTrue((self.receipt / "restored.json").is_file())
        self.assertFalse((self.state / "transaction.json").exists())
        self.assertFalse(any("bootstrap" in c or "quiesce-database" in c for c in self.calls))

    def test_operator_stop_is_preserved(self):
        self.states["mcp"]["operator_stopped"]=True
        self.boundary.enter();self.boundary.restore()
        self.assertTrue(self.states["mcp"]["operator_stopped"])
        self.assertEqual("inactive",self.states["mcp"]["active_state"])

    def test_preexisting_transaction_is_never_claimed_or_committed(self):
        (self.state / "transaction.json").write_text("another deployment")
        with self.assertRaisesRegex(ValueError,"existing managed deployment"): self.boundary.enter()
        with self.assertRaisesRegex(ValueError,"another deployment"): self.boundary.restore()
        self.assertEqual([],self.calls)

    def test_lost_begin_acknowledgement_never_commits_or_clears_stop_intent(self):
        self.unknown_begin=True
        with self.assertRaises(subprocess.TimeoutExpired):self.boundary.enter()
        with self.assertRaisesRegex(ValueError,"acknowledgement is unknown"):self.boundary.restore()
        self.assertTrue((self.state / "transaction.json").exists())
        self.assertTrue(self.states["mcp"]["operator_stopped"])
        self.assertFalse(any(c[-1]=="commit" for c in self.calls))

    def test_changed_root_transaction_is_never_committed(self):
        self.boundary.enter()
        (self.state / "transaction.json").write_text("different transaction")
        with self.assertRaisesRegex(ValueError,"identity changed"):self.boundary.restore()
        self.assertFalse(any(c[-1]=="commit" for c in self.calls))

    def test_partial_stop_failure_restores_original_active_state(self):
        self.fail_stop="mcp"
        with self.assertRaises(subprocess.CalledProcessError):self.boundary.enter()
        self.boundary.restore()
        self.assertEqual("active",self.states["api"]["active_state"])
        self.assertEqual("active",self.states["mcp"]["active_state"])
        self.assertFalse((self.state / "transaction.json").exists())

    def test_restore_failure_retains_prior_state_receipt(self):
        self.boundary.enter();self.fail_start="mcp"
        with self.assertRaisesRegex(ValueError,"restoration failed: mcp"):self.boundary.restore()
        self.assertTrue((self.receipt / "prior-services.json").is_file())
        self.assertFalse((self.receipt / "restored.json").exists())

    def process(self,pid,argv,env):
        p=self.proc / str(pid);p.mkdir()
        (p/"cmdline").write_bytes(b"\0".join(a.encode() for a in argv)+b"\0")
        (p/"environ").write_bytes(b"\0".join((k+"="+v).encode() for k,v in env.items())+b"\0")

    def test_disposable_database_process_is_independently_excluded(self):
        self.process(201,["dotnet","Laplace.Cli.dll","ingest"],{"LAPLACE_DB":"Database=laplace_pr_42","PGDATABASE":"laplace"})
        self.boundary.enter();self.boundary.restore()

    def test_other_local_cluster_is_excluded_with_environment_or_connection_coordinates(self):
        self.process(203,["Laplace.Cli"],{"PGDATABASE":"laplace","PGHOST":"/other/socket","PGPORT":"55432"})
        self.process(204,["Laplace.Cli"],{"LAPLACE_DB":"Server=/refactor/socket;Initial Catalog=laplace;Port=55433"})
        self.boundary.enter();self.boundary.restore()

    def test_unresolved_or_canonical_standalone_writer_fails_without_secrets(self):
        self.process(202,["Laplace.Cli","chess","lichess","--token","SECRET"],
                     {"LAPLACE_DB":"Database=laplace;Password=SECRET","PGDATABASE":"different"})
        with self.assertRaisesRegex(ValueError,"standalone database writers remain") as e:self.boundary.enter()
        self.assertNotIn("SECRET",str(e.exception))
        self.assertFalse(any("stop" in c or "begin" in c for c in self.calls))

    def test_dotnet_exec_is_a_writer_and_incidental_filenames_are_not(self):
        self.assertEqual("Laplace.Cli.dll", Q.laplace_component([b"dotnet",b"exec",b"/app/Laplace.Cli.dll",b"measure-lane"]))
        self.assertIsNone(Q.laplace_component([b"python3",b"tests.py",b"Laplace.Cli.dll"]))
        self.process(205,["dotnet","exec","Laplace.Cli.dll","ingest"],{"PGDATABASE":"laplace"})
        with self.assertRaisesRegex(ValueError,"standalone database writers remain"):self.boundary.enter()

    def test_receipt_sync_failure_prevents_first_stop(self):
        with patch.object(Q,"write_new_json",side_effect=OSError("storage failure")):
            with self.assertRaises(OSError):self.boundary.enter()
        self.assertFalse(any("stop" in c or "begin" in c for c in self.calls))

class ProducerGenerationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR", "/build/laplace/work"))
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.prefix = self.root / "installed"
        self.app = self.prefix / "app"
        self.app.mkdir(parents=True)
        (self.root / "build").mkdir()
        self.verified = self.root / "build/.applications-verified.json"
        self.verified.write_text('{"native_runtime":"verified"}')
        (self.app / "Laplace.Endpoints.OpenAICompat.dll").write_bytes(b"api-generation")
        for role, name in (("mcp", "Mcp"), ("lichess", "Lichess")):
            directory = self.app / "releases/current" / role
            directory.mkdir(parents=True)
            executable = directory / ("Laplace.Endpoints." + name)
            executable.write_bytes(b"apphost")
            Path(str(executable) + ".dll").write_bytes(role.encode() + b"-generation")
            (directory / "Laplace.Chess.dll").write_bytes(b"corrected-chess-producer")
            (self.app / ("laplace-" + role)).symlink_to(executable)
        env = patch.dict(os.environ, {"LAPLACE_REPAIR_PUBLISHED_SOURCE": "a" * 40,
                                      "LAPLACE_INSTALL_PREFIX": str(self.prefix)})
        env.start(); self.addCleanup(env.stop)
        git = patch.object(Q.subprocess, "check_output", return_value="a" * 40 + "\n")
        git.start(); self.addCleanup(git.stop)

    def test_resolved_activated_assemblies_and_verification_bytes_are_receipted(self):
        generation = Q.published_application_generation(root=self.root)
        self.assertEqual("a" * 40, generation["source_sha"])
        self.assertEqual(hashlib.sha256(self.verified.read_bytes()).hexdigest(),
                         generation["application_verification_sha256"])
        selected = generation["assemblies"]["lichess/Laplace.Chess.dll"]
        path = Path(selected["path"])
        self.assertEqual(len(path.read_bytes()), selected["bytes"])
        self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), selected["sha256"])
        path.write_bytes(b"different-producer")
        self.assertNotEqual(generation, Q.published_application_generation(root=self.root))

    def test_wrong_source_or_missing_publication_evidence_refuses_generation_claim(self):
        with patch.dict(os.environ, {"LAPLACE_REPAIR_PUBLISHED_SOURCE": "b" * 40}):
            with self.assertRaisesRegex(ValueError, "source does not match"):
                Q.published_application_generation(root=self.root)
        self.verified.unlink()
        with self.assertRaisesRegex(ValueError, "verification receipt is missing"):
            Q.published_application_generation(root=self.root)

    def test_missing_entry_assembly_refuses_generation_claim(self):
        (self.app / "Laplace.Endpoints.OpenAICompat.dll").unlink()
        with self.assertRaisesRegex(ValueError, "entry assembly is missing: api"):
            Q.published_application_generation(root=self.root)

    def test_prepublication_maintenance_does_not_claim_corrected_producer(self):
        with patch.dict(os.environ, {"LAPLACE_REPAIR_PUBLISHED_SOURCE": ""}):
            self.assertIsNone(Q.published_application_generation(root=self.root))


if __name__ == "__main__":unittest.main()
