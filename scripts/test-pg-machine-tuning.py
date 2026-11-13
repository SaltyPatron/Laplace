#!/usr/bin/env python3
"""Exercise the real tuning validator against a disposable socket-only PostgreSQL.

No sudo, production connection, systemd action or host configuration change.
The installed PostgreSQL binaries must be available; missing dependencies fail CI.
"""
import os
from pathlib import Path
import shutil
import signal
import subprocess
import tempfile
import time
import unittest


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts/pg-machine-tuning.sh"
VARIABLES = {
    "PG_TUNE_SB": "shared_buffers", "PG_TUNE_ECS": "effective_cache_size",
    "PG_TUNE_MWM": "maintenance_work_mem", "PG_TUNE_WM": "work_mem",
    "PG_TUNE_MAX_WAL": "max_wal_size", "PG_TUNE_MIN_WAL": "min_wal_size",
    "PG_TUNE_MAXCONN": "max_connections", "PG_TUNE_RESERVED": "superuser_reserved_connections",
    "PG_TUNE_AVWM": "autovacuum_work_mem", "PG_TUNE_TEMPB": "temp_buffers",
    "PG_TUNE_CHECKPOINT": "checkpoint_timeout", "PG_TUNE_PDEG": "max_parallel_maintenance_workers",
    "PG_TUNE_IO_CONC": "effective_io_concurrency",
}


class TuningTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.env = {k: v for k, v in os.environ.items() if not k.startswith("PG")}
        cls.env["LC_ALL"] = "C"
        prefix = Path(os.environ.get("LAPLACE_PG_PREFIX", "/opt/laplace/pgsql-18")) / "bin"
        cls.bin = {name: str(prefix / name) if (prefix / name).is_file() else shutil.which(name)
                   for name in ("initdb", "pg_ctl", "psql")}
        if not all(cls.bin.values()):
            raise RuntimeError("PostgreSQL initdb, pg_ctl and psql are required")
        cls.temp = tempfile.TemporaryDirectory(prefix="laplace-tuning-test-")
        cls.addClassCleanup(cls.temp.cleanup)
        cls.base = Path(cls.temp.name)
        cls.data = cls.base / "data"
        cls.socket = cls.base / "socket"
        cls.socket.mkdir()
        subprocess.run([cls.bin["initdb"], "-D", str(cls.data), "-U", "tuning_test",
                        "--auth-local=trust", "--auth-host=reject", "--no-locale", "--no-sync"],
                       env=cls.env, check=True, capture_output=True, text=True, timeout=30)
        settings = {"listen_addresses": "", "unix_socket_directories": str(cls.socket),
                    "port": "5432", "shared_buffers": "65533kB", "huge_pages": "try",
                    "max_connections": "20", "hash_mem_multiplier": "1",
                    "synchronous_commit": "off", "wal_compression": "on",
                    "max_locks_per_transaction": "1024", "autovacuum_work_mem": "64MB",
                    "fsync": "off", "max_worker_processes": "8"}
        # Put desired values in this test cluster's config so subsequent reloads
        # do not compare initdb's defaults against command-line overrides.
        config = cls.data / "postgresql.conf"
        config.write_text(config.read_text() + "\n" + "\n".join(
            f"{k} = '{v}'" for k, v in settings.items()) + "\n")
        # Register stop before starting, so even a failed startup is cleaned up.
        cls.addClassCleanup(cls.stop)
        subprocess.run([cls.bin["pg_ctl"], "-D", str(cls.data), "-l", str(cls.base / "postgres.log"),
                        "-w", "-t", "20", "start"], env=cls.env,
                       check=True, capture_output=True, text=True, timeout=25)
        cls.psql = [cls.bin["psql"], "-X", "-w", "-h", str(cls.socket), "-p", "5432",
                    "-U", "tuning_test", "-d", "postgres", "-v", "ON_ERROR_STOP=1"]
        rows = cls.query("SELECT name,current_setting(name) FROM pg_settings ORDER BY name")
        live = dict(line.split("|", 1) for line in rows.splitlines())
        cls.expected = {var: live[name] for var, name in VARIABLES.items()}

    @classmethod
    def stop(cls):
        if not (cls.data / "postmaster.pid").is_file():
            return
        subprocess.run([cls.bin["pg_ctl"], "-D", str(cls.data), "-m", "immediate", "-w", "stop"],
                       env=cls.env, capture_output=True, timeout=20)

    @classmethod
    def query(cls, sql):
        return subprocess.run(cls.psql + ["-At", "-c", sql], env=cls.env, check=True,
                              capture_output=True, text=True, timeout=10).stdout.strip()

    def validate(self, expected=None, settings=None, override="", script=SCRIPT):
        env = dict(self.env, **self.expected)
        env.update(expected or {})
        options = {"default_transaction_read_only": "on", "statement_timeout": "5000"}
        options.update(settings or {})
        env["PGOPTIONS"] = " ".join(f"-c {k}={v}" for k, v in options.items())
        shell = ('set -euo pipefail; source "$1"; shift; '
                 'pg_load_expected_tuning() { :; }; PG_TUNE_PSQL=("$@"); '
                 + override + '\npg_validate_machine_tuning')
        return subprocess.run(["bash", "-c", shell, "tuning-test", str(script)] + self.psql,
                              env=env, capture_output=True, text=True, timeout=15)

    def assertPass(self, result):
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(18, result.stdout.count("✓"), result.stdout)

    def test_native_values_pass(self):
        self.assertPass(self.validate())

    def test_bootstrap_unaligned_shared_buffers(self):
        self.assertEqual("64MB", self.query("SHOW shared_buffers"))
        self.assertPass(self.validate({"PG_TUNE_SB": "65533kB"}))

    def test_reported_cache_failure_and_rounding_boundaries(self):
        for requested, normalized in [("65899578kB", "65899576kB"),
                                      ("32949789kB", "32949792kB"),
                                      ("65539kB", "64MB"),
                                      ("65540kB", "64MB"),
                                      ("65541kB", "65544kB"),
                                      ("65548kB", "65552kB")]:
            with self.subTest(requested=requested):
                self.assertEqual(normalized, self.query(
                    f"SELECT set_config('effective_cache_size','{requested}',false)"))
                self.assertPass(self.validate({"PG_TUNE_ECS": requested},
                                              {"effective_cache_size": requested}))

    def test_kilobyte_and_megabyte_units(self):
        self.assertPass(self.validate({"PG_TUNE_WM": "1025kB", "PG_TUNE_TEMPB": "1029kB"},
                                      {"work_mem": "1025kB", "temp_buffers": "1029kB"}))
        # 1,048,064kB is halfway between 1023 and 1024 MB: ties to even -> 1024.
        self.assertPass(self.validate({"PG_TUNE_MAX_WAL": "1048064kB"}))

    def test_whole_block_and_half_block_mismatches_fail(self):
        for requested in ("65545kB", "65541kB"):
            with self.subTest(requested=requested):
                result = self.validate({"PG_TUNE_ECS": requested}, {"effective_cache_size": "64MB"})
                self.assertNotEqual(0, result.returncode)
                self.assertIn("✗ effective_cache_size", result.stdout)

    def test_non_memory_mismatch_fails(self):
        result = self.validate({"PG_TUNE_MAXCONN": "21"})
        self.assertNotEqual(0, result.returncode)
        self.assertIn("✗ max_connections", result.stdout)

    def test_disabled_feature_fails(self):
        result = self.validate(settings={"wal_compression": "off"})
        self.assertNotEqual(0, result.returncode)
        self.assertIn("✗ wal_compression", result.stdout)

    def test_sql_error_fails_closed(self):
        result = self.validate({"PG_TUNE_SB": "invalid-memory"})
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Could not query live tuning", result.stdout)

    def test_failed_and_empty_query_fail_closed(self):
        for action in ("return 23", "return 0"):
            with self.subTest(action=action):
                result = self.validate(override='''pg_tune_psql() {
                  if [[ "$*" == *"SELECT count"* ]]; then echo 0; return; fi
                  cat >/dev/null; ''' + action + "; }")
                self.assertNotEqual(0, result.returncode)

    def test_pending_query_failure_fails_closed(self):
        result = self.validate(override='''pg_tune_psql() {
          if [[ "$*" == *"SELECT count"* ]]; then return 23; fi
          "${PG_TUNE_PSQL[@]}" "$@";
        }''')
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Could not query pending restarts", result.stdout)

    def test_pending_restart_fails_even_for_unlisted_setting(self):
        # Only the disposable postmaster's configuration is modified/reloaded.
        try:
            self.query("ALTER SYSTEM SET max_worker_processes = 9")
            self.query("SELECT pg_reload_conf()")
            deadline = time.monotonic() + 5
            while self.query("SELECT pending_restart FROM pg_settings WHERE name='max_worker_processes'") != "t":
                self.assertLess(time.monotonic(), deadline, "test cluster did not reload")
                time.sleep(0.05)
            result = self.validate()
            self.assertNotEqual(0, result.returncode)
            self.assertIn("setting(s) pending_restart", result.stdout)
        finally:
            self.query("ALTER SYSTEM RESET max_worker_processes")
            self.query("SELECT pg_reload_conf()")
            deadline = time.monotonic() + 5
            while self.query("SELECT count(*) FROM pg_settings WHERE pending_restart") != "0":
                self.assertLess(time.monotonic(), deadline, "test cluster did not clear restart flag")
                time.sleep(0.05)

    def test_deliberate_original_comparison_break_is_detected(self):
        source = SCRIPT.read_text()
        start = source.index("WHEN 'mem'     THEN")
        end = source.index("WHEN 'enabled'", start)
        broken = source[:start] + ("WHEN 'mem' THEN pg_size_bytes(current_setting(w.name)) "
                                  "= pg_size_bytes(w.expected)\n         ") + source[end:]
        # Deliberate mutation only in a disposable source copy, never the checkout.
        path = self.base / "broken-validator.sh"
        path.write_text(broken)
        result = self.validate({"PG_TUNE_SB": "65533kB"}, script=path)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("✗ shared_buffers", result.stdout)
        self.assertPass(self.validate({"PG_TUNE_SB": "65533kB"}))


if __name__ == "__main__":
    def cancelled(signum, _frame):
        raise SystemExit(128 + signum)

    signal.signal(signal.SIGTERM, cancelled)
    try:
        unittest.main(verbosity=2)
    finally:
        # unittest's normal class cleanup is not guaranteed on interruption.
        # Stop only our own temporary postmaster, including CI cancellation.
        if hasattr(TuningTests, "data"):
            TuningTests.stop()
        if hasattr(TuningTests, "temp"):
            TuningTests.temp.cleanup()
