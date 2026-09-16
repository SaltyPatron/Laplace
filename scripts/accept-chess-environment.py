#!/usr/bin/env python3
"""Explicit installed chess acceptance; the caller owns the shared host lock.

This is separate from deployment. Every measured phase keeps its own existing
validator and receipt. A failed dependency never becomes a throughput result.
"""
import argparse
from contextlib import contextmanager
import datetime
import hashlib
import importlib.util
import json
from itertools import islice
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = "laplace.chess-acceptance/v1"
PROFILE = {
    "recorded": {"games": 24, "depth": 4, "concurrency": [1, 2, 4],
                 "repeats": 3, "caseTimeoutSeconds": 600,
                 "durationSeconds": 30, "totalTimeoutSeconds": 7200},
    "retained": {"games": 16, "depth": 4, "concurrency": 1,
                 "replays": 2, "timeoutSeconds": 900},
    "retainedCapacity": {"jobs": 2, "gamesPerJob": 24, "depth": 4, "concurrency": 1,
                         "prepareTimeoutSeconds": 3600, "measureTimeoutSeconds": 3600,
                         "minimumAdmissionSeconds": 30, "replays": 1},
    "geometry": {"rows": 100000, "transactionRows": 10000,
                 "concurrency": [1, 2, 4], "repeats": 3, "timeoutSeconds": 900},
    "calibration": {"repeats": 3, "reserveCpus": 2, "maxSeconds": 1800, "caseTimeoutSeconds": 600},
}
MANAGED = (
    ("api", "Laplace.Endpoints.OpenAICompat", None),
    ("uci", "Laplace.Chess.Uci", "laplace-uci"),
    ("mcp", "Laplace.Endpoints.Mcp", "laplace-mcp"),
    ("lichess", "Laplace.Endpoints.Lichess", "laplace-lichess"),
)


def save(path, value):
    pending = path.with_name(path.name + ".pending")
    pending.write_text(json.dumps(value, indent=2, allow_nan=False) + "\n")
    pending.replace(path)


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / filename)
    result = importlib.util.module_from_spec(spec)
    sys.modules[name] = result
    spec.loader.exec_module(result)
    return result


def stop_group(process):
    """Reap the owner and kill its remaining group even if the leader exited."""
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        pass
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        pass
    # A resistant descendant can outlive a leader that honored SIGTERM.
    # Always address the owned process group, never processes by executable name.
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    process.wait(timeout=5)


def command(argv, log, timeout, env=None):
    """Every exit path, including cancellation, cleans this command's group."""
    with log.open("xb") as stream:
        process = subprocess.Popen([str(x) for x in argv], cwd=ROOT,
                                   stdout=stream, stderr=subprocess.STDOUT,
                                   start_new_session=True, env=env)
        try:
            try:
                code = process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                raise TimeoutError("phase deadline exceeded") from None
        finally:
            stop_group(process)
    if code:
        raise RuntimeError(f"command exited {code}; inspect {log.name}")


