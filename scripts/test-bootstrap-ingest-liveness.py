#!/usr/bin/env python3
"""Real PostgreSQL SQL/ownership controls; the fixture extension is synthetic.

The journal DDL and upgrade SQL are the repository's actual source. This does not
load native Laplace or claim installed-engine qualification. Install the files
emitted by --prepare-extension into the selected PostgreSQL extension directory,
then run as a non-root user with PG_BINDIR and an absolute permanent TMPDIR.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
HELPER = ROOT / "scripts/bootstrap-ingest-liveness.py"
SOURCES = (
    "extension/laplace_substrate/sql/schema/tables/ingest_run_journal.sql.in",
    "extension/laplace_substrate/sql/schema/tables/ingest_file_journal.sql.in",
    "extension/laplace_substrate/sql/functions/ops/ingest_runs.sql.in",
)
VERSION1 = "liveness_fixture_1"
VERSION2 = "liveness_fixture_2"


def prepare_extension(directory):
    """Emit packaging only; never writes installed PostgreSQL files or starts it."""
    directory = Path(directory)
    directory.mkdir(parents=True, exist_ok=True)
    raw = [(ROOT / path).read_bytes() for path in SOURCES]
    sql = [item.decode("utf-8").replace("@extschema@", "laplace") for item in raw]
    if any("@" in item or "MODULE_PATHNAME" in item for item in sql):
        raise ValueError("fixture source needs unsupported preprocessing")
    files = {
        "laplace_substrate.control":
            "comment = 'SYNTHETIC liveness SQL fixture; no native engine'\n"
            "default_version = 'liveness_fixture_1'\n"
            "relocatable = false\nschema = 'laplace'\nsuperuser = true\n",
        "laplace_substrate--liveness_fixture_1.sql":
            "-- SYNTHETIC extension packaging of actual journal DDL.\n"
            "CREATE SCHEMA ops;\nSET LOCAL search_path = laplace, pg_catalog, ops;\n"
            + sql[0] + "\n" + sql[1],
        "laplace_substrate--liveness_fixture_1--liveness_fixture_2.sql":
            "-- SYNTHETIC upgrade; exact canonical liveness SQL follows.\n" + sql[2],
        "liveness_fixture_foreign.control":
            "comment = 'SYNTHETIC conflicting membership fixture'\n"
            "default_version = '1'\nrelocatable = true\nsuperuser = true\n",
        "liveness_fixture_foreign--1.sql": "-- Deliberately empty synthetic extension.\n",
    }
    for name, content in files.items():
        (directory / name).write_text(content, encoding="utf-8")
    return {
        "scope": "synthetic extension packaging, actual repository SQL",
        "sources": {path: hashlib.sha256(raw[i]).hexdigest()
                    for i, path in enumerate(SOURCES)},
        "files": {name: hashlib.sha256(content.encode()).hexdigest()
                  for name, content in files.items()},
    }


def permanent_parent():
    value = os.environ.get("TMPDIR", "")
    parent = Path(value)
    if not value or not parent.is_absolute() or not parent.is_dir():
        raise RuntimeError("TMPDIR must be an existing absolute permanent directory")
    resolved = parent.resolve()
    if resolved == Path("/tmp") or Path("/tmp") in resolved.parents \
            or resolved == Path("/var/tmp") or Path("/var/tmp") in resolved.parents:
        raise RuntimeError("temporary filesystem fixture paths are not admitted")
    return resolved


class PostgreSqlLivenessTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if os.geteuid() == 0:
            raise RuntimeError("the PostgreSQL fixture must run as a non-root user")
        cls.bin = Path(os.environ["PG_BINDIR"]).resolve(strict=True)
        for name in ("initdb", "pg_ctl", "psql"):
            if not (cls.bin / name).is_file():
                raise RuntimeError(f"missing PostgreSQL fixture executable: {name}")
        cls.work = Path(tempfile.mkdtemp(prefix="laplace-postgres-test.",
                                       dir=permanent_parent()))
        cls.data = cls.work / "data"
        cls.socket = cls.work / "socket"
        cls.socket.mkdir(mode=0o700)
        if len(str(cls.socket).encode()) > 85:
            raise RuntimeError("use a shorter permanent TMPDIR for Unix sockets")
        cls.started = False
        # Cleanup is registered before initdb/start; stop succeeds before rmtree.
        cls.addClassCleanup(cls.cleanup)
        cls.env = os.environ.copy()
        for key in tuple(cls.env):
            if key.startswith("PG"):
                cls.env.pop(key)
        cls.env.update({"PGCONNECT_TIMEOUT": "5", "PGCLIENTENCODING": "UTF8"})
        cls.run_tool([cls.bin / "initdb", "-D", cls.data, "-U", "fixture_admin",
                      "--auth-local=trust", "--auth-host=reject", "--no-locale",
                      "--encoding=UTF8"])
        with (cls.data / "postgresql.conf").open("a", encoding="utf-8") as config:
            config.write("\nlisten_addresses = ''\nport = 55439\n")
            config.write("unix_socket_directories = '" +
                         str(cls.socket).replace("'", "''") + "'\n")
            config.write("max_connections = 12\nshared_buffers = '16MB'\n")
        cls.run_tool([cls.bin / "pg_ctl", "-D", cls.data, "-l",
                      cls.work / "postgres.log", "-w", "-t", "30", "start"])
        cls.started = True
        cls.sql("postgres", "CREATE ROLE liveness_owner SUPERUSER LOGIN; "
                "CREATE ROLE liveness_other;")
        print("Actual fixture server: " + cls.sql("postgres", "SELECT version();"),
              flush=True)

    @classmethod
    def run_tool(cls, argv, *, input=None, check=True, timeout=45):
        result = subprocess.run([str(item) for item in argv], input=input, text=True,
                                capture_output=True, env=cls.env, timeout=timeout)
        if check and result.returncode:
            raise AssertionError(f"command failed ({result.returncode}): {argv}\n"
                                 f"{result.stdout}\n{result.stderr}")
        return result

    @classmethod
    def cleanup(cls):
        # Even a timed-out startup can leave a postmaster: pg_ctl owns shutdown.
        if (cls.data / "postmaster.pid").exists():
            result = cls.run_tool([cls.bin / "pg_ctl", "-D", cls.data, "-w",
                                   "-t", "30", "-m", "immediate", "stop"],
                                  check=False, timeout=40)
            if result.returncode:
                raise RuntimeError("fixture PostgreSQL stop failed; retaining " +
                                   str(cls.work) + "\n" + result.stderr)
        shutil.rmtree(cls.work)

    @classmethod
    def sql(cls, database, sql):
        return cls.run_tool([cls.bin / "psql", "-X", "--no-password", "-v",
                             "ON_ERROR_STOP=1", "-h", cls.socket, "-p", "55439",
                             "-U", "fixture_admin", "-d", database, "-tAX",
                             "-f", "-"], input=sql).stdout.strip()

    def setUp(self):
        self.db = "liveness_" + self._testMethodName.lower()[:45]
        self.sql("postgres", f'CREATE DATABASE "{self.db}";')
        self.addCleanup(self.sql, "postgres", f'DROP DATABASE "{self.db}" WITH (FORCE);')
        self.sql(self.db, "SET ROLE liveness_owner; CREATE SCHEMA laplace; "
                 "CREATE EXTENSION laplace_substrate VERSION 'liveness_fixture_1';")

    def apply(self, *, action="apply", ok=True):
        env = self.env.copy()
        env["PGPORT"] = "55439"
        result = subprocess.run(
            [sys.executable, str(HELPER), action, "--database", self.db,
             "--host", str(self.socket), "--user", "fixture_admin",
             "--psql", str(self.bin / "psql")],
            text=True, capture_output=True, env=env, timeout=135)
        if ok:
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        else:
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        return result

    def state(self):
        return self.sql(self.db, r"""
