#!/usr/bin/env python3
"""Explicit recorded-floor export from a qualified and installed main revision.

Run only under the ordinary host lock. This rebuilds and checks the managed
catalog; it does not claim byte identity with the prior managed publication.
Native libraries come from the retained main build path and are hashed and tested
again here; the ordinary installed-form guard binds them to the pilot generation. No installation or DB write
is performed. Selection is published only after complete readback and postflight.
"""
import argparse
import hashlib
import grp
import importlib.util
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import urllib.request
import xml.etree.ElementTree as ET

REPOSITORY = "SaltyPatron/Laplace"
DURABLE_ROOT = Path("/build/laplace/corpus-exports/chess")
SESSION_ROOT = Path("/build/laplace/work/ci-sessions")
BUILD_ROOT = Path("/build/laplace/build")


def load(path, limit=4 * 1024 * 1024):
    with Path(path).open("rb") as source:
        raw = source.read(limit + 1)
    if len(raw) > limit:
        raise ValueError("metadata exceeds its fixed envelope")
    return json.loads(raw)


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args], text=True,
                                   stderr=subprocess.DEVNULL, timeout=30).strip()


def module(root, name, filename):
    spec = importlib.util.spec_from_file_location(name, root / "scripts" / filename)
    value = importlib.util.module_from_spec(spec)
    sys.modules[name] = value
    spec.loader.exec_module(value)
    return value


def run_json(run_id, suffix=""):
    token = os.environ.get("GITHUB_TOKEN", "")
    request = urllib.request.Request(
        "https://api.github.com/repos/" + REPOSITORY + "/actions/runs/" + str(run_id) + suffix,
        headers={"Accept": "application/vnd.github+json", "Authorization": "Bearer " + token,
                 "X-GitHub-Api-Version": "2022-11-28"})
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, req, fp, code, msg, headers, newurl):
            raise ValueError("workflow metadata redirect refused")
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    with opener.open(request, timeout=20) as response:
        raw = response.read(4 * 1024 * 1024 + 1)
    if len(raw) > 4 * 1024 * 1024:
        raise ValueError("workflow metadata exceeded its envelope")
    return json.loads(raw)


def selection_identity(plan):
    """Select immutable source identities; qualification authenticates their proof."""
    if not isinstance(plan, dict):
        raise ValueError("export selection must be a metadata object")
    values = [plan.get(key) for key in ("candidate_commit", "candidate_tree", "installed_source")]
    if any(not isinstance(value, str) or re.fullmatch(r"[0-9a-f]{40}", value) is None
           for value in values):
        raise ValueError("export source must use complete immutable commit and tree identities")
    if values[0] != values[2]:
        raise ValueError("export candidate must be the selected installed source")
    return {"commit": values[0], "tree": values[1]}