class Acceptance:
    def __init__(self, output):
        self.output = output
        output.mkdir(parents=True, exist_ok=False)
        self.receipt = {"schema": SCHEMA, "status": "incomplete", "profile": PROFILE,
                        "observedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                        "collectorSha256": sha256(Path(__file__)), "phases": [],
                        "targetGamesPerSecond": 2500,
                        "rateClaim": "Complete generation-plus-recording and prepared-corpus admission have separate validated rates; replay is never fresh admission.",
                        "coldServiceBootMeasured": False, "serviceRestartStartupMeasured": False,
                        "serviceTimingScope": "Explicit service restart through full readiness before game timing; existing OS caches, not a machine cold reboot."}
        self.flush()

    def flush(self):
        save(self.output / "receipt.json", self.receipt)

    def phase(self, name, operation, *, required=True, allowed=True):
        row = {"name": name, "required": required, "status": "running" if allowed else "blocked"}
        self.receipt["phases"].append(row)
        self.flush()
        if not allowed:
            row["reason"] = "exact installed runtime prerequisites did not pass"
            self.flush()
            return False
        started = time.monotonic()
        try:
            value = operation()
            row["status"] = "passed"
            if value is not None:
                row["result"] = value
        except KeyboardInterrupt:
            row.update(status="interrupted", failureType="KeyboardInterrupt")
            self.receipt["status"] = "interrupted"
            raise
        except Exception as error:
            row.update(status="failed", failureType=type(error).__name__, failure=str(error))
        finally:
            row["wallSeconds"] = time.monotonic() - started
            self.flush()
        print(f"CHESS_ACCEPTANCE {name} {row['status']}", flush=True)
        try:
            public_summary(name, self.output)
        except (OSError, ValueError, TypeError, KeyError) as error:
            print(f"CHESS_ACCEPTANCE_SUMMARY_ERROR {name} {type(error).__name__}", flush=True)
        return row["status"] == "passed"

    def run(self, name, argv, timeout, **kwargs):
        return self.phase(name, lambda: command(argv, self.output / (name + ".log"), timeout),
                          **kwargs)

    def finish(self):
        passed = all(p["status"] == "passed" for p in self.receipt["phases"] if p["required"])
        self.receipt["status"] = "passed" if passed else "failed"
        self.receipt["finishedUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        self.flush()
        return 0 if passed else 1


def managed_binding(output, prefix, work):
    app = Path(os.environ.get("LAPLACE_APP_DIR", str(prefix / "app")))
    proof = {"scope": "Fresh exact-source managed publication compared byte-for-byte with current installed payloads",
             "services": {}}
    save(output / "managed-binding.json", proof)
    with tempfile.TemporaryDirectory(prefix="chess-acceptance-managed-", dir=work) as directory:
        for name, project, link in MANAGED:
            expected = Path(directory) / name
            actual = app if link is None else (app / link).resolve(strict=True).parent
            command(["dotnet", "publish", ROOT / "app" / project / (project + ".csproj"),
                     "-c", "Release", "--no-self-contained", "-o", expected],
                    output / ("managed-" + name + "-build.log"), 900)
            files = sorted(p for p in expected.iterdir()
                           if p.is_file() and (p.suffix == ".dll" or
                              p.name.endswith((".deps.json", ".runtimeconfig.json"))))
            if not files or not any(p.name.startswith("Laplace.") and p.suffix == ".dll" for p in files):
                raise ValueError(f"{name} publication has no managed Laplace assembly")
            identities = {}
            for source in files:
                target = actual / source.name
                if not target.is_file() or sha256(source) != sha256(target):
                    raise ValueError(f"{name} installed payload differs: {source.name}")
                identities[source.name] = sha256(target)
            proof["services"][name] = {"installedDirectory": str(actual), "sha256": identities}
            save(output / "managed-binding.json", proof)
    return {"matchedServices": sorted(proof["services"])}


@contextmanager
def deadline(seconds):
    def expired(signum, frame):
        raise TimeoutError("HTTP observation deadline exceeded")
    if seconds <= 0:
        raise TimeoutError("observation deadline expired")
    started = time.monotonic()
    prior_timer = signal.getitimer(signal.ITIMER_REAL)
    previous = signal.signal(signal.SIGALRM, expired)
    signal.setitimer(signal.ITIMER_REAL, min(seconds, prior_timer[0]) if prior_timer[0] > 0 else seconds)
    try:
        yield
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0)
        signal.signal(signal.SIGALRM, previous)
        remaining = prior_timer[0] - (time.monotonic() - started)
        if prior_timer[0] > 0 and remaining > 0:
            signal.setitimer(signal.ITIMER_REAL, remaining, prior_timer[1])


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, newurl):
        return None


def response(base, suffix, limit):
    parsed = urllib.parse.urlsplit(base)
    if (parsed.scheme not in ("http", "https") or parsed.hostname not in ("localhost", "127.0.0.1", "::1")
            or parsed.username or parsed.password or parsed.query or parsed.fragment):
        raise ValueError("acceptance observes only configured local service endpoints")
    url = urllib.parse.urlunsplit((parsed.scheme, parsed.netloc, parsed.path.rstrip("/") + suffix, "", ""))
    started = time.monotonic()
    with deadline(10):
        with urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect).open(url, timeout=8) as result:
            body = result.read(limit + 1)
            if len(body) > limit:
                raise ValueError("service observation exceeded byte envelope")
            return body, {"httpStatus": result.status, "responseSeconds": time.monotonic() - started,
                          "contentType": result.headers.get_content_type()}


