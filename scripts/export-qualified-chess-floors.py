#!/usr/bin/env python3
"""Explicit pre-merge recorded-floor export from a qualified source revision.

Run only under the ordinary host lock. This rebuilds and checks the managed
catalog; it does not claim byte identity with a deleted PR managed publication.
Native libraries come from the retained PR build path and are hashed and tested
again here; no historical native-byte hash comparison is available. No installation or DB write
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

CANDIDATE = "934e349e5dc308fd9e355a9105facec44d5d74ca"
CANDIDATE_TREE = "ff3f09e0ccd8f4ecaed95e0073d6b5bade713f83"
INSTALLED_SOURCE = "f2cbb5fc7305dfd20d9fa3a19003b60a06b457c0"
REPOSITORY = "SaltyPatron/Laplace"
DURABLE_ROOT = Path("/build/laplace/corpus-exports/chess")
UNCHANGED = (
    "extension", "db", "engine/core/src", "engine/core/include",
    "app/Laplace.Substrate", "app/Laplace.Core/Core/Hash128.cs",
    "app/Laplace.Core/Core/SqlCatalog.cs", "app/Laplace.Core/Core/NativeLibraryClosure.cs",
    "app/Laplace.Chess/Modality", "app/Laplace.Chess/Service/ChessCompose.cs",
    "app/Laplace.Chess/Service/ChessVocabulary.cs", "app/Laplace.Chess/Service/ChessReplay.cs",
)


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


def run_json(run_id):
    token = os.environ.get("GITHUB_TOKEN", "")
    request = urllib.request.Request(
        "https://api.github.com/repos/" + REPOSITORY + "/actions/runs/" + str(run_id),
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


def qualification(plan, root):
    run_id, attempt = plan["proof_run_id"], plan["proof_run_attempt"]
    if type(run_id) is not int or type(attempt) is not int or run_id <= 0 or attempt <= 0:
        raise ValueError("an explicit positive proof run and attempt are required")
    remote = run_json(run_id)
    if (remote.get("status") != "completed" or remote.get("conclusion") != "success"
            or remote.get("event") != "pull_request"
            or remote.get("path") != ".github/workflows/pr-validation.yml"
            or remote.get("run_attempt") != attempt
            or not (remote.get("head_sha") == CANDIDATE
                    or any(p.get("head", {}).get("sha") == CANDIDATE for p in remote.get("pull_requests", [])))):
        raise ValueError("selected run is not the successful exact-candidate PR proof")
    path = Path("/build/laplace/work/ci-sessions") / (str(run_id) + "-" + str(attempt) + "-pr/session.json")
    state = load(path, 65536)
    expected = subprocess.check_output(
        ["bash", str(root / "scripts/pr-proof.sh"), "--stage", "all", "--list-phases"],
        cwd=root, text=True, timeout=10).splitlines()
    if (state.get("schema") != "laplace.ci-session.v1" or state.get("kind") != "pr"
            or state.get("stage") != "all" or state.get("status") != "stopped"
            or state.get("cleanup_exit_code") != 0 or state.get("active") is not None
            or state.get("source") != {"commit": CANDIDATE, "tree": CANDIDATE_TREE}
            or state.get("phases") != expected
            or state.get("results") != [{"phase": p, "exit_code": 0} for p in expected]):
        raise ValueError("retained PR session does not establish every completed canonical proof phase")
    checkout = Path(state["checkout"])
    if not checkout.is_absolute() or str(checkout).startswith(("/tmp/", "/var/tmp/", "/dev/shm/")):
        raise ValueError("qualified checkout identity was not permanent")
    # This is the existing place-build-directory.py address of this exact checkout,
    # retained after normal PR worktree cleanup; never select a latest build.
    build = Path("/build/laplace/build") / ("legacy-" + hashlib.sha256(os.fsencode(checkout)).hexdigest()[:16])
    cache = (build / "CMakeCache.txt").read_text()
    match = re.search(r"^CMAKE_HOME_DIRECTORY:INTERNAL=(.*)$", cache, re.MULTILINE)
    if not match or Path(match.group(1)) != checkout:
        raise ValueError("retained native build belongs to another checkout")
    if not re.fullmatch(r"[0-9a-f]{64}", (build / ".stamps/build-native").read_text().strip()):
        raise ValueError("qualified native build lacks its successful build stamp")
    return build, {"run_id": run_id, "run_attempt": attempt, "source": state["source"],
                   "phases": state["results"], "native_build": str(build),
                   "pr_checkout": str(checkout)}


def installed_state(guard, artifact, prefix, pg, baseline):
    database = guard.read_database(pg)
    if database != baseline["database"]:
        raise ValueError("installed database/SQL contract differs from the exact pilot baseline")
    if database["running_ingests"] != 0:
        raise ValueError("an ingest remains active")
    observed = {}
    for name, expected in baseline["artifacts"].items():
        if name in database["roms"]:
            path = Path(database["roms"][name])
        elif name in ("laplace_chess_transition_perfcache.bin", "laplace_modality_number_perfcache.bin"):
            path = prefix / "share/laplace" / name
        elif name == "chess_floor_pair_receipt":
            path = prefix / "share/laplace/chess-floor/current/receipt.json"
        else:
            path = prefix / name
        path = path.resolve(strict=True)
        if not path.is_relative_to(prefix.resolve(strict=True)) or artifact.sha256(path) != expected:
            raise ValueError("installed native/floor identity differs from the exact pilot baseline")
        observed[name] = expected
    if not all(name in observed for name in guard.MODULES.values()):
        raise ValueError("pilot baseline lacks the full installed native closure")
    return {"database": database, "artifacts": observed}


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
    artifact = module(root, "export_artifact_owner", "chess-floor-artifacts.py")
    owner = module(root, "export_acceptance_owner", "accept-chess-environment.py")
    guard = module(root, "export_installed_runtime_owner", "check-application-runtime.py")
    corpus = module(root, "export_database_target_owner", "measure-installed-chess-corpus.py")
    proof = {"schema": "laplace.qualified-recorded-floor-export/v1", "status": "incomplete",
             "candidate_commit": CANDIDATE, "candidate_tree": CANDIDATE_TREE,
             "installed_source": INSTALLED_SOURCE, "installed_payload_mutated": False,
             "database_write_requested": False, "selection_attempted": False, "selection_completed": False,
             "managed_provenance": "rebuilt from qualified source and checked in this invocation",
             "native_provenance": "retained build path/source/stamp from the successful PR; current bytes hashed and qualified now",
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
            if git(root, "rev-parse", "HEAD") != CANDIDATE or git(root, "rev-parse", "HEAD^{tree}") != CANDIDATE_TREE:
                raise ValueError("checkout differs from the explicitly qualified candidate")
            if git(root, "status", "--porcelain", "--untracked-files=all"):
                raise ValueError("candidate worktree has local changes")
            subprocess.run(["git", "-C", str(root), "diff", "--exit-code", INSTALLED_SOURCE,
                            CANDIDATE, "--", *UNCHANGED], check=True, timeout=30,
                           stdout=subprocess.DEVNULL)
            proof["unchanged_compatibility_owners"] = list(UNCHANGED)
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
                    or measured.get("sourceSha") != INSTALLED_SOURCE or measured.get("status") != "completed"
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
            proof["installed_before"] = installed_state(guard, artifact, prefix, pg, baseline)
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
            proof["installed_after"] = installed_state(guard, artifact, prefix, pg, baseline)
            if (artifact.sha256(pilot / "receipt.json") != plan["pilot_receipt_sha256"]
                    or artifact.sha256(pilot / "native-before.json") != plan["pilot_native_snapshot_sha256"]
                    or baseline != load(pilot / "native-after.json")):
                raise ValueError("pilot evidence changed")
            if git(root, "status", "--porcelain", "--untracked-files=all"):
                raise ValueError("candidate source changed across export")
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
    if (plan.get("candidate_commit") != CANDIDATE or plan.get("candidate_tree") != CANDIDATE_TREE
            or plan.get("installed_source") != INSTALLED_SOURCE
            or any(not re.fullmatch(r"[0-9a-f]{64}", plan.get(key, ""))
                   for key in ("pilot_receipt_sha256", "pilot_native_snapshot_sha256"))):
        raise ValueError("explicit export selection differs from this reviewed compatibility boundary")
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
