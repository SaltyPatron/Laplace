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
        self.repairs = self.base / "repairs"
        self.repairs.mkdir()
        self.resources = Q.MaintenanceResources.create(self.receipt, repair_root=self.repairs,
            max_bytes=Q.MAX_BYTES, max_line_bytes=Q.MAX_LINE_BYTES, max_prior_bytes=Q.MAX_BYTES,
            max_current_readback_bytes=2 * Q.MAX_BYTES, timeout_seconds=Q.TIMEOUT_SECONDS)
        environment = patch.dict(os.environ, {Q.RESOURCE_ENV: str(self.resources.path)})
        environment.start(); self.addCleanup(environment.stop)
        self.states = {name: {"unit": "laplace-" + name + ".service", "load_state": "loaded",
            "active_state": "active" if name != "lichess" else "inactive", "main_pid": 100 + i if name != "lichess" else 0,
            "operator_stopped": False} for i, name in enumerate(("api", "mcp", "lichess"))}
        self.calls = []
        self.fail_stop = self.fail_start = None
        self.unknown_begin = self.change_transaction = False
        self.boundary = Q.Quiescence(self.receipt, state=self.state, proc=self.proc,
                                     repair_root=self.repairs, resources=self.resources, execute=self.execute)
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

    def repair_attempt(self, name, *, disposition="unknown", reconciles=None):
        directory = self.repairs / name
        directory.mkdir()
        context = {"kind":"context", "database":"fixture", "database_oid":"42",
                   "system_identifier":"123456", "transaction":"1000",
                   "prior_submission_reconciliation":reconciles or []}
        body = (json.dumps(context)+"\n"+json.dumps({"kind":"plan", "count":0,
                "native_input_count":0, "unresolved":0})+"\n").encode()
        (directory / "plan.jsonl").write_bytes(body)
        manifest = {"schema":"laplace.legacy-content-repair-plan/v1",
                    "plan_sha256":hashlib.sha256(body).hexdigest(), "plan_bytes":len(body), "planned_rows":0}
        (directory / "manifest.json").write_text(json.dumps(manifest))
        if disposition != "not-submitted":
            (directory / "submission.json").write_text(json.dumps({"plan_sha256":manifest["plan_sha256"]}))
        if disposition == "confirmed":
            (directory / "outcome.json").write_text(json.dumps({**manifest,
                "disposition":"commit-confirmed", "applied":{"count":0}}))
        self.boundary.record("repair-attempt-" + name, {"schema":"laplace.quiescence-repair-attempt/v1",
            "transaction_identity":self.boundary.transaction, "repair_directory":str(directory),
            "repair_process":{"pid":99999999,"start_ticks":1,"boot_id":"fixture"}})
        return directory

    def transaction_status(self, directory, status="committed", matches=True):
        manifest = json.loads((directory / "manifest.json").read_text())
        return {"receipt":str(directory / "plan.jsonl"), "plan_sha256":manifest["plan_sha256"],
                "transaction":"1000", "database_identity_matches":matches, "status":status}

    def test_unknown_database_commit_holds_writers_even_when_native_xid_finished(self):
        for status in ("committed", "aborted"):
            with self.subTest(status=status):
                if not self.boundary.begin_confirmed:
                    self.boundary.enter()
                    directory = self.repair_attempt("unknown")
                with patch.object(Q, "database_submission_statuses", return_value=[self.transaction_status(directory,status)]):
                    with self.assertRaisesRegex(ValueError, "exact database reconciliation"):
                        self.boundary.restore()
                self.assertTrue((self.state / "transaction.json").exists())
                self.assertFalse(any(c[-1] in ("commit","start") for c in self.calls))
                self.assertTrue(any(self.receipt.glob("database-quiescence-held-*.json")))

    def test_pre_submission_failure_and_verified_commit_allow_original_service_restore(self):
        self.boundary.enter()
        self.repair_attempt("pre-submit", disposition="not-submitted")
        self.repair_attempt("committed", disposition="confirmed")
        self.boundary.restore()
        self.assertTrue((self.receipt / "restored.json").exists())
        self.assertEqual(["active","active","inactive"], [self.states[name]["active_state"] for name in ("api","mcp","lichess")])

    def test_current_receipt_closure_and_restore_share_readback_without_using_prior_allowance(self):
        self.boundary.enter()
        target = self.repairs / "large-current"
        self.resources = Q.MaintenanceResources.create(self.receipt, repair_root=self.repairs,
            max_bytes=Q.MAX_BYTES, max_line_bytes=Q.MAX_LINE_BYTES, max_prior_bytes=1,
            max_current_readback_bytes=4096, timeout_seconds=Q.TIMEOUT_SECONDS)
        self.resources.bind_current(target)
        self.boundary.resources = self.resources
        directory = self.repair_attempt(target.name, disposition="confirmed")
        size = (directory / "plan.jsonl").stat().st_size
        recipe = Q.repair_module()
        child_resources = Q.MaintenanceResources(self.resources.path)
        recipe.close_reconciled_submissions(self.repairs, directory, max_bytes=Q.MAX_BYTES,
            budget=child_resources, current_budget=child_resources, deadline=child_resources.deadline)
        self.assertEqual(size, self.resources.usage()["current_bytes_read"])
        self.boundary.restore()
        usage = self.resources.usage()
        self.assertEqual(2 * size, usage["current_bytes_read"])
        self.assertEqual(0, usage["prior_bytes_read"])
        self.assertTrue((self.receipt / "restored.json").exists())

    def test_wrapper_discovery_and_status_reauthentication_share_aggregate_history_budget(self):
        self.boundary.enter()
        old = self.repair_attempt("old")
        size = (old / "plan.jsonl").stat().st_size
        self.resources = Q.MaintenanceResources.create(self.receipt, repair_root=self.repairs,
            max_bytes=Q.MAX_BYTES, max_line_bytes=Q.MAX_LINE_BYTES, max_prior_bytes=2 * size - 1,
            max_current_readback_bytes=4096, timeout_seconds=Q.TIMEOUT_SECONDS)
        self.boundary.resources = self.resources
        # Discovery fits; full authentication for the native xid query does not.
        # The query and managed commit must both remain unsubmitted.
        with patch.object(Q.subprocess, "run") as database:
            with self.assertRaisesRegex(ValueError, "prior repair receipts exceed aggregate"):
                self.boundary.restore()
        database.assert_not_called()
        self.assertEqual(size, self.resources.usage()["prior_bytes_read"])
        self.assertFalse(any(c[-1] in ("commit", "start") for c in self.calls))
        self.assertTrue((self.state / "transaction.json").exists())

    def test_expired_shared_deadline_holds_writers_before_any_journal_scan(self):
        self.boundary.enter()
        self.repair_attempt("committed", disposition="confirmed")
        with patch.object(Q.time, "monotonic", return_value=self.resources.deadline), \
                patch.object(Q, "repair_module") as recipe:
            with self.assertRaisesRegex(TimeoutError, "maintenance deadline exceeded"):
                self.boundary.restore()
        recipe.assert_not_called()
        self.assertFalse(any(c[-1] in ("commit", "start") for c in self.calls))
        self.assertEqual(0, self.resources.usage()["prior_bytes_read"])

    def test_live_bound_repair_cannot_race_marker_absence_and_restart_writers(self):
        self.boundary.enter()
        self.repair_attempt("still-running", disposition="not-submitted")
        with patch.object(Q,"process_identity",return_value={"pid":99999999,"start_ticks":1,"boot_id":"fixture"}):
            with self.assertRaisesRegex(ValueError, "still running"):
                self.boundary.restore()
        self.assertFalse(any(c[-1] in ("commit","start") for c in self.calls))

    def test_resume_reuses_only_acknowledged_owner_then_requires_exact_reconciliation(self):
        generation = {"source_sha":"a"*40}
        with patch.object(Q,"published_application_generation",return_value=generation):
            self.boundary.enter()
            old = self.repair_attempt("unknown")
            with patch.object(Q,"database_submission_statuses",return_value=[self.transaction_status(old)]):
                resumed = Q.Quiescence(self.receipt,state=self.state,proc=self.proc,
                                       repair_root=self.repairs,resources=self.resources,execute=self.execute)
                resumed.resume()
                self.assertEqual(self.boundary.transaction,resumed.transaction)
                with self.assertRaisesRegex(ValueError,"exact database reconciliation"):
                    resumed.restore()
            self.boundary = resumed
            current = self.repair_attempt("reconciled", disposition="confirmed", reconciles=[{
                "receipt":str(old / "plan.jsonl"), "rows":0, "disposition":"zero-row-no-mutation"}])
            Q.repair_module().close_reconciled_submissions(self.repairs,current)
            resumed.restore()
        self.assertEqual(1,sum(c[-1]=="begin" for c in self.calls))
        self.assertEqual(1,sum(c[-1]=="commit" for c in self.calls))
        self.assertTrue((self.receipt / "restored.json").exists())

    def test_resume_blocks_in_progress_unavailable_wrong_cluster_and_changed_owner(self):
        generation = {"source_sha":"a"*40}
        with patch.object(Q,"published_application_generation",return_value=generation):
            self.boundary.enter()
            old = self.repair_attempt("unknown")
            for status,matches in (("in progress",True),(None,True),("committed",False)):
                with self.subTest(status=status,matches=matches), patch.object(Q,"database_submission_statuses",
                        return_value=[self.transaction_status(old,status,matches)]):
                    with self.assertRaisesRegex(ValueError,"in progress, unavailable or on another database"):
                        self.boundary.resume()
            (self.state / "transaction.json").write_text("different installed service transaction")
            with self.assertRaisesRegex(ValueError,"identity changed"):
                self.boundary.resume()
        self.assertFalse(any(c[-1] in ("commit","start") for c in self.calls))

    def test_resume_never_claims_unknown_service_begin_or_commit(self):
        self.unknown_begin=True
        with self.assertRaises(subprocess.TimeoutExpired):self.boundary.enter()
        with self.assertRaises(FileNotFoundError):self.boundary.resume()
        self.boundary.record("commit-submission", {"transaction_identity":Q.transaction_identity(self.state)})
        with self.assertRaisesRegex(ValueError,"unknown or completed service commit"):
            self.boundary.resume()
        self.assertFalse(any(c[-1] in ("commit","start") for c in self.calls))

    def test_native_xid_query_binds_exact_cluster_database_and_full_receipt(self):
        self.boundary.enter()
        old = self.repair_attempt("unknown")
        response = subprocess.CompletedProcess(["psql"],0,stdout=json.dumps([self.transaction_status(old)]))
        with patch.object(Q.subprocess,"run",return_value=response) as execute:
            statuses = Q.database_submission_statuses([old / "plan.jsonl"],command=["psql"])
        Q.require_finished_database_submissions(statuses)
        sql = execute.call_args.kwargs["input"]
        for fragment in ("BEGIN READ ONLY", "database=current_database()", "pg_control_system()",
                         "database_oid=(SELECT oid::text", "pg_xact_status(transaction::xid8)"):
            self.assertIn(fragment,sql)
        self.assertTrue(execute.call_args.kwargs["check"])
        (old / "plan.jsonl").write_bytes(b"changed full evidence")
        with patch.object(Q.subprocess,"run") as execute:
            with self.assertRaises(ValueError):Q.database_submission_statuses([old / "plan.jsonl"],command=["psql"])
            execute.assert_not_called()

    def test_native_xid_status_query_failure_keeps_writers_stopped(self):
        self.boundary.enter()
        self.repair_attempt("unknown")
        with patch.object(Q,"database_submission_statuses",side_effect=subprocess.TimeoutExpired("psql",1)):
            with self.assertRaises(subprocess.TimeoutExpired):self.boundary.restore()
        self.assertTrue((self.state / "transaction.json").exists())
        self.assertFalse(any(c[-1] in ("commit","start") for c in self.calls))

    def test_durable_attempt_binding_precedes_any_possible_repair_submission(self):
        self.boundary.enter()
        target = self.repairs / "not-yet-created"
        owner = {"pid":os.getpid(),"start_ticks":2,"boot_id":"fixture"}
        with patch.object(Q,"process_identity",return_value=owner):
            Q.bind_repair_attempt(self.receipt,target,source_sha="b"*40,state=self.state,proc=self.proc)
        binding = json.loads(next(self.receipt.glob("repair-attempt-*.json")).read_text())
        self.assertEqual(self.boundary.transaction,binding["transaction_identity"])
        self.assertEqual(str(target),binding["repair_directory"])
        self.assertFalse(target.exists())

    def test_rejected_resume_never_calls_restore_in_main_finally(self):
        self.boundary.enter()
        command=["bash",str(ROOT / "scripts/repair-legacy-content-lifecycle.sh"),"laplace"]
        self.boundary.record("maintenance-command", {"repair_lifecycle":True,
            "argv_sha256":hashlib.sha256(json.dumps(command).encode()).hexdigest()})
        with patch.object(Q,"RECEIPTS",self.base), patch.object(Q,"Quiescence",return_value=self.boundary), \
                patch.object(self.boundary,"resume",side_effect=ValueError("unknown transaction")), \
                patch.object(self.boundary,"restore") as restore, \
                patch.object(sys,"argv",["quiesce","--database","laplace","--",*command]):
            with self.assertRaisesRegex(ValueError,"unknown transaction"):Q.main()
        restore.assert_not_called()

    def test_unknown_repair_cannot_resume_through_an_unrelated_maintenance_command(self):
        self.boundary.enter()
        self.boundary.record("maintenance-command", {"repair_lifecycle":True,"argv_sha256":"fixed"})
        with patch.object(Q,"RECEIPTS",self.base), patch.object(Q,"Quiescence") as constructor, \
                patch.object(sys,"argv",["quiesce","--database","laplace","--","bash","unrelated.sh"]):
            with self.assertRaisesRegex(ValueError,"original measurement-lane lifecycle"):Q.main()
        constructor.assert_not_called()

    def test_failure_before_attempt_binding_still_holds_unresolved_prior_repair_estate(self):
        self.boundary.enter()
        old=self.repair_attempt("prior-unknown")
        next(self.receipt.glob("repair-attempt-*.json")).unlink()
        self.boundary.record("repair-estate", {"transaction_identity":self.boundary.transaction,
            "repair_root":str(self.repairs)})
        with patch.object(Q,"database_submission_statuses",return_value=[self.transaction_status(old)]):
            with self.assertRaisesRegex(ValueError,"exact database reconciliation"):
                self.boundary.restore()
        self.assertTrue((self.state / "transaction.json").exists())

    def test_resume_preflight_with_no_held_receipt_does_not_start_maintenance(self):
        command=["bash",str(ROOT / "scripts/repair-legacy-content-lifecycle.sh"),"laplace"]
        with patch.object(Q,"RECEIPTS",self.base), patch.object(Q,"Quiescence") as constructor, \
                patch.object(sys,"argv",["quiesce","--database","laplace","--resume-if-needed","--",*command]):
            self.assertEqual(0,Q.main())
        constructor.assert_not_called()

class MaintenanceResourceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR", "/build/laplace/work"))
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.repairs = self.base / "repairs"
        self.repairs.mkdir()
        self.resources = Q.MaintenanceResources.create(self.base, repair_root=self.repairs,
            max_bytes=1000, max_line_bytes=1000, max_prior_bytes=300,
            max_current_readback_bytes=200, timeout_seconds=180)
        self.old = self.repairs / "old"
        self.old.mkdir()
        self.current = self.repairs / "current"

    def test_independent_child_process_and_parent_share_durable_counters(self):
        self.resources.bind_current(self.current)
        self.current.mkdir()
        self.resources.reserve(150, directory=self.old)
        code = ("import importlib.util, pathlib, sys; "
                "sys.path.insert(0, sys.argv[1]); "
                "spec=importlib.util.spec_from_file_location('q', pathlib.Path(sys.argv[1])/'quiesce-managed-database.py'); "
                "q=importlib.util.module_from_spec(spec); spec.loader.exec_module(q); "
                "r=q.MaintenanceResources(pathlib.Path(sys.argv[2])); "
                "r.reserve(100, directory=pathlib.Path(sys.argv[3])); "
                "r.reserve(50, directory=pathlib.Path(sys.argv[4]))")
        result = subprocess.run([sys.executable, "-c", code, str(ROOT / "scripts"),
            str(self.resources.path), str(self.old), str(self.current)], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        usage = self.resources.usage()
        self.assertEqual((250, 50, 3), (usage["prior_bytes_read"], usage["current_bytes_read"], usage["reservations"]))
        with self.assertRaisesRegex(ValueError, "prior repair receipts exceed aggregate"):
            self.resources.reserve(51, directory=self.old)
        self.assertEqual(usage, self.resources.usage())
        self.resources.reserve(150, directory=self.current)
        with self.assertRaisesRegex(ValueError, "current receipt readback exceed aggregate"):
            Q.MaintenanceResources(self.resources.path).reserve(1, directory=self.current)

    def test_inherited_limits_and_original_deadline_must_match_without_refresh(self):
        arguments = dict(receipt_root=self.repairs, max_bytes=1000, max_line_bytes=1000,
                         max_prior_bytes=300, timeout_seconds=180)
        with patch.dict(os.environ, {Q.RESOURCE_ENV: str(self.resources.path)}):
            child = Q.inherited_repair_resources(self.base, **arguments)
            self.assertEqual(self.resources.deadline, child.deadline)
            for field in ("max_bytes", "max_line_bytes", "max_prior_bytes", "timeout_seconds"):
                with self.subTest(field=field), self.assertRaisesRegex(ValueError, "bound differs"):
                    Q.inherited_repair_resources(self.base, **(arguments | {field: arguments[field] + 1}))
            with patch.object(Q.time, "monotonic", return_value=self.resources.deadline):
                with self.assertRaisesRegex(TimeoutError, "maintenance deadline exceeded"):
                    Q.inherited_repair_resources(self.base, **arguments)
        self.assertEqual(0, self.resources.usage()["reservations"])

    def test_old_or_different_directory_cannot_be_relabelled_as_current(self):
        with self.assertRaisesRegex(ValueError, "already exists"):
            self.resources.bind_current(self.old)
        self.resources.bind_current(self.current)
        with self.assertRaisesRegex(ValueError, "different current"):
            self.resources.bind_current(self.repairs / "second")
        with self.assertRaisesRegex(ValueError, "admitted estate"):
            self.resources.reserve(1, directory=self.base)
        self.assertEqual(str(self.current), self.resources.usage()["current_receipt"])

    def test_failed_durable_charge_cannot_grant_read_allowance(self):
        before = self.resources.usage()
        with patch.object(Q, "write_new_json", side_effect=OSError("durability unavailable")):
            with self.assertRaises(OSError):
                self.resources.reserve(100, directory=self.old)
        self.assertEqual(before, self.resources.usage())
        self.assertEqual([], list(self.base.glob("*-usage.json.*")))

    def test_readback_admission_measures_every_future_current_authentication(self):
        self.resources.bind_current(self.current)
        self.resources.reserve(10, directory=self.current)
        self.assertEqual(["complete current receipt readback exceeds aggregate byte bound"],
            self.resources.readback_rejections(self.current, measured_bytes=96, reads=2))
        receipt = Q.bounded_metadata(self.resources.path.with_name(
            self.resources.path.stem + "-current-readback-admission.json"))
        self.assertEqual((96, 2, 192, 10, 200), (receipt["measured_plan_bytes"], receipt["read_multiplicity"],
            receipt["required_readback_bytes"], receipt["already_consumed_bytes"], receipt["max_current_readback_bytes"]))
        self.assertEqual(10, self.resources.usage()["current_bytes_read"])
        self.assertLess(self.resources.usage_path.stat().st_size, Q.MAX_METADATA_BYTES)

    def test_readback_admission_accepts_exact_capacity_and_rejects_unbound_or_invalid_input(self):
        for size, reads in ((True, 2), (100, 0), (100, 1.5)):
            with self.subTest(size=size, reads=reads), self.assertRaises(ValueError):
                self.resources.readback_rejections(self.current, size, reads)
        with self.assertRaisesRegex(ValueError, "matching bound"):
            self.resources.readback_rejections(self.current, 100, 2)
        self.resources.bind_current(self.current)
        self.assertEqual([], self.resources.readback_rejections(self.current, 100, 2))

    def test_shared_resource_metadata_rejects_wrong_boot_symlinks_and_oversize(self):
        with patch.object(Q, "boot_identity", return_value="different-boot"):
            with self.assertRaisesRegex(ValueError, "boot identity"):
                Q.MaintenanceResources(self.resources.path)
        self.resources.usage_path.write_bytes(b" " * (Q.MAX_METADATA_BYTES + 1))
        with self.assertRaisesRegex(ValueError, "metadata exceeds byte bound"):
            self.resources.reserve(1, directory=self.old)
        self.resources.usage_path.unlink()
        self.resources.usage_path.symlink_to(self.resources.path)
        with self.assertRaisesRegex(ValueError, "cannot be a symlink"):
            self.resources.usage()

    def test_resource_metadata_rejects_fifo_without_waiting_for_a_writer(self):
        path = self.base / "fifo.json"
        os.mkfifo(path)
        with self.assertRaisesRegex(ValueError, "must be a regular file"):
            Q.bounded_metadata(path)


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

    def test_resume_checks_retained_producer_bytes_without_relabelling_its_source(self):
        generation=Q.published_application_generation(root=self.root)
        with patch.object(Q.subprocess,"check_output",return_value="b"*40+"\n"), \
                patch.dict(os.environ,{"LAPLACE_REPAIR_PUBLISHED_SOURCE":"b"*40}):
            observed=Q.published_application_generation(root=self.root,retained=generation)
        self.assertEqual(generation,observed)
        self.assertEqual("a"*40,observed["source_sha"])
        (self.app / "Laplace.Endpoints.OpenAICompat.dll").write_bytes(b"different activated producer")
        with self.assertRaisesRegex(ValueError,"producer generation changed"):
            Q.published_application_generation(root=self.root,retained=generation)

    def test_durable_original_verification_supports_resume_after_checkout_receipt_disappears(self):
        durable=self.root / "held-quiescence"
        durable.mkdir()
        original_bytes=self.verified.read_bytes()
        generation=Q.published_application_generation(root=self.root,retain_to=durable)
        archived=durable / "producer-application-verification.json"
        self.assertEqual(original_bytes,archived.read_bytes())
        self.verified.unlink()
        with patch.object(Q.subprocess,"check_output",return_value="b"*40+"\n"):
            self.assertEqual(generation,Q.published_application_generation(root=self.root,
                             retained=generation,verification_receipt=archived))
        archived.write_bytes(b"different original verification")
        with self.assertRaisesRegex(ValueError,"producer generation changed"):
            Q.published_application_generation(root=self.root,retained=generation,verification_receipt=archived)


class ProductRepairResumeOrderTests(unittest.TestCase):
    def test_product_retry_resumes_before_native_install_and_stops_on_ambiguity(self):
        with tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR","/build/laplace/work")) as temp:
            root=Path(temp);scripts=root / "scripts";scripts.mkdir()
            deploy=root / "deploy/linux";deploy.mkdir(parents=True)
            (scripts / "product-ci.sh").write_bytes((ROOT / "scripts/product-ci.sh").read_bytes())
            for relative,label in (("scripts/ci-policy.sh","policy"),("scripts/ci-deps.sh","deps"),
                ("scripts/pipeline.sh","pipeline"),("scripts/test-parallel.sh","tests"),
                ("scripts/wait-for-quiet-substrate.sh","quiet"),("deploy/linux/managed-publish.sh","managed")):
                (root / relative).write_text('printf "%s\\n" "'+label+':$*" >> "$TRACE"\n')
            (scripts / "quiesce-managed-database.py").write_text(
                'import os,sys\n'
                'resuming="--resume-if-needed" in sys.argv\n'
                'with open(os.environ["TRACE"],"a") as f:f.write("resume\\n" if resuming else "quiesce\\n")\n'
                'sys.exit(int(os.environ.get("RESUME_FAILURE","0")) if resuming else 0)\n')
            for fail in (False,True):
                trace=root / ("failure.log" if fail else "success.log")
                result=subprocess.run(["bash",str(scripts / "product-ci.sh"),"deploy"],capture_output=True,text=True,
                    env={**os.environ,"TRACE":str(trace),"RESUME_FAILURE":"1" if fail else "0",
                         "PGDATABASE":"laplace","LAPLACE_FRESH_DB":"1","LAPLACE_RESTORE_FOUNDATION":"0"})
                events=trace.read_text().splitlines()
                if fail:
                    self.assertNotEqual(0,result.returncode)
                    self.assertEqual(["policy:","resume"],events)
                else:
                    self.assertEqual(0,result.returncode,result.stderr)
                    self.assertLess(events.index("resume"),events.index("pipeline:install"))
                    self.assertLess(events.index("resume"),events.index("managed:preflight"))


if __name__ == "__main__":unittest.main()
