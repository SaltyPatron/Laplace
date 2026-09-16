#!/usr/bin/env python3
"""Measure authentic source PGN through the existing source-built CLI and installed DB.

The workflow owns the shared host lock. Inventory is read-only; measurement uses the
ordinary CLI/native build owners and never deletes, reseeds or rewrites source games.
"""
import argparse
import datetime
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = "laplace.installed-native-corpus-measurement/v1"


def acceptance_owner():
    spec = importlib.util.spec_from_file_location("corpus_acceptance_owner", ROOT / "scripts/accept-chess-environment.py")
    owner = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = owner
    spec.loader.exec_module(owner)
    return owner


def inventory(chess_root, limit=4096, seconds=30):
    """Observe only configured source families; stop rather than walk an entire vault."""
    root = chess_root.resolve()
    started = time.monotonic()
    result = {"root": str(root), "scope": "Observed local filesystem; remote acquisition not authenticated",
              "files": [], "missingDirectories": [], "complete": False}
    directories = [(root / "Lumbras/otb", "lumbras-otb"),
                   (root / "Lumbras/pgn", "lumbras-elite"),
                   (root / "twic", "twic")]
    observed = 0
    for directory, family in directories:
        current = root
        linked_ancestor = False
        for part in directory.relative_to(root).parts:
            current = current / part
            linked_ancestor |= current.is_symlink()
        if not directory.is_dir() or linked_ancestor:
            result["missingDirectories"].append(str(directory))
            continue
        with os.scandir(directory) as items:
            for entry in items:
                observed += 1
                if observed > limit or time.monotonic() - started > seconds:
                    result["reason"] = "inventory envelope exceeded"
                    return result
                lower = entry.name.lower()
                plain = lower.endswith(".pgn")
                if not (plain or lower.endswith((".pgn.zst", ".pgn.gz", ".pgn.zip", ".zip"))) or not entry.is_file(follow_symlinks=False):
                    continue
                if family == "lumbras-elite" and not entry.name.startswith("export_ELO2400.pgn"):
                    continue
                path = Path(entry.path)
                details = entry.stat(follow_symlinks=False)
                result["files"].append({"path": str(path), "family": family, "bytes": details.st_size,
                                        "mtimeNs": details.st_mtime_ns, "regularFile": True,
                                        "resolvedPath": str(path.resolve()), "eligiblePlainPgn": plain})
    result["files"].sort(key=lambda row: row["path"])
    result["complete"] = True
    return result


def select_source(observed, requested, family):
    if requested:
        path = Path(requested)
        if not path.is_absolute():
            raise ValueError("source PGN path must be absolute")
        path = path.resolve(strict=True)
        if not stat.S_ISREG(path.stat().st_mode) or path.suffix.lower() != ".pgn":
            raise ValueError("source must be an existing plain regular PGN file")
        return path
    if not observed["complete"]:
        raise ValueError("automatic selection requires a complete bounded source inventory")
    candidates = [row for row in observed["files"] if row["bytes"] > 0 and row["eligiblePlainPgn"] and (
        family == "otb-2025" and row["family"] == "lumbras-otb" and "2025" in Path(row["path"]).name
        or family == "elite" and row["family"] == "lumbras-elite")]
    if len(candidates) != 1:
        raise ValueError("source selection requires exactly one observed family file or an explicit PGN path")
    return Path(candidates[0]["path"])


def source_identity(owner, path):
    observer = owner.module("corpus_source_file_identity", "lib/chess_corpus_inventory.py")
    result = observer._file(path)
    if result["status"] != "present":
        raise ValueError("source file identity is not stable: " + result["status"])
    return {**result, "acquisitionScope": "Observed unchanged local file; remote download provenance is not inferred"}


def exact_source(expected):
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True, timeout=10).strip()
    if not re.fullmatch(r"[0-9a-f]{40}", head) or head != expected:
        raise ValueError("checkout differs from the requested exact source")
    changed = subprocess.check_output(["git", "diff", "--name-only", "HEAD"], cwd=ROOT, text=True, timeout=10)
    if changed:
        raise ValueError("tracked source differs from the requested revision")
    return head


def cli_identity(owner):
    directory = ROOT / "app/Laplace.Cli/bin/Release/net10.0"
    required = ["Laplace.Cli.dll", "Laplace.Cli.deps.json", "Laplace.Cli.runtimeconfig.json",
                "liblaplace_core.so", "liblaplace_dynamics.so", "liblaplace_synthesis.so"]
    if any(not (directory / name).is_file() for name in required):
        raise ValueError("exact Release CLI/native output is incomplete")
    files = sorted(path for path in directory.iterdir()
                   if path.is_file() and (path.suffix in (".dll", ".so", ".json")))
    identities = {path.name: owner.sha256(path) for path in files}
    for component in ("core", "dynamics", "synthesis"):
        filename = "liblaplace_" + component + ".so"
        if identities[filename] != owner.sha256(ROOT / "build/engine" / component / filename):
            raise ValueError("CLI app-local native image differs from the shared exact build")
    return {"directory": str(directory), "sha256": identities,
            "launcherSha256": owner.sha256(ROOT / "scripts/laplace"),
            "scope": "Existing source-built Release CLI, not a separately installed CLI payload; installed-form native/DB binding is proved separately"}