def lexical_failure_evidence(run_id, attempt, expected, state):
    """Authenticate the one supported failed lifecycle without changing its status."""
    prerequisites = ["policy", "dependencies", "build", "native-dev", "managed-dev",
                     "uci-dev", "browser-dev", "native-install", "database-maintenance"]
    failed_phase = "lexical-foundation"
    if expected.count(failed_phase) != 1:
        raise ValueError("canonical product phase list has no unique lexical boundary")
    boundary = expected.index(failed_phase)
    results = state.get("results")
    if (expected[:boundary] != prerequisites or state.get("status") != "failed"
            or state.get("next") != boundary + 1 or not isinstance(results, list)
            or len(results) != boundary + 1
            or results[:-1] != [{"phase": p, "exit_code": 0} for p in prerequisites]
            or not isinstance(results[-1], dict) or set(results[-1]) != {"phase", "exit_code"}
            or results[-1]["phase"] != failed_phase
            or type(results[-1]["exit_code"]) is not int or results[-1]["exit_code"] <= 0):
        raise ValueError("retained canonical product phases do not establish lexical-only failure")

    jobs = run_json(run_id, "/attempts/" + str(attempt) + "/jobs?per_page=100")
    rows = jobs.get("jobs") if isinstance(jobs, dict) else None
    if (not isinstance(rows, list) or jobs.get("total_count") != 1 or len(rows) != 1
            or not isinstance(rows[0], dict)):
        raise ValueError("failed exact-main lifecycle job is absent or ambiguous")
    job = rows[0]
    if (type(job.get("id")) is not int or job["id"] <= 0 or job.get("run_id") != run_id
            or job.get("head_sha") != state["source"]["commit"]
            or job.get("status") != "completed" or job.get("conclusion") != "failure"):
        raise ValueError("failed exact-main lifecycle job identity differs")
    steps = job.get("steps")
    if (not isinstance(steps, list) or any(not isinstance(step, dict)
            or not all(isinstance(step.get(key), str) for key in ("name", "status", "conclusion"))
            for step in steps)):
        raise ValueError("failed exact-main lifecycle steps are incomplete")
    installed = (
        "Resolve the Stockfish checkout for application publication", "Start ordered development phases",
        "Check source and policy", "Resolve build dependencies", "Build native and managed artifacts",
        "Test native engine", "Test managed code", "Test UCI runtime", "Test browser product",
        "Install native artifacts", "Migrate and reconcile installed database",
    )
    downstream = (
        "Admit operational memory", "Publish applications",
        "Verify the installed direct build uses the selected Stockfish checkout",
        "Verify ordinary operational execution", "Verify database health",
        "Test native PostgreSQL extensions", "Test managed database integration",
        "Prove live recursive substrate", "Verify live API endpoints", "Test live product behavior",
        "Evaluate witnessed generation",
    )
    sequence = [(name, "success") for name in installed]
    sequence += [("Admit required lexical foundation", "failure")]
    sequence += [(name, "skipped") for name in downstream]
    sequence += [("Release product host reservation", "success")]
    observed = []
    for name, conclusion in sequence:
        matches = [(index, step) for index, step in enumerate(steps) if step["name"] == name]
        if (len(matches) != 1 or matches[0][1]["status"] != "completed"
                or matches[0][1]["conclusion"] != conclusion):
            raise ValueError("failed exact-main lifecycle lacks required phase outcome: " + name)
        observed.append(matches[0][0])
    if observed != sorted(observed) or [step["name"] for step in steps
            if step["conclusion"] == "failure"] != ["Admit required lexical foundation"]:
        raise ValueError("failed exact-main lifecycle differs from ordered lexical-only failure")
    return {"job_id": job["id"], "failed_phase": failed_phase,
            "failed_exit_code": results[-1]["exit_code"],
            "not_executed_phases": expected[boundary + 1:],
            "workflow_steps": [{key: step[key] for key in ("name", "status", "conclusion")}
                               for step in steps]}