def lichess_status(payload):
    if not isinstance(payload, dict):
        raise ValueError("invalid Lichess status shape")
    service = {key: payload[key] for key in ("configured", "connected", "running", "substrate")
               if type(payload.get(key)) is bool}
    service.update({key: payload[key] for key in ("depth", "maxConcurrent", "gamesRecorded")
                    if type(payload.get(key)) is int and payload[key] >= 0})
    service["errorPresent"] = bool(payload.get("error"))
    account = payload.get("account") if isinstance(payload.get("account"), dict) else {}
    keys = ("tokenValid", "botAccount", "botPlayScope", "ready")
    sanitized = {key: account.get(key) if type(account.get(key)) is bool else None for key in keys}
    sanitized["errorPresent"] = bool(account.get("error"))
    ready = (all(service.get(key) is True for key in ("configured", "connected", "running"))
             and all(sanitized[key] is True for key in keys)
             and not service["errorPresent"] and not sanitized["errorPresent"])
    return {"service": service, "account": sanitized, "ready": ready,
            "verification": "Read-only configured account and event-stream state; no remote game or account mutation."}


def services(output):
    base = os.environ.get("LAPLACE_API_BASE", "http://127.0.0.1:5187")
    proof = {"coldBootMeasured": False, "scope": "Current response latency, not boot duration"}
    save(output / "services.json", proof)
    health, proof["api"] = response(base, "/health", 65536)
    if json.loads(health).get("status") != "ok":
        raise ValueError("API health is not ok")
    body, proof["ui"] = response(base, "/", 4 << 20)
    if proof["ui"]["contentType"] != "text/html" or not body:
        raise ValueError("installed UI did not return HTML")
    save(output / "services.json", proof)
    for unit in ("laplace-api", "laplace-mcp", "laplace-lichess"):
        command(["systemctl", "show", unit, "--no-pager",
                 "--property=Id,ActiveState,SubState,MainPID,ExecMainStartTimestamp,ActiveEnterTimestamp,ExecMainStartTimestampMonotonic,ActiveEnterTimestampMonotonic"],
                output / (unit + "-state.txt"), 15)
    raw, http = response(os.environ.get("LAPLACE_LICHESS_STATUS_BASE", "http://127.0.0.1:5189"), "/status", 65536)
    online = lichess_status(json.loads(raw))
    online["http"] = http
    save(output / "lichess-readiness.json", online)
    if online["service"].get("configured") is True and not online["ready"]:
        raise ValueError("configured Lichess account/event stream is not ready; inspect sanitized receipt")
    return {"apiAndUiReady": True, "lichessReady": online["ready"],
            "lichessConfigured": online["service"].get("configured")}


def unit_states():
    result = {}
    for unit in ("laplace-api", "laplace-mcp", "laplace-lichess"):
        completed = subprocess.run(["systemctl", "show", unit, "--no-pager",
                                    "--property=MainPID,ExecMainStartTimestampMonotonic,ActiveState,SubState"],
                                   check=True, capture_output=True, text=True, timeout=15)
        values = dict(line.split("=", 1) for line in completed.stdout.splitlines() if "=" in line)
        result[unit] = {"pid": int(values.get("MainPID", "0")),
                        "startMonotonicUsec": int(values.get("ExecMainStartTimestampMonotonic", "0")),
                        "activeState": values.get("ActiveState"), "subState": values.get("SubState")}
    return result