SELECT jsonb_build_object(
 'extensions',(SELECT jsonb_agg(to_jsonb(e) ORDER BY e.oid) FROM pg_extension e),
 'schemas',(SELECT jsonb_agg(to_jsonb(n) ORDER BY n.oid) FROM pg_namespace n WHERE n.nspname IN ('laplace','ops')),
 'columns',(SELECT jsonb_agg(to_jsonb(a) ORDER BY a.attrelid,a.attnum) FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname IN ('laplace','ops')),
 'functions',(SELECT jsonb_agg(to_jsonb(p) ORDER BY p.oid) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname IN ('laplace','ops')),
 'dependencies',(SELECT jsonb_agg(to_jsonb(d) ORDER BY d.classid,d.objid,d.objsubid,d.refclassid,d.refobjid,d.refobjsubid,d.deptype) FROM pg_depend d WHERE (d.classid='pg_proc'::regclass AND d.objid IN (SELECT p.oid FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname IN ('laplace','ops'))) OR d.refclassid='pg_extension'::regclass),
 'triggers',(SELECT jsonb_agg(to_jsonb(t) ORDER BY t.oid) FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='laplace'),
 'runs',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r.run_id) FROM laplace.ingest_run_journal r),
 'files',(SELECT jsonb_agg(to_jsonb(f) ORDER BY f.run_id,f.file_label) FROM laplace.ingest_file_journal f));
