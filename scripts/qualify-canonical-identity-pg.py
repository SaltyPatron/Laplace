#!/usr/bin/env python3
"""Qualify an already-installed candidate in a new, private PostgreSQL 18 cluster.

This operator neither builds/installs nor selects a product. The caller supplies
real isolated prefixes and their installation receipt. All SQL, fixture databases,
roles and extension-upgrade controls belong to the newly-created cluster/prefix.
The caller retains the shared-host reservation for the entire invocation.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import pwd
import re
import shutil
import signal
import subprocess
import sys
import time
import traceback
import xml.etree.ElementTree as ET

MANAGED_CLASSES = {
    "EntityInterpretationTransactionTests": 8,
    "EntityInterpretationDirectWriteTests": 3,
    "EntityInterpretationNativeReadTests": 2,
    "WorkingSetAtomicReplayTests": None,
    "PhysicalityObservationWriterTests": None,
    "PhysicalityViewReceiptTests": None,
    "LegacyBootstrapReconciliationTests": None,
    "IngestUnitCompletionWriterTests": 2,
    "ConsensusEvidencePeriodTests": None,
    "HighwayMaskRefreshConcurrencyTests": None,
}
NATIVE_TESTS = (
    "generated_stage_sink_pg_native_helpers",
    "physicality_descriptor_pg_native_helpers",
    "physicality_readback_pg_native_helpers",
)
# CTest uses its own POSIX-style regular expressions, not Python's (?:...) syntax.
CTEST_SELECTION_REGEX = ("^(regress_laplace_(geom|substrate)|" + "|".join(NATIVE_TESTS) + ")$")
LIVE_ROOTS = (
    Path("/opt/laplace/pgsql-18"),
    Path("/opt/laplace/lib/postgresql/18"),
    Path("/opt/laplace/share/postgresql/18"),
)


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def beneath(path, root):
    return path == root or root in path.parents


def resolved(value):
    return Path(value).resolve(strict=True)


def fingerprint(path):
    path = resolved(path)
    stat = path.stat()
    require(path.is_file(), f"not a regular file: {path}")
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return {"path": str(path), "sha256": digest.hexdigest(),
            "size": stat.st_size, "device": stat.st_dev, "inode": stat.st_ino}


def same_bytes(left, right):
    a, b = fingerprint(left), fingerprint(right)
    require(a["sha256"] == b["sha256"] and a["size"] == b["size"],
            f"file bytes differ: {left} versus {right}")
    return {"build": a, "installed_or_app_local": b}


def content_identity(info):
    return {key: info[key] for key in ("path", "sha256", "size")}


def validate_install_receipt(report, expected):
    require(report.get("schema") == "laplace.canonical-identity-isolated-install/v1",
            "unexpected candidate installation receipt schema")
    require(report.get("status") == "completed", "candidate installation receipt is not completed")
    # These are required typed bindings, not equivalence of installed and build
    # ELF bytes: the normal install step may legitimately rewrite RPATH.
    def compare(actual, wanted, label):
        if isinstance(wanted, dict):
            require(isinstance(actual, dict), "missing installation object: " + label)
            for key, value in wanted.items():
                require(key in actual, "missing installation field: " + label + "." + key)
                compare(actual[key], value, label + "." + key)
        else:
            require(actual == wanted, "installation identity differs: " + label)
    compare(report, expected, "installation")


def file_records(value):
    if isinstance(value, dict):
        if {"path", "sha256", "size", "device", "inode"}.issubset(value):
            yield value
        else:
            for child in value.values():
                yield from file_records(child)
    elif isinstance(value, list):
        for child in value:
            yield from file_records(child)


def cache_values(path):
    values = {}
    for line in path.read_text().splitlines():
        match = re.match(r"^([^/#][^:=]*):[^=]+=(.*)$", line)
        if match:
            values[match[1]] = match[2]
    return values


def proc_identity(pid):
    root = Path("/proc") / str(pid)
    stat = (root / "stat").read_text()
    fields = stat[stat.rfind(")") + 2:].split()
    return {"pid": pid, "ppid": int(fields[1]), "start_ticks": fields[19],
            "exe": str((root / "exe").resolve(strict=True))}


def mapped_files(pid, cache=None):
    entries = {}
    for line in (Path("/proc") / str(pid) / "maps").read_text().splitlines():
        fields = line.split(None, 5)
        if len(fields) != 6 or not fields[5].startswith("/"):
            continue
        pathname = fields[5]
        if not ("laplace" in Path(pathname).name or "postgis" in Path(pathname).name):
            continue
        require(not pathname.endswith(" (deleted)"), f"deleted mapped library: {line}")
        path = resolved(pathname)
        if str(path) in entries:
            continue
        stat = path.stat()
        key = (str(path), stat.st_dev, stat.st_ino, stat.st_size, stat.st_mtime_ns, stat.st_ctime_ns)
        if cache is not None and key in cache:
            info = cache[key]
        else:
            info = fingerprint(path)
            if cache is not None:
                cache[key] = info
        major, minor = (int(part, 16) for part in fields[3].split(":"))
        require(info["inode"] == int(fields[4]) and
                os.major(info["device"]) == major and os.minor(info["device"]) == minor,
                f"mapped inode no longer matches file: {path}")
        entries[str(path)] = info
    return entries


def extension_version(control):
    match = re.search(r"(?m)^\s*default_version\s*=\s*'([^']+)'\s*(?:#.*)?$", control.read_text())
    require(match is not None, f"missing default_version: {control}")
    return match[1]


def pg_quote(text):
    return "'" + str(text).replace("\\", "\\\\").replace("'", "''") + "'"


def expected_regressions(cmake):
    text = cmake.read_text()
    first = re.search(r"set\(REGRESS_TESTS\s+([^)]*)\)", text, re.S)
    require(first is not None, f"missing canonical regression declaration: {cmake}")
    names = first[1].split()
    for more in re.finditer(r"list\(APPEND REGRESS_TESTS\s+([^)]*)\)", text, re.S):
        names.extend(more[1].split())
    require(all(re.fullmatch(r"[a-z][a-z0-9_]*", name) for name in names),
            "unrecognized canonical regression declaration")
    return names


def validate_test_plan(document, source, build, bindir, pgprefix, stem):
    tests = {test["name"]: test for test in document["tests"]}
    required = set(NATIVE_TESTS)
    for suffix in ("geom", "substrate"):
        required.update(f"regress_{part}laplace_{suffix}" for part in ("setup_", "", "teardown_"))
    require(set(tests) == required, f"unexpected selected CTest set: {sorted(tests)}")
    summary = {}
    for suffix in ("geom", "substrate"):
        name = "regress_laplace_" + suffix
        command = tests[name]["command"]
        executable = resolved(command[0])
        require(beneath(executable, pgprefix) and executable.name == "pg_regress",
                f"pg_regress escapes isolated PG prefix: {executable}")
        expected_db = stem + "_" + suffix
        expected_input = source / "extension" / ("laplace_" + suffix) / "tests"
        expected_output = build / "extension" / ("laplace_" + suffix) / "tests" / "regress_output"
        for argument in ("--bindir=" + str(bindir), "--inputdir=" + str(expected_input),
                         "--outputdir=" + str(expected_output), "--dbname=" + expected_db,
                         "--user=laplace_admin", "--use-existing"):
            require(argument in command, f"{name} missing exact argument {argument}")
        require(not any(arg.startswith(("--host", "--port", "--temp-instance")) for arg in command[1:]),
                f"{name} overrides private cluster routing")
        cases = expected_regressions(expected_input / "CMakeLists.txt")
        actual_cases = [arg for arg in command[1:] if not arg.startswith("-")]
        require(actual_cases == cases, f"{name} does not run the entire canonical SQL suite")
        setup = tests["regress_setup_laplace_" + suffix]["command"]
        require(len(setup) == 3 and setup[1] == "-c", "unexpected registered setup shape")
        wanted = (f'"{bindir}/dropdb" -U laplace_admin --if-exists {expected_db} && '
                  f'"{bindir}/createdb" -U laplace_admin -O laplace_admin {expected_db}')
        require(setup[2] == wanted, f"unexpected registered fixture setup: {setup}")
        cleanup = tests["regress_teardown_laplace_" + suffix]["command"]
        require(cleanup == [str(bindir / "dropdb"), "-U", "laplace_admin", "--if-exists", expected_db],
                f"unexpected registered fixture cleanup: {cleanup}")
        summary[suffix] = {"database": expected_db, "cases": cases}
    for name in NATIVE_TESTS:
        require(beneath(resolved(tests[name]["command"][0]), build),
                f"native helper is outside candidate build: {name}")
    return summary


def validate_trx(path):
    tree = ET.parse(path)
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    methods = {}
    for test in tree.findall(".//t:TestDefinitions/t:UnitTest", ns):
        method = test.find("t:TestMethod", ns)
        require(method is not None, "TRX test has no method identity")
        methods[test.attrib["id"]] = method.attrib["className"].split(",")[0].rsplit(".", 1)[-1]
    results = tree.findall(".//t:Results/t:UnitTestResult", ns)
    require(results, "managed tests produced no individual results")
    counts = {name: 0 for name in MANAGED_CLASSES}
    for result in results:
        require(result.attrib.get("outcome") == "Passed", f"managed result did not pass: {result.attrib}")
        name = methods.get(result.attrib["testId"])
        require(name in counts, f"unexpected managed class: {name}")
        counts[name] += 1
    for name, expected in MANAGED_CLASSES.items():
        require(counts[name] > 0, f"managed class was not executed: {name}")
        if expected is not None:
            require(counts[name] == expected, f"managed case count changed: {name}: {counts[name]}")
    counter = tree.find(".//t:ResultSummary/t:Counters", ns)
    require(counter is not None, "TRX has no counters")
    for field in ("total", "executed", "passed"):
        require(int(counter.attrib[field]) == len(results), f"TRX {field} count differs")
    for field in ("failed", "error", "timeout", "aborted", "inconclusive", "notExecuted"):
        require(int(counter.attrib.get(field, "0")) == 0, f"TRX reports {field}")
    return {"total": len(results), "classes": counts, "trx": fingerprint(path)}


class Qualification:
    def __init__(self, args):
        self.args = args
        self.work = args.work.resolve()
        require(not self.work.exists(), f"work directory already exists: {self.work}")
        require(self.work.parent.is_dir(), "work directory parent must already exist")
        self.work.mkdir(mode=0o700)
        self.logs = self.work / "logs"
        self.logs.mkdir()
        self.started = time.monotonic()
        self.deadline = self.started + args.timeout
        self.receipt = {"schema": "laplace.canonical-identity-pg-qualification/v1",
                        "status": "running", "work": str(self.work), "commands": [], "phases": [],
                        "claim_scope": "fresh isolated candidate cluster; no production admission or activation"}
        self.postmaster = None
        self.postmaster_identity = None
        self.backend = None
        self.env = {}
        self.protected_before = None
        self.prepared_files = {}
        self.write()

    def write(self):
        self.receipt["elapsed_seconds"] = round(time.monotonic() - self.started, 3)
        temporary = self.work / "receipt.json.tmp"
        temporary.write_text(json.dumps(self.receipt, indent=2, sort_keys=True) + "\n")
        temporary.replace(self.work / "receipt.json")

    def phase(self, name):
        value = {"name": name, "elapsed_seconds": round(time.monotonic() - self.started, 3)}
        self.receipt["phases"].append(value)
        self.write()
        print("CANONICAL_IDENTITY_PG_PHASE " + json.dumps(value), flush=True)

    def remaining(self, limit):
        require(time.monotonic() < self.deadline, "qualification time limit exceeded")
        return min(limit, self.deadline - time.monotonic())

    def command(self, label, command, *, limit=600, allow_failure=False, env=None):
        require(re.fullmatch(r"[a-z0-9_-]+", label) is not None, "unsafe log label")
        argv = [str(item) for item in command]
        output = self.logs / (label + ".log")
        require(not output.exists(), f"duplicate command label: {label}")
        entry = {"name": label, "argv": argv, "log": str(output),
                 "started_elapsed_seconds": round(time.monotonic() - self.started, 3)}
        self.receipt["commands"].append(entry)
        self.write()
        with output.open("wb") as stream:
            process = subprocess.Popen(argv, env=self.env if env is None else env,
                                       cwd=self.work, stdout=stream, stderr=subprocess.STDOUT,
                                       start_new_session=True)
            try:
                process.wait(timeout=self.remaining(limit))
            except subprocess.TimeoutExpired:
                entry["timed_out"] = True
                raise
            finally:
                if process.poll() is None:
                    # This group was created by this exact invocation. No process discovery.
                    os.killpg(process.pid, signal.SIGTERM)
                    try:
                        process.wait(timeout=15)
                    except subprocess.TimeoutExpired:
                        os.killpg(process.pid, signal.SIGKILL)
                        process.wait(timeout=15)
                entry["returncode"] = process.returncode
                self.write()
        entry["output"] = fingerprint(output)
        self.write()
        require(allow_failure or process.returncode == 0,
                f"{label} failed ({process.returncode}); retained log: {output}")
        return output.read_text(errors="replace")

    def protected_snapshot(self):
        files = [fingerprint(path) for path in self.args.protected_file]
        processes = []
        for path in self.args.protected_pid_file:
            info = fingerprint(path)
            pid = int(resolved(path).read_text().splitlines()[0])
            processes.append({"pid_file": info, "process": proc_identity(pid)})
        return {"files": files, "processes": processes}

    def prepare(self):
        a = self.args
        require(sys.platform == "linux" and os.geteuid() != 0,
                "requires the ordinary non-root Linux build/runner account")
        require(re.fullmatch(r"[0-9a-f]{40}", a.source_commit), "source commit must be an exact SHA-1")
        require(re.fullmatch(r"laplace_[a-zA-Z0-9_]{1,42}", a.regress_db), "invalid private regression DB stem")
        require(1024 <= a.port <= 65535, "port must be between 1024 and 65535")
        self.source, self.build = resolved(a.source), resolved(a.build)
        self.pgprefix, self.nativeprefix = resolved(a.pg_prefix), resolved(a.native_prefix)
        for prefix in (self.pgprefix, self.nativeprefix):
            for live in LIVE_ROOTS:
                live = live.resolve()
                require(not beneath(prefix, live) and not beneath(live, prefix),
                        f"candidate prefix overlaps live preservation root: {prefix}, {live}")
        for root in (self.source, self.build, self.pgprefix, self.nativeprefix):
            require(not beneath(root, self.work), f"candidate input is inside new proof work: {root}")
        require(not any(char.isspace() for char in str(self.work)),
                "fixture admin commands require a whitespace-free work/socket path")
        self.socket = self.work / "socket"
        require(len(os.fsencode(str(self.socket / (".s.PGSQL." + str(a.port))))) < 104,
                "private Unix socket pathname is too long")
        self.protected_before = self.protected_snapshot()
        self.receipt["protected_before"] = self.protected_before
        self.receipt["preservation_observation_scope"] = (
            "caller-specified files and process incarnations" if
            self.args.protected_file or self.args.protected_pid_file else
            "no live preservation inputs supplied; external operator owns live-state observation")
        inherited = os.environ.copy()
        self.env = {key: value for key, value in inherited.items()
                    if not key.startswith(("PG", "LAPLACE_")) and key not in ("LD_PRELOAD", "LD_LIBRARY_PATH")}
        self.env["PATH"] = str(self.pgprefix / "bin") + os.pathsep + inherited.get("PATH", "")
        self.env["LC_ALL"] = "C"
        self.env["TZ"] = "UTC"
        head = self.command("source-head", ["git", "-C", self.source, "rev-parse", "HEAD"]).strip()
        require(head == a.source_commit, "source HEAD does not match exact requested commit")
        self.command("source-clean", ["git", "-C", self.source, "diff", "--exit-code", "HEAD", "--"])
        tree = self.command("source-tree", ["git", "-C", self.source, "rev-parse", "HEAD^{tree}"]).strip()
        cachepath = self.build / "CMakeCache.txt"
        cache = cache_values(cachepath)
        require(resolved(cache["CMAKE_HOME_DIRECTORY"]) == self.source, "CMake source directory mismatch")
        require(resolved(cache["CMAKE_INSTALL_PREFIX"]) == self.nativeprefix, "CMake install prefix mismatch")
        require(resolved(cache["LAPLACE_PG_PREFIX"]) == self.pgprefix, "CMake PG prefix mismatch")
        pgconfig = resolved(self.pgprefix / "bin" / "pg_config")
        require(resolved(cache["LAPLACE_PG_CONFIG"]) == pgconfig, "CMake pg_config mismatch")
        self.pgconfig = pgconfig
        self.pgdirs = {}
        for name in ("bindir", "pkglibdir", "sharedir", "libdir"):
            path = resolved(self.command("pgconfig-" + name, [pgconfig, "--" + name]).strip())
            require(beneath(path, self.pgprefix), f"pg_config --{name} escapes isolated prefix: {path}")
            self.pgdirs[name] = path
        self.bindir = self.pgdirs["bindir"]
        version = self.command("pgconfig-version", [pgconfig, "--version"]).strip()
        require(re.match(r"PostgreSQL 18(?:[.\s]|$)", version), f"requires actual PG18: {version}")
        for name in ("postgres", "initdb", "psql", "pg_isready", "dropdb", "createdb", "pg_ctl"):
            require(beneath(resolved(self.bindir / name), self.pgprefix),
                    f"PostgreSQL tool escapes isolated prefix: {name}")
        compiler_records = {}
        for language in ("C", "CXX"):
            compiler = resolved(cache["CMAKE_" + language + "_COMPILER"])
            compiler_version = self.command("compiler-" + language.lower(), [compiler, "--version"]).strip()
            require("Intel" in compiler_version or "oneAPI" in compiler_version,
                    f"candidate was not configured with genuine Intel oneAPI {language}: {compiler_version}")
            compiler_records[language] = {"file": fingerprint(compiler), "version": compiler_version}
        staged = cache.get("LAPLACE_INSTALL_STAGED", "").upper() in ("1", "ON", "TRUE", "YES")
        self.extlib = resolved(self.nativeprefix / "lib/postgresql/18" if staged else self.pgdirs["pkglibdir"])
        self.extshare = resolved(self.nativeprefix / "share/postgresql/18/extension" if staged
                                 else self.pgdirs["sharedir"] / "extension")
        for path in (self.extlib, self.extshare):
            require(beneath(path, self.nativeprefix) or beneath(path, self.pgprefix),
                    "candidate extension path escaped isolated prefixes")
        modulefile = self.build / "extension/laplace_substrate/laplace_execution_module.txt"
        self.module = modulefile.read_text().strip()
        require(re.fullmatch(r"laplace_execution_[0-9a-f]{16}", self.module), "invalid generated execution module")
        installation = {"execution_module": same_bytes(modulefile, self.extshare / modulefile.name)}
        self.versions = {}
        for name in ("laplace_geom", "laplace_substrate"):
            buildroot = self.build / "extension" / name
            control = self.extshare / (name + ".control")
            self.versions[name] = extension_version(control)
            files = [name + ".control", name + "--" + self.versions[name] + ".sql",
                     name + "_upgrade.sql"]
            installation[name] = {filename: same_bytes(buildroot / filename, self.extshare / filename)
                                  for filename in files}
        postgis_control = resolved(self.pgdirs["sharedir"] / "extension/postgis.control")
        require(beneath(postgis_control, self.pgprefix), "PostGIS control escapes isolated PG prefix")
        self.versions["postgis"] = extension_version(postgis_control)
        installation["postgis_control"] = fingerprint(postgis_control)
        postgis_sql = resolved(postgis_control.parent / ("postgis--" + self.versions["postgis"] + ".sql"))
        require(beneath(postgis_sql, self.pgprefix), "PostGIS install SQL escapes isolated PG prefix")
        installation["postgis_install_sql"] = fingerprint(postgis_sql)
        self.installed = {name: resolved(self.extlib / (name + ".so"))
                          for name in ("laplace_substrate", "laplace_geom", self.module)}
        self.installed["laplace_core"] = resolved(self.nativeprefix / "lib/liblaplace_core.so")
        self.installed["laplace_dynamics"] = resolved(self.nativeprefix / "lib/liblaplace_dynamics.so")
        for path in self.installed.values():
            require(beneath(path, self.nativeprefix) or beneath(path, self.pgprefix),
                    f"installed Laplace library escapes candidate prefixes: {path}")
        self.assembly = resolved(a.managed_assembly)
        require(self.assembly.name == "Laplace.Substrate.Tests.dll", "wrong managed test assembly")
        self.managed_core = resolved(self.assembly.parent / "liblaplace_core.so")
        managed_native = {}
        for name, component in (("laplace_core", "core"), ("laplace_dynamics", "dynamics")):
            managed_native[name] = same_bytes(self.build / "engine" / component / ("lib" + name + ".so"),
                                             self.assembly.parent / ("lib" + name + ".so"))
        runtime_dirs = [self.nativeprefix / "lib", self.pgdirs["libdir"]]
        runtime_dirs.extend(resolved(path) for path in a.runtime_library_directory)
        self.env["LD_LIBRARY_PATH"] = os.pathsep.join(str(path) for path in runtime_dirs)
        perfcaches = {"LAPLACE_PERFCACHE_BIN": resolved(a.perfcache),
                     "LAPLACE_HIGHWAY_PERFCACHE_BIN": resolved(a.highway_perfcache),
                     "LAPLACE_CHESS_POSITION_PERFCACHE_BIN": resolved(a.chess_position_perfcache)}
        # The canonical managed Highway resolver uses the sibling beside the selected
        # T0 blob; there is no Highway environment override in LaplaceInstall.
        require(resolved(perfcaches["LAPLACE_PERFCACHE_BIN"].parent / "laplace_highway_perfcache.bin") ==
                perfcaches["LAPLACE_HIGHWAY_PERFCACHE_BIN"],
                "managed Highway sibling differs from the selected PostgreSQL Highway perfcache")
        self.perfcaches = perfcaches
        self.env["LAPLACE_PERFCACHE_BIN"] = str(perfcaches["LAPLACE_PERFCACHE_BIN"])
        self.env.update({"PGHOST": str(self.socket), "PGPORT": str(a.port), "PGUSER": "laplace_admin",
                         "PGDATABASE": "postgres", "PGCONNECT_TIMEOUT": "10",
                         "PGPASSFILE": str(self.work / "no-password-file"),
                         "LAPLACE_DB": f"Host={self.socket};Port={a.port};Username=laplace_admin;Database=postgres;Search Path=laplace,public;Include Error Detail=true;Timeout=15",
                         "LAPLACE_ENGINE_BUILD": str(self.build / "engine"),
                         "LAPLACE_BUILD_ROOT": str(self.build), "LAPLACE_REGRESS_DB": a.regress_db})
        install_receipt = fingerprint(a.install_receipt)
        require(install_receipt["sha256"] == a.install_receipt_sha256,
                "supplied candidate installation receipt digest mismatch")
        shutil.copyfile(a.install_receipt, self.work / "candidate-install-receipt")
        self.receipt["inputs"] = {"source_commit": head, "source_tree": tree, "source": str(self.source),
                                 "cmake_cache": fingerprint(cachepath), "compilers": compiler_records,
                                 "pg_config": fingerprint(pgconfig), "postgres": fingerprint(self.bindir / "postgres"),
                                 "pg_version": version, "pg_directories": {k: str(v) for k, v in self.pgdirs.items()},
                                 "native_prefix": str(self.nativeprefix), "staged_install": staged,
                                 "extension_files": installation,
                                 "installed_libraries": {k: fingerprint(v) for k, v in self.installed.items()},
                                 "managed_assembly": fingerprint(self.assembly), "managed_native": managed_native,
                                 "perfcaches": {k: fingerprint(v) for k, v in perfcaches.items()},
                                 "installation_receipt": install_receipt}
        shutil.copyfile(cachepath, self.work / "CMakeCache.txt")
        inputs = self.receipt["inputs"]
        install_contract = {
            "source": {"commit": head, "tree": tree, "directory": str(self.source), "build_directory": str(self.build)},
            "cmake_cache": content_identity(inputs["cmake_cache"]),
            "pg_config": content_identity(inputs["pg_config"]),
            "installed_libraries": {key: content_identity(value)
                                    for key, value in inputs["installed_libraries"].items()},
            "build_libraries": {key: content_identity(value["build"]) for key, value in managed_native.items()},
            "managed_assembly": content_identity(inputs["managed_assembly"]),
            "managed_libraries": {key: content_identity(value["installed_or_app_local"])
                                  for key, value in managed_native.items()},
        }
        validate_install_receipt(json.loads(Path(a.install_receipt).read_text()), install_contract)
        self.receipt["validated_installation_binding"] = install_contract
        for record in file_records(inputs):
            previous = self.prepared_files.setdefault(record["path"], record)
            require(previous == record, "candidate file changed during preparation: " + record["path"])
        self.receipt["routing"] = {key: self.env[key] for key in
                                   ("PGHOST", "PGPORT", "PGUSER", "LAPLACE_DB", "LD_LIBRARY_PATH")}
        self.write()
        self.ctest = resolved(a.ctest)
        self.dotnet = resolved(a.dotnet)
        self.test_regex = CTEST_SELECTION_REGEX
        plan = json.loads(self.command("ctest-plan", [self.ctest, "--test-dir", self.build,
                                                     "--show-only=json-v1", "-R", self.test_regex]))
        (self.work / "ctest-plan.json").write_text(json.dumps(plan, indent=2) + "\n")
        self.receipt["registered_tests"] = validate_test_plan(plan, self.source, self.build, self.bindir,
                                                              self.pgprefix, a.regress_db)
        self.phase("isolated-inputs-verified")

    def sql(self, label, statement, database="postgres", limit=120):
        sqlpath = self.work / (label + ".sql")
        sqlpath.write_text(statement + "\n")
        return self.command(label, [self.bindir / "psql", "-X", "-A", "-t", "--no-password",
                                   "--set=ON_ERROR_STOP=1", "--dbname=" + database, "--file", sqlpath], limit=limit)

    def start(self):
        self.data = self.work / "data"
        self.socket.mkdir(mode=0o700)
        self.command("initdb", [self.bindir / "initdb", "-D", self.data, "-U", "laplace_admin",
                               "--encoding=UTF8", "--locale=C", "--auth-local=peer", "--auth-host=reject"])
        account = pwd.getpwuid(os.geteuid()).pw_name
        require(re.fullmatch(r"[A-Za-z0-9_.-]+", account), "unsupported peer account syntax")
        (self.data / "pg_hba.conf").write_text("local all all peer map=qualification\nhost all all 0.0.0.0/0 reject\nhost all all ::0/0 reject\n")
        (self.data / "pg_ident.conf").write_text("qualification " + account + " laplace_admin\n")
        settings = {
            "listen_addresses": "", "port": str(self.args.port), "unix_socket_directories": str(self.socket),
            "unix_socket_permissions": "0700", "shared_preload_libraries": "laplace_substrate",
            "dynamic_library_path": str(self.extlib) + ":$libdir",
            "extension_control_path": str(self.extshare.parent) + ":$system",
            "laplace_substrate.perfcache_path": self.env["LAPLACE_PERFCACHE_BIN"],
            "laplace_substrate.highway_perfcache_path": str(self.perfcaches["LAPLACE_HIGHWAY_PERFCACHE_BIN"]),
            "laplace_substrate.chess_position_perfcache_path": str(self.perfcaches["LAPLACE_CHESS_POSITION_PERFCACHE_BIN"]),
            "log_min_messages": "info",
        }
        with (self.data / "postgresql.conf").open("a") as stream:
            stream.write("\n# Isolated canonical identity qualification.\n")
            for key, value in settings.items():
                stream.write(key + " = " + pg_quote(value) + "\n")
        self.receipt["cluster_configuration"] = {"settings": settings,
                                                "files": [fingerprint(self.data / name) for name in
                                                          ("postgresql.conf", "pg_hba.conf", "pg_ident.conf")]}
        self.pglog = (self.logs / "postmaster.log").open("wb")
        self.postmaster = subprocess.Popen([str(self.bindir / "postgres"), "-D", str(self.data)],
                                          cwd=self.work, env=self.env, stdout=self.pglog,
                                          stderr=subprocess.STDOUT, start_new_session=True)
        self.postmaster_identity = proc_identity(self.postmaster.pid)
        require(self.postmaster_identity["exe"] == str(resolved(self.bindir / "postgres")),
                "new postmaster executable mismatch")
        self.receipt["postmaster"] = self.postmaster_identity
        self.write()
        deadline = time.monotonic() + self.remaining(90)
        while time.monotonic() < deadline:
            require(self.postmaster.poll() is None, "private postmaster exited during startup")
            status = subprocess.run([str(self.bindir / "pg_isready"), "-q"], env=self.env,
                                    cwd=self.work, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                    timeout=12, check=False)
            if status.returncode == 0:
                break
            time.sleep(0.2)
        else:
            raise RuntimeError("private postmaster did not become ready")
        pid = int((self.data / "postmaster.pid").read_text().splitlines()[0])
        require(pid == self.postmaster.pid, "private data PID is not our direct child")
        identity = json.loads(self.sql("cluster-identity",
            "SELECT json_build_object('server_version_num',current_setting('server_version_num'),"
            "'data_directory',current_setting('data_directory'),'port',current_setting('port'),"
            "'listen_addresses',current_setting('listen_addresses'),'user',current_user,"
            "'max_connections',current_setting('max_connections'),"
            "'system_identifier',(SELECT system_identifier::text FROM pg_control_system()),"
            "'postmaster_start',pg_postmaster_start_time()::text);").strip())
        require(int(identity["server_version_num"]) // 10000 == 18 and
                resolved(identity["data_directory"]) == self.data and identity["port"] == str(self.args.port) and
                identity["listen_addresses"] == "" and identity["user"] == "laplace_admin",
                "connected server does not match fresh isolated cluster")
        self.receipt["cluster_identity"] = identity
        self.phase("private-pg18-ready")

    def backend_proof(self):
        self.sql("probe-database", "CREATE DATABASE laplace_identity_probe OWNER laplace_admin;")
        self.sql("probe-extensions", "CREATE EXTENSION postgis; CREATE EXTENSION laplace_geom;"
                 "CREATE EXTENSION laplace_substrate;", database="laplace_identity_probe")
        statement = (
            "SET search_path=laplace,public;\n"
            "SELECT postgis_full_version();\n"
            "SELECT encode(laplace.word_id('canonical identity qualification'),'hex');\n"
            "SELECT count(*) FROM converse.text_root_placements(ARRAY['canonical identity qualification']);\n"
            "SELECT json_build_object('qualification_backend',pg_backend_pid(),"
            "'extensions',(SELECT json_object_agg(extname,extversion) FROM pg_extension WHERE extname IN "
            "('postgis','laplace_geom','laplace_substrate')),"
            "'execution_modules',(SELECT json_agg(DISTINCT probin) FROM pg_proc WHERE probin LIKE '%laplace_execution_%'));"
            "\nSELECT pg_sleep(15);\n")
        path = self.work / "backend-proof.sql"
        path.write_text(statement)
        log = self.logs / "backend-proof.log"
        with log.open("wb") as stream:
            self.backend = subprocess.Popen([str(self.bindir / "psql"), "-X", "-A", "-t", "--no-password",
                                             "--set=ON_ERROR_STOP=1", "--dbname=laplace_identity_probe", "--file", str(path)],
                                            env=self.env, cwd=self.work, stdout=stream, stderr=subprocess.STDOUT)
            observed = None
            deadline = time.monotonic() + self.remaining(120)
            while time.monotonic() < deadline:
                for line in log.read_text(errors="replace").splitlines():
                    if line.startswith("{") and '"qualification_backend"' in line:
                        observed = json.loads(line)
                        break
                if observed:
                    break
                require(self.backend.poll() is None, f"backend proof exited before observation; see {log}")
                time.sleep(0.02)
            require(observed is not None, "backend proof produced no held-session identity")
            pid = observed["qualification_backend"]
            identity = proc_identity(pid)
            require(identity["ppid"] == self.postmaster.pid and
                    identity["exe"] == self.postmaster_identity["exe"], "proof backend is not our postmaster child")
            mappings = mapped_files(pid)
            self.receipt["backend_observation"] = {"identity": identity, "sql": observed,
                                                   "mapped_libraries": mappings}
            self.write()
            require(observed["extensions"] == self.versions, "actual extension versions differ from installed controls")
            require(observed["execution_modules"] == [self.module], "SQL catalog selects a different execution module")
            for name, expected in self.installed.items():
                require(str(expected) in mappings, f"backend did not map installed {name}: {expected}")
                require(mappings[str(expected)] == self.prepared_files[str(expected)],
                        f"backend mapped a changed candidate library: {expected}")
            postgis_maps = [Path(path) for path in mappings if Path(path).name.startswith("postgis")]
            require(postgis_maps, "backend has no observed PostGIS module")
            for path in postgis_maps:
                require(beneath(path, self.pgprefix), f"PostGIS backend mapping escapes isolated PG prefix: {path}")
            for path in mappings:
                if "laplace" in Path(path).name:
                    require(beneath(Path(path), self.nativeprefix) or beneath(Path(path), self.pgprefix),
                            f"backend mapped foreign Laplace library: {path}")
            require(self.backend.wait(timeout=self.remaining(35)) == 0, "backend probe failed")
            self.backend = None
        self.receipt["backend_proof_log"] = fingerprint(log)
        self.phase("installed-backend-library-identity-verified")

    def sql_tests(self):
        junit = self.work / "ctest.xml"
        try:
            self.command("native-and-sql-regressions",
                         [self.ctest, "--test-dir", self.build, "--output-on-failure", "-j", "1",
                          "--timeout", "1200", "--output-junit", junit, "-R", self.test_regex], limit=1800)
        finally:
            destination = self.work / "regression-evidence"
            destination.mkdir()
            for suffix in ("geom", "substrate"):
                source = self.build / "extension" / ("laplace_" + suffix) / "tests/regress_output"
                if source.exists():
                    shutil.copytree(source, destination / suffix, symlinks=True)
            for name in ("LastTest.log", "LastTestsFailed.log"):
                source = self.build / "Testing/Temporary" / name
                if source.exists():
                    shutil.copyfile(source, destination / name)
        suite = ET.parse(junit).getroot()
        cases = suite.findall(".//testcase")
        require(len(cases) == 9, f"registered native/SQL fixture count differs: {len(cases)}")
        require(all(not any(child.tag in ("failure", "error", "skipped") for child in case) for case in cases),
                "CTest reports failure, error, or skipped test")
        self.receipt["native_and_sql_results"] = {"junit": fingerprint(junit), "registered_cases": len(cases)}
        self.phase("native-and-complete-sql-regressions-passed")

    def upgrade_test(self):
        output = self.work / "physicality-upgrade.json"
        script = self.source / "scripts/test-physicality-transport-upgrade.py"
        self.receipt["physicality_upgrade_inputs"] = {
            "script": fingerprint(script),
            "old_template": fingerprint(self.source / "scripts/testdata/physicality-descriptor-materialize-22.sql"),
            "new_template": fingerprint(self.source / "extension/laplace_substrate/sql/functions/ingest/physicality_descriptor_materialize.sql.in"),
            "execution_library": fingerprint(self.installed[self.module])}
        self.command("physicality-output-upgrade",
                     [sys.executable, script, "--pg-config", self.pgconfig,
                      "--execution-library", self.installed[self.module], "--receipt", output], limit=600)
        report = json.loads(output.read_text())
        require(report.get("status") == "completed" and report.get("libraryScope") == "actual-execution-library",
                "upgrade control did not pass against the actual execution library")
        require(not report.get("cleanupErrors") and all(check.get("passed") is True for check in report["checks"]),
                "upgrade control reports incomplete checks or cleanup")
        self.receipt["physicality_upgrade"] = {"receipt": fingerprint(output), "result": report}
        self.phase("real-execution-library-upgrade-passed")

    def managed_tests(self):
        results = self.work / "managed-results"
        results.mkdir()
        selector = "|".join("FullyQualifiedName~." + name + "." for name in MANAGED_CLASSES)
        argv = [str(self.dotnet), "vstest", str(self.assembly), "/TestCaseFilter:" + selector,
                "/Logger:trx;LogFileName=canonical-identity.trx", "/ResultsDirectory:" + str(results)]
        log = self.logs / "managed-tests.log"
        entry = {"name": "managed-real-pg-tests", "argv": argv, "log": str(log)}
        self.receipt["commands"].append(entry)
        self.write()
        observed = {}
        mapping_cache = {}
        root_identity = None
        with log.open("wb") as stream:
            process = subprocess.Popen(argv, env=self.env, cwd=self.source, stdout=stream,
                                       stderr=subprocess.STDOUT, start_new_session=True)
            root_identity = proc_identity(process.pid)
            deadline = time.monotonic() + self.remaining(1200)
            try:
                while process.poll() is None:
                    require(time.monotonic() < deadline, "managed tests timed out")
                    pending = [process.pid]
                    seen = set()
                    while pending:
                        pid = pending.pop()
                        if pid in seen:
                            continue
                        seen.add(pid)
                        try:
                            identity = proc_identity(pid)
                            if os.stat(Path("/proc") / str(pid)).st_uid != os.geteuid():
                                continue
                            # Discover children of this invocation, not arbitrary testhost processes.
                            for task in (Path("/proc") / str(pid) / "task").iterdir():
                                children = task / "children"
                                pending.extend(int(item) for item in children.read_text().split())
                            key = str(pid) + ":" + identity["start_ticks"]
                            maps = mapped_files(pid, mapping_cache)
                            if str(self.managed_core) in maps:
                                observed[key] = {"process": identity, "mapped_libraries": maps}
                                for mapped, info in maps.items():
                                    if Path(mapped).name.startswith("liblaplace_"):
                                        require(beneath(Path(mapped), self.assembly.parent),
                                                f"managed invocation mapped foreign Laplace library: {mapped}")
                                    if mapped in self.prepared_files:
                                        require(info == self.prepared_files[mapped],
                                                f"managed invocation mapped a changed candidate library: {mapped}")
                        except (FileNotFoundError, ProcessLookupError):
                            continue
                    time.sleep(0.025)
                entry["returncode"] = process.returncode
                require(process.returncode == 0, f"managed real-PG tests failed; see {log}")
            finally:
                if process.poll() is None:
                    # Only this invocation's freshly-created session; never a basename/UID sweep.
                    os.killpg(process.pid, signal.SIGTERM)
                    try:
                        process.wait(timeout=15)
                    except subprocess.TimeoutExpired:
                        os.killpg(process.pid, signal.SIGKILL)
                        process.wait(timeout=15)
                entry["returncode"] = process.returncode
                self.receipt["managed_process_observation"] = {"invocation": root_identity, "mapped_processes": observed}
                self.write()
        require(observed, "no actual descendant managed test process mapped the candidate app-local core")
        self.receipt["managed_results"] = validate_trx(results / "canonical-identity.trx")
        self.phase("managed-real-pg-transactions-passed")

    def verify_unchanged_inputs(self):
        for path, expected in self.prepared_files.items():
            require(fingerprint(path) == expected, "candidate input changed during qualification: " + path)
        head = self.command("source-head-after", ["git", "-C", self.source, "rev-parse", "HEAD"]).strip()
        require(head == self.args.source_commit, "candidate source commit changed during qualification")
        self.command("source-clean-after", ["git", "-C", self.source, "diff", "--exit-code", "HEAD", "--"])
        self.receipt["candidate_inputs_unchanged"] = {
            "source_commit": head, "verified_file_count": len(self.prepared_files)}
        self.write()

    def stop(self):
        if self.backend is not None and self.backend.poll() is None:
            self.backend.terminate()
            self.backend.wait(timeout=15)
        if self.postmaster is None:
            return
        cleanup = {"owned_pid": self.postmaster.pid, "status": "stopping"}
        self.receipt["cluster_shutdown"] = cleanup
        self.write()
        if self.postmaster.poll() is None:
            identity = proc_identity(self.postmaster.pid)
            require(identity == self.postmaster_identity, "refusing stop: owned postmaster identity changed")
            # An unreaped direct child cannot have its PID reused. SIGINT is PG fast shutdown.
            self.postmaster.send_signal(signal.SIGINT)
            try:
                self.postmaster.wait(timeout=60)
            except subprocess.TimeoutExpired:
                cleanup["immediate_shutdown_required"] = True
                self.postmaster.send_signal(signal.SIGQUIT)
                self.postmaster.wait(timeout=30)
        cleanup["returncode"] = self.postmaster.returncode
        require(not (self.data / "postmaster.pid").exists(), "private postmaster PID file remains after stop")
        cleanup["status"] = "stopped"
        self.pglog.close()
        cleanup["log"] = fingerprint(self.logs / "postmaster.log")
        self.write()

    def run(self):
        primary = None
        try:
            self.prepare()
            self.start()
            self.backend_proof()
            self.sql_tests()
            self.upgrade_test()
            self.managed_tests()
        except BaseException as error:
            primary = error
            self.receipt["failure"] = {"type": type(error).__name__, "message": str(error),
                                       "traceback": traceback.format_exc()}
        finally:
            try:
                self.stop()
            except BaseException as error:
                self.receipt["shutdown_failure"] = {"type": type(error).__name__, "message": str(error)}
                if primary is None:
                    primary = error
            try:
                if self.prepared_files:
                    self.verify_unchanged_inputs()
            except BaseException as error:
                self.receipt["candidate_identity_failure"] = str(error)
                if primary is None:
                    primary = error
            try:
                if self.protected_before is not None:
                    after = self.protected_snapshot()
                    self.receipt["protected_after"] = after
                    require(after == self.protected_before, "protected file/process identity changed during proof")
            except BaseException as error:
                self.receipt["preservation_failure"] = str(error)
                if primary is None:
                    primary = error
            self.receipt["status"] = "failed" if primary else "passed"
            self.write()
        print("CANONICAL_IDENTITY_PG_RESULT " + json.dumps({
            "status": self.receipt["status"], "receipt": str(self.work / "receipt.json"),
            "source_commit": self.args.source_commit}), flush=True)
        if primary:
            raise primary


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    for option in ("source", "build", "pg-prefix", "native-prefix", "work", "managed-assembly",
                   "perfcache", "highway-perfcache", "chess-position-perfcache", "install-receipt", "ctest", "dotnet"):
        result.add_argument("--" + option, required=True, type=Path)
    result.add_argument("--source-commit", required=True)
    result.add_argument("--install-receipt-sha256", required=True)
    result.add_argument("--regress-db", required=True, help="exact unique stem used during CMake configuration")
    result.add_argument("--port", required=True, type=int)
    result.add_argument("--runtime-library-directory", action="append", default=[], type=Path)
    result.add_argument("--protected-file", action="append", default=[], type=Path)
    result.add_argument("--protected-pid-file", action="append", default=[], type=Path)
    result.add_argument("--timeout", type=int, default=3600)
    return result


def main():
    args = parser().parse_args()
    require(300 <= args.timeout <= 7200, "total proof timeout must be finite, between 300 and 7200 seconds")
    require(re.fullmatch(r"[0-9a-f]{64}", args.install_receipt_sha256), "invalid install receipt SHA-256")
    def interrupted(signum, frame):
        raise KeyboardInterrupt("qualification interrupted by signal " + str(signum))
    signal.signal(signal.SIGTERM, interrupted)
    Qualification(args).run()


if __name__ == "__main__":
    main()