def measure_service_startup(output, readiness_timeout=60):
    directory = output / "service-startup"
    directory.mkdir()
    proof = {"status": "failed", "scope": "Service restart request through full application/UI and configured account/stream readiness; existing OS caches",
             "machineColdBootMeasured": False, "services": {}}
    save(directory / "receipt.json", proof)
    started = None
    try:
        before = unit_states()
        proof["before"] = before
        save(directory / "receipt.json", proof)
        started = time.monotonic()
        command(["sudo", "-n", "systemctl", "restart", "laplace-api"], directory / "api-restart.log", 180)
        command(["bash", "deploy/linux/managed-publish.sh", "activate"], directory / "managed-activate.log", 180)
        command([sys.executable, "scripts/verify-application-release.py",
                 "--state-file", directory / "application-readiness.json", "--timeout-seconds", "60"],
                directory / "application-readiness.log", 90)
        if not 0 < readiness_timeout <= 60:
            raise ValueError("startup readiness deadline must be in (0, 60] seconds")
        readiness_deadline = time.monotonic() + readiness_timeout
        proof["readinessDeadlineSeconds"] = readiness_timeout
        proof["readinessAttempts"] = []
        while True:
            remaining = readiness_deadline - time.monotonic()
            if remaining <= 0:
                raise ValueError("service startup full-readiness deadline expired")
            attempt = {"number": len(proof["readinessAttempts"]) + 1, "status": "failed"}
            proof["readinessAttempts"].append(attempt)
            readiness = directory / ("readiness-" + str(attempt["number"]).zfill(3))
            readiness.mkdir()
            try:
                with deadline(remaining):
                    proof["readiness"] = services(readiness)
                attempt["status"] = "passed"
                save(directory / "receipt.json", proof)
                break
            except (OSError, ValueError, TimeoutError, subprocess.SubprocessError) as error:
                attempt["failureType"] = type(error).__name__
                save(directory / "receipt.json", proof)
            remaining = readiness_deadline - time.monotonic()
            if remaining > 0:
                time.sleep(min(1, remaining))
        after = unit_states()
        proof["after"] = after
        for name, current in after.items():
            prior = before[name]
            if current["pid"] <= 0:
                proof["services"][name] = {"started": False, "claim": "not running; no startup claim"}
                if name == "laplace-api" or prior["pid"] > 0:
                    raise ValueError(f"{name} is not running after its startup owner")
                continue
            if (current["pid"], current["startMonotonicUsec"]) == (prior["pid"], prior["startMonotonicUsec"]):
                raise ValueError(f"{name} did not create a new process instance")
            if current["activeState"] != "active" or current["startMonotonicUsec"] <= 0:
                raise ValueError(f"{name} is not active with a measured process start")
            proof["services"][name] = {"started": True, **current}
        proof["status"] = "passed"
        return {"serviceRestartStartupMeasured": True,
                "restartToFullReadinessSeconds": time.monotonic() - started,
                "services": proof["services"], "readiness": proof["readiness"]}
    finally:
        if started is not None:
            proof["wallSecondsIncludingFailedReadiness"] = time.monotonic() - started
        save(directory / "receipt.json", proof)