""")

    def function_ids(self):
        return self.sql(self.db, """
SELECT jsonb_object_agg(p.proname,p.oid ORDER BY p.proname)
FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
WHERE n.nspname='ops' AND p.proname IN
('ingest_run_live','ingest_run_touch','ingest_file_touch_run',
 'ingest_reconcile_orphans','ingest_runs');""")

    def assert_membership(self):
        rows = json.loads(self.apply(action="probe").stdout.splitlines()[0])["observation"]
        expected = {"ops.ingest_run_live(uuid,interval)", "ops.ingest_run_touch()",
                    "ops.ingest_file_touch_run()", "ops.ingest_reconcile_orphans(interval)",
                    "ops.ingest_runs(integer)"}
        found = {item["name"] for item in rows["functions"] if item["name"] in expected}
        self.assertEqual(found, expected)
        for item in rows["functions"]:
            if item["name"] in expected:
                self.assertTrue(item["present"])
                self.assertEqual(item["owner"], "liveness_owner")
                self.assertEqual(item["extension"], "laplace_substrate")

    def test_apply_upgrade_reapply_preserves_function_identity_and_owner(self):
        self.apply()
        self.assert_membership()
        ids = self.function_ids()
        self.apply()
        self.assertEqual(self.function_ids(), ids)
        self.sql(self.db, "SET ROLE liveness_owner; "
                 "ALTER EXTENSION laplace_substrate UPDATE TO 'liveness_fixture_2';")
        self.assertEqual(self.function_ids(), ids)
        self.apply()
        self.assert_membership()
        self.assertEqual(self.function_ids(), ids)
        self.assertEqual(self.sql(self.db, "SELECT extversion FROM pg_extension "
                                  "WHERE extname='laplace_substrate';"), VERSION2)

    def test_probe_reads_without_catalog_or_journal_mutation(self):
        self.sql(self.db, "INSERT INTO laplace.ingest_run_journal "
                 "(run_id,source_name,layer) VALUES "
                 "('00000000-0000-0000-0000-000000000001','fixture',0);")
        before = self.state()
        result = self.apply(action="probe")
        self.assertEqual(len(result.stdout.splitlines()), 1)
        self.assertEqual(self.state(), before)
        self.apply()
        before = self.state()
        self.apply(action="probe")
        self.assertEqual(self.state(), before)

    def test_canonical_heartbeat_beacon_and_orphan_reconciliation(self):
        self.sql(self.db, """