def qualification(plan, root):
    source = selection_identity(plan)
    run_id, attempt = plan["proof_run_id"], plan["proof_run_attempt"]
    if type(run_id) is not int or type(attempt) is not int or run_id <= 0 or attempt <= 0:
        raise ValueError("an explicit positive proof run and attempt are required")
    remote = run_json(run_id)
    if (remote.get("id") != run_id or remote.get("status") != "completed"
            or remote.get("conclusion") not in ("success", "failure")
            or remote.get("event") not in ("push", "workflow_dispatch")
            or remote.get("head_branch") != "main"
            or remote.get("path") != ".github/workflows/laplace.yml"
            or remote.get("run_attempt") != attempt or remote.get("head_sha") != source["commit"]):
        raise ValueError("selected run is not a supported terminal exact-main product lifecycle")
    path = SESSION_ROOT / (str(run_id) + "-" + str(attempt) + "-product/session.json")
    if (path.is_symlink() or path.parent.is_symlink()
            or path.parent.stat().st_uid != os.getuid()
            or path.parent.stat().st_mode & 0o077):
        raise ValueError("retained lifecycle session must remain private and owned by this runner")
    state = load(path, 65536)
    # Resolve the full activation contract independently of the operator event.
    # Current push/all is development-only; a real dispatch/all still selects all
    # phases. Authenticate the actual remote event and retained outcomes separately.
    # A lexical-only failure retains the unexecuted suffix explicitly.
    environment = dict(os.environ, GITHUB_EVENT_NAME="workflow_dispatch",
                       LAPLACE_FRESH_DB="", LAPLACE_RESTORE_FOUNDATION="",
                       LAPLACE_GENERATION_BENCHMARK="")
    expected = subprocess.check_output(
        ["bash", str(root / "scripts/product-ci.sh"), "all", "--list-phases"],
        cwd=root, text=True, timeout=10, env=environment).splitlines()
    if (not expected or state.get("schema") != "laplace.ci-session.v1"
            or state.get("kind") != "product" or state.get("stage") != "all"
            or type(state.get("cleanup_exit_code")) is not int or state["cleanup_exit_code"] != 0
            or "active" not in state or state["active"] is not None
            or state.get("source") != source or state.get("phases") != expected):
        raise ValueError("retained main session does not establish canonical product phase identity and cleanup")
    failed = None
    if remote["conclusion"] == "success":
        if (state.get("status") != "stopped" or state.get("next") != len(expected)
                or state.get("results") != [{"phase": p, "exit_code": 0} for p in expected]):
            raise ValueError("retained main session does not establish every completed canonical product phase")
    else:
        failed = lexical_failure_evidence(run_id, attempt, expected, state)
    checkout = Path(state["checkout"])
    if (not checkout.is_absolute() or checkout.resolve(strict=True) != checkout
            or str(checkout).startswith(("/tmp/", "/var/tmp/", "/dev/shm/"))):
        raise ValueError("qualified checkout identity was not permanent")
    # Follow the placement owner's actual link, including any completed migration.
    # Never reconstruct an obsolete directory name or select the newest build.
    build_link = checkout / "build"
    if not build_link.is_symlink():
        raise ValueError("retained checkout has no placed native build link")
    build = build_link.resolve(strict=True)
    if not build.is_dir() or build.parent != BUILD_ROOT.resolve(strict=True):
        raise ValueError("retained native build is outside the canonical build root")
    cache = (build / "CMakeCache.txt").read_text()
    match = re.search(r"^CMAKE_HOME_DIRECTORY:INTERNAL=(.*)$", cache, re.MULTILINE)
    if not match or Path(match.group(1)) != checkout:
        raise ValueError("retained native build belongs to another checkout")
    directory = re.search(r"^CMAKE_CACHEFILE_DIR:INTERNAL=(.*)$", cache, re.MULTILINE)
    if not directory or Path(directory.group(1)).resolve(strict=True) != build:
        raise ValueError("retained CMake cache belongs to another build directory")
    stamps = {name: (build / ".stamps" / name).read_text().strip()
              for name in ("build-native", "install-native")}
    if (any(not re.fullmatch(r"[0-9a-f]{64}", value) for value in stamps.values())
            or len(set(stamps.values())) != 1):
        raise ValueError("qualified native build/install stamps are absent or disagree")
    return build, {"run_id": run_id, "run_attempt": attempt, "source": state["source"],
                   "lifecycle_event": remote["event"],
                   "lifecycle_conclusion": remote["conclusion"],
                   "full_lifecycle_passed": remote["conclusion"] == "success",
                   "session_status": state["status"], "cleanup_exit_code": state["cleanup_exit_code"],
                   "lexical_failure": failed,
                   "phases": state["results"], "native_build": str(build),
                   "lifecycle_checkout": str(checkout), "native_fingerprint": stamps["build-native"],
                   "session_receipt": str(path),
                   "session_sha256": hashlib.sha256(path.read_bytes()).hexdigest()}