def public_summary(name, output):
    """Emit bounded, typed fields from the existing validators, never raw auth."""
    paths = {
        "native-binding": "native-before.json", "native-binding-after": "native-after.json",
        "requested-source-directory": "requested-stockfish-directory.json",
        "stockfish-corpus": "stockfish-corpus/receipt.json",
        "recorded": "recorded/recorded-chess/receipt.json",
        "retained": "retained/receipt.json",
        "retained-capacity-prepare": "retained-capacity-corpus/corpus.json",
        "retained-capacity-measure": "retained-capacity-measurement/receipt.json",
        "geometry": "geometry/postgres-geometry/receipt.json",
        "calibration": "calibration/chess-environment/report.json",
        "gui-session": "gui-session/receipt.json", "service-startup": "service-startup/receipt.json",
        "gui-x11-runtime": "x11-runtime.json",
    }
    if name not in paths:
        return
    path = output / paths[name]
    if not path.is_file():
        print("CHESS_ACCEPTANCE_RESULT " + json.dumps({"phase": name, "receipt": paths[name],
                                                      "available": False}), flush=True)
        return
    if path.stat().st_size > 32 << 20:
        raise ValueError("summary receipt exceeded its byte envelope")
    value = json.loads(path.read_text())
    summary = {"phase": name, "receipt": paths[name], "status": value.get("status")}
    def fields(source, keys):
        return {key: source[key] for key in keys if key in source and
                (source[key] is None or type(source[key]) in (bool, int, float))}
    if name == "service-startup":
        summary.update(value)
    elif name == "requested-source-directory":
        summary.update(value)
    elif name.startswith("native-binding"):
        summary.update(nativeFingerprint=value.get("native_fingerprint"), artifacts=value.get("artifacts"))
    elif name == "stockfish-corpus":
        summary.update(fields(value, ("native_exact_readback", "repeat_without_amplification", "selected_files",
                                      "tracked_entries", "full_tracked_corpus")))
        summary["coverageScope"] = value.get("coverage_scope")
        summary["requestedCoverage"] = value.get("requested_coverage")
        summary["commit"] = value.get("commit")
        summary["coverage"] = value.get("coverage")
    elif name == "recorded":
        summary.update(fields(value, ("targetMet", "durationQualified", "correctnessSweepNewlyRecordedPlayings")))
        summary["cases"] = [{"status": item.get("status"), **fields(item,
            ("concurrency", "repeat", "verifiedRecordedGames", "verifiedRecordedPlies",
             "gamesPerSecondServerWorkflow", "gamesPerSecondEndToEnd",
             "gamesPerSecondRecordingAndReadback"))} for item in value.get("cases", [])[:12]]
        window = value.get("sustained") or {}
        summary["sustained"] = {"status": window.get("status"), **fields(window,
            ("durationQualified", "elapsedSeconds", "verifiedRecordedGames", "verifiedRecordedPlies",
             "gamesPerSecondSustainedEndToEnd", "gamesPerDayExtrapolated"))}
    elif name == "retained":
        summary["attempts"] = [{"kind": item.get("kind"), **fields(item,
            ("parsedGames", "newlyRecordedGames", "newlyRecordedPlayings", "pliesReadback",
             "serviceElapsedSeconds", "collectorElapsedSeconds", "newlyRecordedGamesPerSecondService",
             "processedGamesPerSecondService", "processedGamesPerSecondCollector"))}
             for item in value.get("attempts", [])[:3]]
        summary.update(fields(value, ("minimumAdmissionDurationReached", "sustainedAdmissionCapacityEstablished")))
    elif name == "retained-capacity-prepare":
        summary.update(fields(value, ("requestedGames", "retainedBytes", "commandSecondsIncludingCleanup")))
        summary["preparedJobs"] = len(value.get("jobs", []))
    elif name == "retained-capacity-measure":
        summary["scope"] = "Prepared authentic full games, sequential fresh admission; engine generation excluded"
        summary.update(fields(value, ("requestedGames", "admissionConcurrency", "admissionWindowSeconds")))
        measured = value.get("measurement") or {}
        summary["measurement"] = fields(measured,
            ("newlyRecordedPlayings", "newlyRecordedCompleteGames", "endToEndAdmissionSeconds",
             "observedNewPlayingsPerSecond", "minimumAdmissionSeconds", "minimumDurationReached",
             "variedOrderedLineCorpus", "exactReplayControlsPassed",
             "sustainedIngestionCapacityEstablished", "targetGamesPerSecond", "pliesReadback"))
        summary["measurement"]["targetVerdict"] = measured.get("targetVerdict")
        summary["measurement"]["contentInventory"] = fields(measured.get("contentInventory") or {},
            ("playings", "distinctOrderedLines", "distinctStartPositions", "distinctLines",
             "repeatedLinePlayings", "repeatedOrderedLinePlayings", "maximumPlayingsSharingLine",
             "multipleStartPositionsAndLines"))
        summary["measurement"]["writer"] = fields(measured.get("writer") or {},
            ("applyCalls", "entitiesAttempted", "entitiesInserted", "physicalitiesAttempted",
             "physicalitiesInserted", "attestationsAttempted", "attestationsInserted",
             "copyTransactionsStarted", "copyTransactionsCommitted"))
    elif name == "geometry":
        summary["scope"] = "Storage rows and vertices; not recorded games"
        summary["cases"] = [{"mode": item.get("mode"), "status": item.get("status"), **fields(item,
            ("concurrency", "repeat", "rows_per_second", "trajectory_vertices_per_second",
             "copy_and_commit_seconds", "readback_seconds", "exact_committed_readback"))}
             for item in value.get("cases", [])[:18]]
    elif name == "calibration":
        summary["plan"] = value.get("plan")
        summary["stockfishNodesPerSecond"] = [item.get("steady_nodes_per_second") for item in value.get("stockfish_bench", [])[:24]]
        summary["cutechessPliesPerSecond"] = [item.get("steady_plies_per_second") for item in value.get("cutechess_matches", [])[:24]]
        summary["recommendations"] = value.get("recommendations")
    elif name == "gui-x11-runtime":
        summary["scope"] = "Authenticated tool and library selection; actual GUI interaction is a separate phase"
        summary["mode"] = value.get("mode")
        summary.update(fields(value, ("gui_ready", "host_packages_installed")))
        summary["selectionSha256"] = value.get("selection_sha256")
        names = {"xvfb-run", "Xvfb", "xauth", "xdotool", "xprop", "xwininfo", "xkbcomp"}
        summary["initiallyMissingTools"] = [item for item in value.get("initially_missing_tools", []) if item in names]
        summary["tools"] = {key: path for key, path in value.get("tools", {}).items()
                            if key in names and isinstance(path, str) and len(path) <= 4096}
        private = value.get("private_runtime") or {}
        summary["privateRuntime"] = {key: private.get(key) for key in ("runtime_id", "manifest_sha256")}
        summary["privateRuntime"]["packages"] = [
            {key: item.get(key) for key in ("name", "version", "architecture", "sha256")}
            for item in private.get("packages", [])[:64]]
    elif name == "gui-session":
        summary.update(fields(value, ("windowObserved", "interactionVerified", "cleanupVerified",
                                      "elapsed_seconds", "interactive_session_verified", "qapplication_event_loop_verified",
                                      "virtual_x11_interaction_verified", "clean_exit_verified",
                                      "whole_session_seconds_including_cleanup", "operator_desktop_tested")))
    print("CHESS_ACCEPTANCE_RESULT " + json.dumps(summary, allow_nan=False), flush=True)