def database_target():
    expected = "Host=/var/run/postgresql;Username=laplace_admin;Database=laplace"
    if (os.environ.get("PGHOST", "/var/run/postgresql") != "/var/run/postgresql"
            or os.environ.get("PGDATABASE", "laplace") != "laplace"
            or os.environ.get("PGUSER", "laplace_admin") != "laplace_admin"
            or os.environ.get("PGPORT", "5432") != "5432"
            or os.environ.get("LAPLACE_DB", expected) != expected):
        raise ValueError("CLI and native guard must select the same local laplace database")
    os.environ["LAPLACE_DB"] = expected
    return {"socket": "/var/run/postgresql", "port": 5432,
            "database": "laplace", "role": "laplace_admin"}


def measure_arguments(source, output, games, seconds):
    return ["bash", str(ROOT / "scripts/laplace"), "chess", "measure-corpus",
            "--pgn", source["path"], "--expected-sha256", source["sha256"],
            "--evidence-root", str(output / "measurement"), "--games", str(games),
            "--minimum-seconds", "30", "--replays", "1", "--deadline-seconds", str(seconds)]


def summary(receipt, games, source):
    if receipt.get("schema") != "laplace.chess-corpus-capacity/v1":
        raise ValueError("unknown corpus measurement receipt")
    if (receipt.get("status") != "completed" or receipt.get("newlyRecordedGames") != games
            or receipt.get("readbackGames") != games or receipt.get("alreadyPresentGames") != 0):
        raise ValueError("corpus measurement did not newly record and exactly read back every selected playing")
    observed = (receipt.get("source") or {}).get("source") or {}
    if observed.get("sha256") != source["sha256"] or observed.get("bytes") != source["bytes"]:
        raise ValueError("measured source differs from the observed file identity")
    phases = receipt.get("phases") or []
    if len(phases) != 2 or any(phase.get("status") != "completed" for phase in phases):
        raise ValueError("fresh admission and exact replay are not both complete")
    replay = phases[1]
    writer = replay.get("writer") or {}
    counters = ("applyCalls", "entitiesAttempted", "entitiesInserted", "physicalitiesAttempted",
                "physicalitiesInserted", "attestationsAttempted", "attestationsInserted",
                "entitiesSkippedAtMerge", "physicalitiesSkippedAtMerge", "roundTrips",
                "copyTransactionsStarted", "copyTransactionsCommitted", "journalReplayHits")
    if (replay.get("novelGames") != 0 or replay.get("appliedGames") != 0
            or replay.get("readbackGames") != games or replay.get("durability") is not None
            or any(type(writer.get(key)) is not int or writer[key] != 0 for key in counters)):
        raise ValueError("replay did not preserve exact zero-writer accounting")
    rate = receipt.get("recordedGamesPerSecond")
    elapsed = (receipt.get("elapsedSeconds") or {}).get("freshAdmission")
    if (type(rate) not in (int, float) or type(elapsed) not in (int, float)
            or not math.isfinite(rate) or not math.isfinite(elapsed)
            or elapsed <= 0 or abs(rate - games / elapsed) > max(1e-9, abs(rate) * 1e-9)):
        raise ValueError("recorded rate does not match the complete fresh numerator/window")
    qualified = receipt.get("qualifiedWindow")
    target = receipt.get("targetMet")
    if type(qualified) is not bool or type(target) is not bool or target != (qualified and rate >= 2500):
        raise ValueError("corpus target qualification is inconsistent")
    if qualified and (elapsed < 30 or (receipt.get("source") or {}).get("distinctLines", 0) < 2):
        raise ValueError("short admission cannot qualify a sustained target")
    return {key: receipt[key] for key in ("status", "newlyRecordedGames", "readbackGames",
            "recordedGamesPerSecond", "qualifiedWindow", "targetMet", "targetVerdict")}