def installed_state(guard, prefix, pg, baseline, qualification):
    if qualification["native_fingerprint"] != baseline["native_fingerprint"]:
        raise ValueError("retained native generation differs from the exact installed pilot")
    database = guard.read_database(pg)
    if database != baseline["database"]:
        raise ValueError("installed database/SQL contract differs from the exact pilot baseline")
    # The same source is now proved and installed. Reuse the full existing guard:
    # staged CMake installed bytes, native/SQL identities, ROMs, pair receipt,
    # migrations, actual build/install stamps and zero active ingests.
    observed = guard.snapshot(Path(qualification["lifecycle_checkout"]), prefix, database,
                              baseline["native_fingerprint"])
    if observed != baseline:
        raise ValueError("installed native/floor generation differs from the exact pilot baseline")
    return observed

def payload(artifact, directory):
    rows = {}
    for path in sorted(directory.rglob("*")):
        if path.is_file():
            if path.is_symlink():
                raise ValueError("managed publication may not use an unbound symlink")
            rows[str(path.relative_to(directory))] = {"bytes": path.stat().st_size,
                                                     "sha256": artifact.sha256(path)}
    if not rows:
        raise ValueError("managed publication is empty")
    return rows


def publish_selection(artifact, owner, prefix, exported, proof, publish):
    """Observe the atomic publisher's actual outcome even when its process fails."""
    proof.update(selection_attempted=True, selection_completed=None)
    primary = None
    try:
        publish()
    except BaseException as error:
        primary = error
        proof["status"] = "failed"
        raise
    finally:
        try:
            # The full existing export validator owns readback. Bound it separately
            # so cancellation cannot turn an unknown publication outcome into false.
            with owner.deadline(120):
                path = prefix / "etc/chess-corpus-export.json"
                before = artifact.sha256(path) if path.exists() else None
                actual = artifact.selected_export(prefix)
                after = artifact.sha256(path) if path.exists() else None
                if before != after:
                    raise ValueError("persisted selection changed during final readback")
                proof["selection_completed"] = actual == exported
                proof["selection_observation"] = (
                    "matching-export" if actual == exported else
                    "no-persisted-selection" if actual is None else "different-export")
                proof["selection"] = {"path": str(path), "sha256": after}
        except BaseException as error:
            proof.update(status="failed", selection_completed=None, selection_observation="unknown",
                         selection_readback_failure_type=type(error).__name__)
            if primary is None:
                raise
    if proof["selection_completed"] is not True:
        proof["status"] = "failed"
        raise ValueError("persisted selection does not resolve to the completed export")