def source_directory_inventory(path):
    """Inventory names and kinds only; never read source/config file contents."""
    result = {"requestedPath": str(path), "exists": path.exists(), "entries": [],
              "nestedGitCandidates": [], "limits": {"entries": 128, "directories": 128,
                                                   "gitCandidates": 8, "depth": 2}}
    if not path.exists():
        return result
    if not path.is_dir():
        result["kind"] = "non-directory"
        return result
    result["kind"] = "directory"
    def entries(directory):
        with os.scandir(directory) as stream:
            return sorted(islice(stream, 129), key=lambda p: p.name)
    def kind(item):
        return "symlink" if item.is_symlink() else "directory" if item.is_dir(follow_symlinks=False) else "file"
    direct = entries(path)
    result["entriesTruncated"] = len(direct) > 128
    result["entries"] = [{"name": item.name, "kind": kind(item)} for item in direct[:128]]
    result["sourceLayout"] = {name: (path / name).is_file()
                              for name in ("src/Makefile", "src/uci.cpp", "CMakeLists.txt", "README.md")}
    pending = [(path, 0)]
    scanned = 0
    env = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")}
    env.update(GIT_CONFIG_GLOBAL="/dev/null", GIT_CONFIG_NOSYSTEM="1", GIT_NO_REPLACE_OBJECTS="1",
               GIT_TERMINAL_PROMPT="0")
    while pending and scanned < 128:
        directory, depth = pending.pop(0)
        scanned += 1
        marker = directory / ".git"
        if marker.exists() and len(result["nestedGitCandidates"]) < 8:
            candidate = {"path": str(directory), "markerKind": "directory" if marker.is_dir() else "file",
                         "verifiedCheckout": False}
            try:
                def git(*arguments):
                    return subprocess.check_output(["git", "-C", str(directory), *arguments],
                                                   env=env, text=True, stderr=subprocess.DEVNULL, timeout=2).strip()
                candidate["verifiedCheckout"] = git("rev-parse", "--is-inside-work-tree") == "true"
                commit = git("rev-parse", "--verify", "HEAD")
                if not re.fullmatch(r"[0-9a-f]{40}", commit):
                    raise ValueError("invalid checkout commit")
                candidate["commit"] = commit
                origin = git("remote", "get-url", "origin").rstrip("/")
                official = origin in ("https://github.com/official-stockfish/Stockfish",
                                      "https://github.com/official-stockfish/Stockfish.git",
                                      "git@github.com:official-stockfish/Stockfish",
                                      "git@github.com:official-stockfish/Stockfish.git")
                candidate["officialOrigin"] = official
                candidate["origin"] = "https://github.com/official-stockfish/Stockfish" if official else "not-the-official-origin"
            except (OSError, ValueError, subprocess.SubprocessError) as error:
                candidate["failureType"] = type(error).__name__
            result["nestedGitCandidates"].append(candidate)
        if depth < 2:
            for item in entries(directory)[:128]:
                if item.name != ".git" and item.is_dir(follow_symlinks=False):
                    pending.append((Path(item.path), depth + 1))
    result["directoriesScanned"] = scanned
    result["directoryScanTruncated"] = bool(pending)
    return result