INSERT INTO laplace.ingest_run_journal
(run_id,source_name,layer,started_at,status,files_done,files_total) VALUES
('00000000-0000-0000-0000-000000000001','fixture',0,now()-interval '1 day','running',0,0),
('00000000-0000-0000-0000-000000000002','fixture',0,now()-interval '1 day','failed',6,0);
""")
        self.apply()
        self.assertEqual(self.sql(self.db, """
SELECT ops.ingest_run_live('00000000-0000-0000-0000-000000000001');
SELECT count(*) FROM ops.ingest_reconcile_orphans(interval '90 seconds');
SELECT files_total FROM laplace.ingest_run_journal WHERE status='failed';
WITH changed AS (
 UPDATE laplace.ingest_run_journal SET status='cancelled',
 error='run did not reach completion: liveness lock absent'
 WHERE run_id='00000000-0000-0000-0000-000000000001' RETURNING 1)
SELECT count(*) FROM changed;"""), "t\n0\n6\n0")
        # The actual AFTER file trigger must refresh an old parent heartbeat.
        self.sql(self.db, """
INSERT INTO laplace.ingest_run_journal (run_id,source_name,layer,heartbeat_at)
VALUES ('00000000-0000-0000-0000-000000000004','fixture',0,now()-interval '5 minutes');
INSERT INTO laplace.ingest_file_journal(run_id,file_label,source_name)
VALUES ('00000000-0000-0000-0000-000000000004','refresh','fixture');
-- Insert file before its parent so the ordinary trigger cannot refresh this orphan.
INSERT INTO laplace.ingest_file_journal(run_id,file_label,source_name)
VALUES ('00000000-0000-0000-0000-000000000003','orphan','fixture');
INSERT INTO laplace.ingest_run_journal (run_id,source_name,layer,heartbeat_at)
VALUES ('00000000-0000-0000-0000-000000000003','fixture',0,now()-interval '5 minutes');
DO $proof$
BEGIN
 IF NOT ops.ingest_run_live('00000000-0000-0000-0000-000000000004') THEN
   RAISE EXCEPTION 'file update did not refresh parent heartbeat';
 END IF;
 PERFORM pg_advisory_lock(1280330827,
   hashtext('00000000-0000-0000-0000-000000000003'));
 IF NOT ops.ingest_run_live('00000000-0000-0000-0000-000000000003') THEN
   RAISE EXCEPTION 'actual advisory beacon was ignored';
 END IF;
 IF EXISTS (SELECT 1 FROM ops.ingest_reconcile_orphans(interval '90 seconds')) THEN
   RAISE EXCEPTION 'live heartbeat or beacon was reconciled';
 END IF;
 PERFORM pg_advisory_unlock(1280330827,
   hashtext('00000000-0000-0000-0000-000000000003'));
END
$proof$;
""")
        self.assertEqual(self.sql(self.db, """
SELECT count(*) FROM ops.ingest_reconcile_orphans(interval '90 seconds');
SELECT status FROM laplace.ingest_file_journal WHERE file_label='orphan';
SELECT count(*) FROM ops.ingest_runs(20);"""), "1\ncancelled\n4")

    def test_wrong_function_owner_refuses_without_mutation(self):
        self.sql(self.db, """
CREATE FUNCTION ops.ingest_run_live(uuid,interval) RETURNS boolean
LANGUAGE sql AS 'SELECT false';
ALTER FUNCTION ops.ingest_run_live(uuid,interval) OWNER TO liveness_other;
""")
        before = self.state()
        result = self.apply(ok=False)
        self.assertIn("unexpected existing function owner/membership", result.stderr)
        self.assertEqual(self.state(), before)

    def test_foreign_function_membership_refuses_without_mutation(self):
        self.sql(self.db, """