def run(args, owner=None):
    owner = owner or acceptance_owner()
    args.output_dir.mkdir(parents=True, exist_ok=False)
    proof = {"schema": SCHEMA, "status": "failed", "mode": args.mode, "phases": [],
             "observedUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
             "executionScope": "Source-built CLI using existing installed PostgreSQL/native runtime",
             "targetGamesPerSecond": 2500, "targetMet": False}
    def checkpoint():
        owner.save(args.output_dir / "receipt.json", proof)
    def phase(name, action):
        row = {"name": name, "status": "running"}
        proof["phases"].append(row)
        checkpoint()
        started = time.monotonic()
        try:
            value = action()
            row["status"] = "passed"
            return value
        except BaseException as error:
            row.update(status="failed", errorType=type(error).__name__)
            raise
        finally:
            row["wallSeconds"] = time.monotonic() - started
            checkpoint()
    def command(name, argv, timeout):
        return phase(name, lambda: owner.command(argv, args.output_dir / (name + ".log"), timeout))
    native_bound = False
    before_cli = None
    measured = None
    try:
        proof["sourceSha"] = phase("source", lambda: exact_source(args.expected_source))
        observed = phase("inventory", lambda: inventory(args.chess_root))
        owner.save(args.output_dir / "source-inventory.json", observed)
        proof["inventory"] = {"files": len(observed["files"]), "complete": observed["complete"]}
        if args.mode == "inventory":
            proof["status"] = "observed"
            return 0
        source = phase("source-file", lambda: source_identity(owner, select_source(observed, args.pgn, args.family)))
        owner.save(args.output_dir / "source-file.json", source)
        proof["sourceFile"] = source
        proof["databaseTarget"] = phase("database-target", database_target)
        command("native-before", [sys.executable, ROOT / "scripts/check-application-runtime.py",
                                  "--snapshot", args.output_dir / "native-before.json"], 240)
        native_bound = True
        command("cli-build", ["dotnet", "build", ROOT / "app/Laplace.Cli/Laplace.Cli.csproj",
                             "-c", "Release", "--nologo", "-v", "minimal"], 900)
        command("cli-native-sync", ["bash", ROOT / "scripts/sync-managed-native-artifacts.sh"], 120)
        before_cli = phase("cli-identity", lambda: cli_identity(owner))
        owner.save(args.output_dir / "cli-before.json", before_cli)
        command("measurement", measure_arguments(source, args.output_dir, args.games, args.deadline_seconds),
                args.deadline_seconds + 30)
        measured = phase("receipt", lambda: summary(json.loads(
            (args.output_dir / "measurement/corpus-recording.json").read_text()), args.games, source))
        proof["measurement"] = measured
        proof["status"] = "completed"
    except KeyboardInterrupt:
        proof.update(status="interrupted", errorType="KeyboardInterrupt")
    except Exception as error:
        proof.update(status="failed", errorType=type(error).__name__, error=str(error))
    finally:
        # Both checks still run after a failed admission; each failure remains visible.
        if before_cli is not None:
            try:
                after_cli = phase("cli-identity-after", lambda: cli_identity(owner))
                owner.save(args.output_dir / "cli-after.json", after_cli)
                if before_cli != after_cli:
                    raise ValueError("CLI/native source-built payload changed during admission")
            except BaseException as error:
                proof.update(status="failed", postflightErrorType=type(error).__name__)
        if before_cli is not None:
            try:
                phase("source-after", lambda: exact_source(args.expected_source))
            except BaseException as error:
                proof.update(status="failed", sourcePostflightErrorType=type(error).__name__)
        if native_bound:
            try:
                command("native-after", [sys.executable, ROOT / "scripts/check-application-runtime.py",
                        "--compare", args.output_dir / "native-before.json",
                        "--snapshot", args.output_dir / "native-after.json"], 240)
            except BaseException as error:
                proof.update(status="failed", nativePostflightErrorType=type(error).__name__)
        proof["targetMet"] = bool(proof["status"] == "completed" and measured and measured["targetMet"])
        proof["finishedUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        checkpoint()
        print("CORPUS_CAPACITY_RESULT " + json.dumps(proof, allow_nan=False), flush=True)
    return 0 if proof["status"] == "completed" else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("inventory", "measure"), required=True)
    parser.add_argument("--expected-source", required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument("--chess-root", type=Path, default=Path(os.environ.get("LAPLACE_DATA_ROOT", "/vault/Data")) / "Games/Chess")
    parser.add_argument("--pgn", type=Path)
    parser.add_argument("--family", choices=("otb-2025", "elite"), default="otb-2025")
    parser.add_argument("--games", type=int, default=75000)
    parser.add_argument("--deadline-seconds", type=int, default=3600)
    args = parser.parse_args()
    if (not args.output_dir.is_absolute() or not args.chess_root.is_absolute()
            or not re.fullmatch(r"[0-9a-f]{40}", args.expected_source)
            or not 1 <= args.games <= 1000000 or not 30 <= args.deadline_seconds <= 86400):
        parser.error("invalid absolute paths, exact source or bounded measurement limits")
    import signal
    def interrupted(signum, frame):
        raise KeyboardInterrupt("corpus operation interrupted")
    signal.signal(signal.SIGTERM, interrupted)
    return run(args)


if __name__ == "__main__":
    raise SystemExit(main())