def retained_capacity(owner, python, *, allowed):
    """Prepare the authentic pool completely before any measured admission."""
    corpus = owner.output / "retained-capacity-corpus"
    measured = owner.output / "retained-capacity-measurement"
    prepared = owner.run("retained-capacity-prepare",
        [python, "scripts/benchmark-retained-chess-capacity.py", "prepare",
         "--output-dir", corpus, "--jobs", "2", "--games-per-job", "24",
         "--depth", "4", "--concurrency", "1", "--timeout-seconds", "3600"],
        3630, allowed=allowed)
    # No restart or new match job may occur between these adjacent operations.
    return owner.run("retained-capacity-measure",
        [python, "scripts/benchmark-retained-chess-capacity.py", "measure",
         "--corpus-dir", corpus, "--output-dir", measured,
         "--minimum-seconds", "30", "--replays", "1",
         "--target-games-per-second", "2500", "--timeout-seconds", "3600"],
        3630, allowed=allowed and prepared)


def recording_checks(owner, python, *, allowed):
    """Prove complete admission/replay before the independent full rate sweep."""
    owner.run("retained", [python, "scripts/benchmark-retained-chess-ingestion.py",
              "--output-dir", owner.output / "retained", "--games", "16", "--depth", "4",
              "--concurrency", "1", "--replays", "2", "--timeout-seconds", "900"], 930,
              allowed=allowed)
    owner.run("recorded", [python, "scripts/benchmark_suite.py", "run", "--suite", "recorded",
              "--repeats", "3", "--receipt-dir", owner.output / "recorded",
              "--recorded-games", "24", "--recorded-depth", "4", "--recorded-concurrency", "1,2,4",
              "--recorded-duration-seconds", "30", "--recorded-total-timeout", "7200"], 7230,
              allowed=allowed)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", required=True, type=Path)
    parser.add_argument("--prefix", type=Path, default=Path(os.environ.get("LAPLACE_INSTALL_PREFIX", "/opt/laplace")))
    parser.add_argument("--work", type=Path, default=Path("/build/laplace/work"))
    args = parser.parse_args()
    if not args.prefix.is_absolute():
        parser.error("--prefix must be absolute")
    os.environ["LAPLACE_INSTALL_PREFIX"] = str(args.prefix)
    def interrupted(signum, frame):
        raise KeyboardInterrupt("acceptance interrupted")
    signal.signal(signal.SIGTERM, interrupted)
    args.work.mkdir(parents=True, exist_ok=True)
    owner = Acceptance(args.output_dir)
    python = sys.executable
    def source_identity():
        revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True, timeout=10).strip()
        if revision != os.environ.get("LAPLACE_BENCH_SHA", revision) or not re.fullmatch(r"[0-9a-f]{40}", revision):
            raise ValueError("checkout does not match requested exact source")
        changed = subprocess.check_output(["git", "diff", "--name-only", "HEAD"], cwd=ROOT, text=True, timeout=10)
        if changed:
            raise ValueError("tracked source differs from checked out revision")
        owner.receipt["sourceSha"] = revision
        return {"sourceSha": revision}
    exact_source = owner.phase("source", source_identity)
    def inventory():
        result = source_directory_inventory(Path("/vault/External/Stockfish/SF_19"))
        save(args.output_dir / "requested-stockfish-directory.json", result)
        return {"exists": result["exists"], "gitCandidates": len(result["nestedGitCandidates"])}
    owner.phase("requested-source-directory", inventory)
    # These are the publication owner's existing exact installed-form controls.
    native = owner.run("native-binding", [python, "scripts/check-application-runtime.py",
                       "--snapshot", args.output_dir / "native-before.json"], 240, allowed=exact_source)
    managed = owner.phase("managed-binding", lambda: managed_binding(args.output_dir, args.prefix, args.work),
                          allowed=exact_source)
    owner.phase("services-before", lambda: services(args.output_dir), required=False)
    binding = exact_source and native and managed
    x11 = owner.run("gui-x11-runtime", [python, "scripts/chess-x11-runtime.py",
              "--root", args.prefix / "tools/chess/x11-runtime", "--deadline-seconds", "280",
              "--output", args.output_dir / "x11-runtime.json",
              "--evidence-output", args.output_dir / "x11-runtime-evidence"], 330)
    owner.run("provision-chess", ["bash", "scripts/bootstrap-chess-lab.sh", "--cutechess-gui"], 3600)
    runtime = [python, "scripts/chess-runtime-env.py", "--prefix", str(args.prefix), "--"]
    owner.run("dependencies", [*runtime, python, "scripts/check-chess-dependencies.py",
              "--prefix", args.prefix, "--uci", args.prefix / "app/laplace-uci",
              "--check-latest", "--cutechess-gui"], 300)
    def gui():
        doctor = module("acceptance_chess_configuration", "check-chess-dependencies.py")
        config = doctor.configuration(args.prefix, keys={"LAPLACE_CUTECHESS_GUI", "LAPLACE_CUTECHESS_GUI_RECEIPT"})
        command([python, "scripts/check-cutechess-gui-session.py",
                 "--binary", config.get("LAPLACE_CUTECHESS_GUI", str(args.prefix / "bin/cutechess")),
                 "--receipt", config.get("LAPLACE_CUTECHESS_GUI_RECEIPT", "/build/cutechess/laplace-cutechess-gui-build.json"),
                 "--output-dir", args.output_dir / "gui-session", "--work", args.work,
                 "--x11-runtime-receipt", args.output_dir / "x11-runtime.json",
                 "--timeout-seconds", "60"], args.output_dir / "gui-session.log", 90)
    owner.phase("gui-session", gui, allowed=x11)
    owner.run("runtime", [*runtime, python, "scripts/benchmark-chess-environment.py", "--runtime-only",
              "--output-dir", args.output_dir / "runtime", "--reserve-cpus", "0", "--cpu-budget", "1",
              "--memory-mb", "512", "--max-seconds", "60"], 90)
    startup = owner.phase("service-startup", lambda: measure_service_startup(args.output_dir))
    owner.receipt["serviceRestartStartupMeasured"] = startup
    owner.flush()
    cli = owner.run("cli-build", ["dotnet", "build", "app/Laplace.Cli/Laplace.Cli.csproj",
                    "-c", "Release", "--nologo", "-v", "minimal"], 900, allowed=binding)
    synced = owner.run("cli-native-sync", ["bash", "scripts/sync-managed-native-artifacts.sh"], 120, allowed=cli)
    owner.run("stockfish-corpus", [python, "scripts/ingest-stockfish-corpus.py", "--prefix", args.prefix,
              "--output", args.output_dir / "stockfish-corpus", "--coverage", "all-tracked"], 3600, allowed=binding and synced)
    # Independent measurements still run after a failed match or corpus proof.
    recording_checks(owner, python, allowed=binding)
    retained_capacity(owner, python, allowed=binding)
    owner.run("geometry", [python, "scripts/benchmark_suite.py", "run", "--suite", "geometry",
              "--database", os.environ.get("PGDATABASE", "laplace"), "--repeats", "3",
              "--receipt-dir", args.output_dir / "geometry", "--geometry-rows", "100000",
              "--geometry-transaction-rows", "10000", "--geometry-concurrency", "1,2,4",
              "--geometry-timeout", "900"], 930, allowed=native)
    def calibration():
        doctor = module("acceptance_chess_calibration", "check-chess-dependencies.py")
        configured = doctor.configuration(args.prefix, keys={"LAPLACE_UCI"})
        uci = Path(configured.get("LAPLACE_UCI", str(args.prefix / "app/laplace-uci")))
        if not uci.is_file() or not os.access(uci, os.X_OK):
            raise ValueError("configured Laplace UCI executable is unavailable")
        command([*runtime, python, "scripts/benchmark_suite.py", "run", "--suite", "chess",
                 "--repeats", "3", "--receipt-dir", args.output_dir / "calibration",
                 "--chess-reserve-cpus", "2", "--chess-max-seconds", "1800",
                 "--chess-case-timeout", "600", "--chess-laplace-uci", str(uci)],
                args.output_dir / "calibration.log", 1830)
    owner.phase("calibration", calibration)
    owner.run("native-binding-after", [python, "scripts/check-application-runtime.py",
              "--compare", args.output_dir / "native-before.json",
              "--snapshot", args.output_dir / "native-after.json"], 240, allowed=native)
    after = args.output_dir / "after"
    after.mkdir()
    owner.phase("services-after", lambda: services(after))
    return owner.finish()


if __name__ == "__main__":
    raise SystemExit(main())