SET ROLE liveness_owner;
CREATE EXTENSION liveness_fixture_foreign;
CREATE FUNCTION ops.ingest_run_live(uuid,interval) RETURNS boolean
LANGUAGE sql AS 'SELECT false';
ALTER EXTENSION liveness_fixture_foreign ADD FUNCTION ops.ingest_run_live(uuid,interval);
""")
        before = self.state()
        result = self.apply(ok=False)
        self.assertIn("unexpected existing function owner/membership", result.stderr)
        self.assertEqual(self.state(), before)

    def test_late_canonical_failure_rolls_back_heartbeat_and_prior_data_update(self):
        # Ownership is allowed; incompatible return type fails only after canonical
        # ALTER TABLE and historical terminal-total repair have actually executed.
        self.sql(self.db, """
SET ROLE liveness_owner;
CREATE FUNCTION ops.ingest_run_live(uuid,interval) RETURNS integer
LANGUAGE sql AS 'SELECT 0';
INSERT INTO laplace.ingest_run_journal
(run_id,source_name,layer,status,files_done,files_total)
VALUES ('00000000-0000-0000-0000-000000000002','fixture',0,'failed',6,0);
""")
        before = self.state()
        result = self.apply(ok=False)
        self.assertIn("cannot change return type", result.stderr)
        self.assertEqual(self.state(), before)
        self.assertEqual(self.sql(self.db, "SELECT files_total FROM "
                                  "laplace.ingest_run_journal;"), "0")
        self.assertEqual(self.sql(self.db, "SELECT count(*) FROM pg_attribute "
                                  "WHERE attrelid='laplace.ingest_run_journal'::regclass "
                                  "AND attname='heartbeat_at' AND NOT attisdropped;"), "0")


class ProductInstallOrderingTests(unittest.TestCase):
    def test_actual_install_function_bootstraps_then_waits_and_stops_on_failure(self):
        text = (ROOT / "scripts/product-ci.sh").read_text(encoding="utf-8")
        match = re.search(r"(?m)^run_install\(\) \{\n.*?^\}", text, re.S)
        self.assertIsNotNone(match)
        with tempfile.TemporaryDirectory(prefix="ingest-install-", dir=permanent_parent()) as work:
            work = Path(work)
            scripts = work / "scripts"
            scripts.mkdir()
            (scripts / "bootstrap-ingest-liveness.py").write_text(
                "import json,os,sys\n"
                "with open(os.environ['EVENTS'],'a') as f: "
                "f.write(json.dumps(['bootstrap',*sys.argv[1:]])+'\\n')\n"
                "sys.exit(int(os.environ.get('BOOTSTRAP_STATUS','0')))\n",
                encoding="utf-8")
            for name in ("wait-for-quiet-substrate.sh", "pipeline.sh"):
                (scripts / name).write_text(
                    '#!/usr/bin/env bash\nprintf "%s\\n" "$0 $*" >> "$EVENTS"\n',
                    encoding="utf-8")
            for code in (0, 19):
                events = work / "events"
                events.unlink(missing_ok=True)
                env = os.environ.copy()
                env.update({"EVENTS": str(events), "PGDATABASE": "fixture_db",
                            "BOOTSTRAP_STATUS": str(code)})
                result = subprocess.run(["bash", "-c",
                                         "set -euo pipefail\n" + match.group(0) +
                                         "\nrun_install\n"], cwd=work, env=env,
                                        text=True, capture_output=True, timeout=10)
                self.assertEqual(result.returncode, code, result.stderr)
                lines = events.read_text().splitlines()
                self.assertEqual(json.loads(lines[0]),
                                 ["bootstrap", "apply", "--database", "fixture_db"])
                self.assertEqual(lines[1:],
                                 ["scripts/wait-for-quiet-substrate.sh fixture_db 180",
                                  "scripts/pipeline.sh install"] if code == 0 else [])


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--prepare-extension":
        print(json.dumps(prepare_extension(sys.argv[2]), sort_keys=True))
    else:
        unittest.main()