def execute(plan, root, prefix, pg, output):
    source = selection_identity(plan)
    artifact = module(root, "export_artifact_owner", "chess-floor-artifacts.py")
    owner = module(root, "export_acceptance_owner", "accept-chess-environment.py")
    guard = module(root, "export_installed_runtime_owner", "check-application-runtime.py")
    corpus = module(root, "export_database_target_owner", "measure-installed-chess-corpus.py")
    proof = {"schema": "laplace.qualified-recorded-floor-export/v1", "status": "incomplete",
             "candidate_commit": source["commit"], "candidate_tree": source["tree"],
             "installed_source": source["commit"], "installed_payload_mutated": False,
             "database_write_requested": False, "selection_attempted": False, "selection_completed": False,
             "managed_provenance": "rebuilt from qualified source and checked in this invocation",
             "native_provenance": "retained main build path/source/stamps; current bytes installed-form verified against the completed pilot and focused-tested now",
             "native_historical_byte_comparison": False,
             "claim": "completed observed-read-interval export; no snapshot, historical coverage or throughput claim"}
    evidence = output / "evidence"
    evidence.mkdir()
    save = lambda: artifact.atomic_json(evidence / "receipt.json", proof)
    save()
    def command(label, argv, timeout, env):
        owner.command(argv, evidence / (label + ".log"), timeout, env=env)
    try:
        with owner.deadline(7200):
            if git(root, "rev-parse", "HEAD") != source["commit"] or git(root, "rev-parse", "HEAD^{tree}") != source["tree"]:
                raise ValueError("checkout differs from the explicitly qualified candidate")
            if git(root, "status", "--porcelain", "--untracked-files=all"):
                raise ValueError("candidate worktree has local changes")
            command("lifecycle-controls", [sys.executable,
                    Path(__file__).with_name("test-qualified-chess-floor-export.py"), "-v"],
                    60, dict(os.environ))
            proof["lifecycle_controls"] = "passed"
            build, proof["qualification"] = qualification(plan, root)
            pilot = Path(plan["pilot_evidence_directory"])
            if not pilot.is_absolute() or not pilot.resolve(strict=True).is_relative_to(Path("/build/laplace")):
                raise ValueError("pilot evidence must be an explicitly selected permanent directory")
            if artifact.sha256(pilot / "receipt.json") != plan["pilot_receipt_sha256"]:
                raise ValueError("selected pilot receipt changed")
            measured = load(pilot / "receipt.json")
            passed = {p["name"] for p in measured.get("phases", []) if p.get("status") == "passed"}
            if (measured.get("schema") != "laplace.installed-native-corpus-measurement/v1"
                    or measured.get("sourceSha") != source["commit"] or measured.get("status") != "completed"
                    or not {"native-before", "native-after", "receipt"}.issubset(passed)):
                raise ValueError("pilot did not complete with the selected installed native/database proof")
            if artifact.sha256(pilot / "native-before.json") != plan["pilot_native_snapshot_sha256"]:
                raise ValueError("selected pilot native snapshot changed")
            baseline = load(pilot / "native-before.json")
            if baseline != load(pilot / "native-after.json") or baseline.get("format") != 1:
                raise ValueError("pilot installed runtime changed")
            if os.environ.get("LAPLACE_CHESS_CORPUS_EXPORT"):
                raise ValueError("explicit export override would hide the persisted selection")
            selection_parent = prefix / "etc"
            if (not selection_parent.is_dir() or selection_parent.is_symlink()
                    or not os.access(selection_parent, os.W_OK | os.X_OK)):
                raise ValueError("ordinary export-selection directory is not writable")
            proof["database_target"] = corpus.database_target()
            proof["installed_before"] = installed_state(guard, prefix, pg, baseline, proof["qualification"])
            proof["pilot"] = {"directory": str(pilot), "receipt_sha256": plan["pilot_receipt_sha256"],
                              "native_snapshot_sha256": artifact.sha256(pilot / "native-before.json")}
            for name in ("laplace_geom", "laplace_substrate"):
                if guard.control_version(build / "extension" / name / (name + ".control")) != baseline["database"]["extensions"][name]:
                    raise ValueError("candidate requires a different installed SQL extension version")
            module_name = (build / "extension/laplace_substrate/laplace_execution_module.txt").read_text().strip()
            if "lib/postgresql/18/" + module_name + ".so" not in baseline["artifacts"]:
                raise ValueError("candidate execution-module identity differs from installed baseline")
            native = {component: build / "engine" / component / ("liblaplace_" + component + ".so")
                      for component in ("core", "dynamics", "synthesis")}
            native_before = {name: artifact.sha256(path) for name, path in native.items()}
            proof["native_sha256"] = native_before
            save()
            managed_build = Path("/build/laplace/build") / ("chess-floor-export-" + output.name)
            managed_build.mkdir(mode=0o2770, parents=False, exist_ok=False)
            proof["managed_build"] = str(managed_build)
            env = dict(os.environ)
            env.update(LAPLACE_BUILD_ROOT=str(managed_build),
                       LAPLACE_ENGINE_BUILD=str(build / "engine"),
                       LAPLACE_PERFCACHE_BIN=baseline["database"]["roms"]["laplace_substrate.perfcache_path"],
                       MSBUILDDISABLENODEREUSE="1", UseSharedCompilation="false",
                       PYTHONDONTWRITEBYTECODE="1")
            env["LD_LIBRARY_PATH"] = ":".join([str(build / "engine" / part)
                for part in ("core", "dynamics", "synthesis")] + [env.get("LD_LIBRARY_PATH", "")])
            for project in ("ChessCatalogSurfaces", "Laplace.Core.Tests", "Laplace.Chess.Tests"):
                command("build-" + project,
                        ["dotnet", "build", root / "app" / project / (project + ".csproj"),
                         "-c", "Release", "--disable-build-servers", "--nologo", "-v", "minimal",
                         "-p:UseSharedCompilation=false"], 600, env)
            base = managed_build / "app/bin"
            catalog = base / "ChessCatalogSurfaces/Release/net10.0"
            for project in ("ChessCatalogSurfaces", "Laplace.Core.Tests", "Laplace.Chess.Tests"):
                folder = base / project / "Release/net10.0"
                for name, expected in native_before.items():
                    if artifact.sha256(folder / ("liblaplace_" + name + ".so")) != expected:
                        raise ValueError("managed app-local native closure differs from qualified build")
            before = payload(artifact, catalog)
            proof["managed_payload"] = before
            save()
            for project, selected in (
                    ("Laplace.Core.Tests", "FullyQualifiedName~ChessTransitionFloorBuilderTests|FullyQualifiedName~ChessPositionFloorTests|FullyQualifiedName~ChessTransitionFloorTests"),
                    ("Laplace.Chess.Tests", "FullyQualifiedName~ChessRecordedFloorExportTests|FullyQualifiedName~ChessRecordedFloorWitnessTests|FullyQualifiedName~ChessStartingSideInventoryTests")):
                command("test-" + project,
                        ["dotnet", "test", root / "app" / project / (project + ".csproj"),
                         "-c", "Release", "--no-build", "--nologo", "--filter", selected,
                         "--logger", "trx", "--results-directory", evidence / ("tests-" + project)], 600, env)
                reports = list((evidence / ("tests-" + project)).glob("*.trx"))
                if len(reports) != 1 or reports[0].stat().st_size > 16 * 1024 * 1024:
                    raise ValueError("focused qualification did not retain one bounded TRX report")
                report = ET.parse(reports[0]).getroot()
                namespace = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
                results = report.findall(".//t:UnitTestResult", namespace)
                if not results or any(row.get("outcome") != "Passed" for row in results):
                    raise ValueError("focused qualification has missing, failed or skipped results")
                classes = {row.get("className", "").split(",")[0].rsplit(".", 1)[-1]
                           for row in report.findall(".//t:TestMethod", namespace)}
                expected_classes = {item.split("~", 1)[1] for item in selected.split("|")}
                if not expected_classes.issubset(classes):
                    raise ValueError("focused qualification omitted a required actual test class")
                proof.setdefault("focused_tests", {})[project] = {
                    "passed": len(results), "trx_sha256": artifact.sha256(reports[0])}
            destination = output / "export"
            command("export", ["dotnet", catalog / "ChessCatalogSurfaces.dll", "export-recorded-floors",
                              "--output-dir", destination, "--page-size", "16",
                              "--maximum-materialized-mib", "128", "--maximum-retained-mib", "4096",
                              "--deadline-seconds", "3600", "--transition-buffer-records", "65536",
                              "--merge-fan-in", "32", "--maximum-spill-mib", "4096",
                              "--maximum-export-mib", "4096"], 3660, env)
            receipt = destination / "export-receipt.json"
            exported = artifact.validate_export(receipt)
            if exported["coverage"]["exported_playings"] <= 0 or exported["coverage"]["transition_occurrences"] <= 0:
                raise ValueError("completed export did not contain recorded chess transitions")
            hasher = artifact.native_hasher(native["core"])
            transition = artifact.validate_floor(exported["roles"]["transition-floor"], "transition", hasher)
            if transition["records"] != exported["coverage"]["unique_transitions"]:
                raise ValueError("exported transition count differs from native-verified floor")
            proof["export"] = exported
            proof["transition"] = transition
            if payload(artifact, catalog) != before or native_before != {name: artifact.sha256(path) for name, path in native.items()}:
                raise ValueError("managed/native payload changed across export")
            proof["installed_after"] = installed_state(guard, prefix, pg, baseline, proof["qualification"])
            if (artifact.sha256(pilot / "receipt.json") != plan["pilot_receipt_sha256"]
                    or artifact.sha256(pilot / "native-before.json") != plan["pilot_native_snapshot_sha256"]
                    or baseline != load(pilot / "native-after.json")):
                raise ValueError("pilot evidence changed")
            if git(root, "status", "--porcelain", "--untracked-files=all"):
                raise ValueError("candidate source changed across export")
            if artifact.sha256(Path(proof["qualification"]["session_receipt"])) != proof["qualification"]["session_sha256"]:
                raise ValueError("qualified lifecycle session changed across export")
            save()
            # The ordinary publisher owns this one persistent configuration write.
            # It does not replace serving files; the subsequent normal build/install does.
            if os.environ.get("LAPLACE_CHESS_CORPUS_EXPORT"):
                raise ValueError("explicit export override would hide the persisted selection")
            publish_selection(artifact, owner, prefix, exported, proof,
                              lambda: command("select", [sys.executable,
                                  root / "scripts/chess-floor-artifacts.py", "select-export",
                                  "--receipt", receipt, "--prefix", prefix], 120, env))
            proof["status"] = "completed"
    except BaseException as error:
        proof.update(status="failed", failure_type=type(error).__name__,
                     failure=str(error) if isinstance(error, ValueError) else "See retained phase log; raw driver/connection text omitted.")
        raise
    finally:
        save()
        print(json.dumps(proof, sort_keys=True, allow_nan=False))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--selection", type=Path, required=True)
    parser.add_argument("--candidate-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    plan = load(args.selection, 65536)
    selection_identity(plan)
    if any(not isinstance(plan.get(key), str) or re.fullmatch(r"[0-9a-f]{64}", plan[key]) is None
           for key in ("pilot_receipt_sha256", "pilot_native_snapshot_sha256")):
        raise ValueError("export selection requires complete pilot receipt and native snapshot hashes")
    if subprocess.run(["mountpoint", "-q", "/build"], check=False).returncode:
        raise ValueError("the dedicated build volume is not mounted")
    if (not args.output.is_absolute() or args.output.parent.resolve() != DURABLE_ROOT.resolve()
            or args.output.exists() or args.output.is_symlink()):
        raise ValueError("export output must be a new direct durable chess-export child")
    os.umask(0o002)
    group = grp.getgrnam("laplace-runner").gr_gid
    for directory in (Path("/build/laplace"), DURABLE_ROOT.parent, DURABLE_ROOT):
        if directory != Path("/build/laplace"):
            directory.mkdir(mode=0o2770, parents=False, exist_ok=True)
        metadata = directory.stat()
        if (directory.resolve(strict=True) != directory
                or metadata.st_dev != Path("/build").stat().st_dev
                or metadata.st_gid != group or not metadata.st_mode & stat.S_ISGID
                or not os.access(directory, os.W_OK | os.X_OK)):
            raise ValueError("durable export parent chain must retain mounted setgid laplace-runner ownership")
    args.output.mkdir(mode=0o2770)
    import signal
    def interrupted(signum, frame):
        raise KeyboardInterrupt("export qualification interrupted")
    signal.signal(signal.SIGTERM, interrupted)
    execute(plan, args.candidate_root.resolve(strict=True),
            Path(os.environ.get("LAPLACE_INSTALL_PREFIX", "/opt/laplace")),
            Path(os.environ.get("LAPLACE_PG_PREFIX", "/opt/laplace/pgsql-18")), args.output)


if __name__ == "__main__":
    main()
