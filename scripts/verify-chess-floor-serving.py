#!/usr/bin/env python3
"""Verify an installed recorded chess floor against canonical replay and live serving.

The caller owns the ordinary host reservation. This command creates evidence and
issues one read-only chess inference request; it never starts a game or records one.
"""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import stat
import sys
import time
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[1]


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / filename)
    result = importlib.util.module_from_spec(spec)
    sys.modules[name] = result
    spec.loader.exec_module(result)
    return result


artifacts = module("floor_serving_artifacts", "chess-floor-artifacts.py")
acceptance = module("floor_serving_acceptance", "accept-chess-environment.py")
SCHEMA = "laplace.chess-floor-serving/v1"
MAX_HTTP_BYTES = 1024 * 1024
MAX_PROC_BYTES = 8 * 1024 * 1024


def read_bytes(path, limit):
    with Path(path).open("rb") as source:
        value = source.read(limit + 1)
    if len(value) > limit:
        raise ValueError("bounded observation exceeded: " + str(path))
    return value


def fact(path):
    path = Path(path).resolve(strict=True)
    initial = path.stat()
    if not stat.S_ISREG(initial.st_mode):
        raise ValueError("selected input is not a regular file")
    digest = artifacts.sha256(path)
    final = path.stat()
    identity = lambda s: (s.st_dev, s.st_ino, s.st_size, s.st_mtime_ns, s.st_ctime_ns)
    if identity(initial) != identity(final):
        raise ValueError("selected input changed while hashing")
    return {"path": str(path), "bytes": initial.st_size, "sha256": digest,
            "device_major": os.major(initial.st_dev), "device_minor": os.minor(initial.st_dev),
            "inode": initial.st_ino}


def local_base(value):
    parsed = urllib.parse.urlsplit(value)
    if (parsed.scheme != "http" or parsed.hostname not in ("127.0.0.1", "::1", "localhost")
            or parsed.username or parsed.password or parsed.query or parsed.fragment
            or parsed.path not in ("", "/")):
        raise ValueError("API observation requires a plain loopback HTTP origin")
    return value.rstrip("/")


def request(base, route, token, payload=None, seconds=30):
    base = local_base(base)
    headers = {"Accept": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    data = None
    if payload is not None:
        data = json.dumps(payload, separators=(",", ":"), allow_nan=False).encode()
        headers["Content-Type"] = "application/json"
    outgoing = urllib.request.Request(base + route, data=data, headers=headers)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), acceptance.NoRedirect())
    # The absolute owner also bounds slow/trickling headers and bodies.
    with acceptance.deadline(seconds):
        with opener.open(outgoing, timeout=min(seconds, 10)) as response:
            raw = response.read(MAX_HTTP_BYTES + 1)
            if response.status != 200 or len(raw) > MAX_HTTP_BYTES:
                raise ValueError("API response status or body bound failed")
    value = json.loads(raw)
    if not isinstance(value, dict):
        raise ValueError("API response must be an object")
    return value


def ready(value, floor_files):
    chess = value.get("chess_perfcache")
    if (value.get("ready") is not True or not isinstance(chess, dict)
            or chess.get("ready") is not True
            or chess.get("initialization_completed") is not True
            or chess.get("failure_type") is not None):
        raise ValueError("API and both chess floors must report successful readiness")
    if type(chess.get("process_id")) is not int or chess["process_id"] <= 0:
        raise ValueError("readiness lacks the serving process ID")
    for kind, name in zip(("position", "transition"), artifacts.NAMES):
        row = chess.get(kind)
        if (not isinstance(row, dict) or row.get("is_loaded") is not True
                or type(row.get("record_count")) is not int
                or row["record_count"] != floor_files[name]["records"]
                or row["record_count"] <= 0):
            raise ValueError("readiness map count differs from installed " + kind)
    for kind, names in (("position", ("lookup_hits", "lookup_misses")),
                        ("transition", ("persistent_hits", "novel_hits", "lookup_misses"))):
        for name in names:
            if type(chess[kind].get(name)) is not int or chess[kind][name] < 0:
                raise ValueError("readiness counter is missing or invalid")
    return chess


def mapped_rows(text, selected):
    """Bind kernel device/inode, not a filename substring or a stale symlink."""
    found = {name: [] for name in selected}
    for line in text.splitlines():
        columns = line.split(None, 5)
        if len(columns) < 5:
            continue
        try:
            major, minor = (int(part, 16) for part in columns[3].split(":"))
            inode, offset = int(columns[4]), int(columns[2], 16)
        except ValueError:
            raise ValueError("malformed proc mapping observation") from None
        for name, item in selected.items():
            if (major, minor, inode) == (item["device_major"], item["device_minor"], item["inode"]):
                if name in ("position", "transition") and "w" in columns[1]:
                    raise ValueError("selected serving artifact has a writable mapping: " + name)
                found[name].append({"address": columns[0], "permissions": columns[1],
                                    "offset": offset, "path": columns[5] if len(columns) > 5 else ""})
    for name, rows in found.items():
        if not any(row["offset"] == 0 and "r" in row["permissions"] for row in rows):
            raise ValueError("serving process does not map the exact selected artifact: " + name)
    return found


def process(pid, selected):
    root = Path("/proc") / str(pid)
    def start():
        text = read_bytes(root / "stat", 65536).decode()
        end = text.rfind(")")
        fields = text[end + 2:].split()
        if end < 0 or len(fields) < 20:
            raise ValueError("malformed process identity")
        return int(fields[19])  # field 22; fields after comm start at field 3
    before = start()
    executable = fact(root / "exe")
    maps = read_bytes(root / "maps", MAX_PROC_BYTES).decode("utf-8", "strict")
    rows = mapped_rows(maps, selected)
    if before != start():
        raise ValueError("serving process changed during mapping observation")
    return {"pid": pid, "start_ticks": before, "executable": executable, "mappings": rows}


def delta(before, after, result, legal):
    if before["process_id"] != after["process_id"]:
        raise ValueError("serving process changed across inference")
    if (result.get("rated") is not True or result.get("uci") not in legal
            or type(result.get("depth")) is not int or result["depth"] < 1
            or type(result.get("nodes")) is not int or result["nodes"] <= 0
            or not isinstance(result.get("fen"), str) or not result["fen"]):
        raise ValueError("request did not complete legal substrate-enabled native chess search")
    changes = {}
    for kind, names in (("position", ("lookup_hits", "lookup_misses")),
                        ("transition", ("persistent_hits", "novel_hits", "lookup_misses"))):
        changes[kind] = {}
        for name in names:
            value = after[kind][name] - before[kind][name]
            if value < 0:
                raise ValueError("process-lifetime cache counter decreased")
            changes[kind][name] = value
    if changes["transition"]["persistent_hits"] <= 0:
        raise ValueError("no persistent transition hit observed; the legal frontier may already be cached")
    return changes


def require_same_files(selected):
    for value in selected.values():
        if fact(value["path"]) != value:
            raise ValueError("selected artifact changed during acceptance")


def observe(args, output, receipt):
    prefix = args.prefix.resolve(strict=True)
    app = (args.app_dir or prefix / "app").resolve(strict=True)
    current = prefix / "share/laplace/chess-floor/current"
    generation = current.resolve(strict=True)
    if (not current.is_symlink()
            or generation.parent != (current.parent / "generations").resolve(strict=True)):
        raise ValueError("installed floor selection is not an owned generation link")
    native = app / "liblaplace_core.so"
    hasher = artifacts.native_hasher(native)
    artifacts.verify_generation(generation, hasher)
    manifest = artifacts.read_json(generation / "receipt.json")
    floors = [manifest["files"][name] for name in artifacts.NAMES]
    manifest = artifacts.validate_pair_receipt(generation / "receipt.json", floors)
    if not manifest.get("export"):
        raise ValueError("installed pair has no completed recorded corpus provenance")
    provenance = artifacts.validate_export(manifest["export"]["receipt"])
    if provenance != manifest["export"]:
        raise ValueError("installed pair and authenticated export differ")
    for name in artifacts.NAMES:
        if (prefix / "share/laplace" / name).resolve(strict=True) != generation / name:
            raise ValueError("installed compatibility path selects another floor generation")
    catalog = args.catalog_dll.resolve(strict=True)
    selected = {"position": fact(generation / artifacts.NAMES[0]),
                "transition": fact(generation / artifacts.NAMES[1]), "native": fact(native),
                "pair_receipt": fact(generation / "receipt.json"),
                "catalog": fact(catalog), "position_emitter": fact(args.position_emitter)}
    mapped = {name: selected[name] for name in ("position", "transition", "native")}
    # Both canonical selector and serving host must use the exact same installed
    # managed/native owners, even when catalog is a separate build publication.
    for name in ("Laplace.Core.dll", "Laplace.Chess.dll"):
        installed = fact(app / name)
        selector = fact(catalog.parent / name)
        if (installed["sha256"], installed["bytes"]) != (selector["sha256"], selector["bytes"]):
            raise ValueError("catalog and installed managed owner differ: " + name)
        selected[name] = installed
        selected["catalog_" + name] = selector
        mapped[name] = installed
    selected["api"] = fact(app / "Laplace.Endpoints.OpenAICompat.dll")
    mapped["api"] = selected["api"]
    catalog_native = fact(catalog.parent / "liblaplace_core.so")
    if (catalog_native["sha256"], catalog_native["bytes"]) != (selected["native"]["sha256"], selected["native"]["bytes"]):
        raise ValueError("catalog app-local native owner differs from installed core")
    selected["catalog_native"] = catalog_native
    if selected["position_emitter"]["sha256"] != manifest["producer"]["binary_sha256"]:
        raise ValueError("position producer differs from the installed pair's bound producer")
    receipt.update(generation=str(generation), pair=manifest, files=selected)
    artifacts.atomic_json(output / "receipt.json", receipt)

    # Regenerate the finite opening/960 baseline with the existing catalog owner.
    # Matching its surfaces to the sealed generation binds its actual seed recipe.
    seed = output / "seed"
    seed.mkdir()
    surfaces, transition, position = (seed / name for name in ("surfaces.txt", artifacts.NAMES[1], artifacts.NAMES[0]))
    env = dict(os.environ)
    env["LAPLACE_PREFIX"] = str(prefix)
    acceptance.command(["dotnet", catalog, args.openings_dir, surfaces, transition],
                       output / "seed-catalog.log", args.deadline_seconds, env=env)
    if artifacts.sha256(surfaces) != manifest["producer"]["seed_surfaces_sha256"]:
        raise ValueError("regenerated seed surfaces differ from the installed pair's declared seed")
    scratch = seed / "sort"
    acceptance.command([args.position_emitter, "--surfaces", surfaces, "--output", position,
                        "--memory-bytes", str(args.memory_bytes), "--scratch-dir", scratch,
                        "--maximum-spill-bytes", str(args.maximum_spill_bytes)],
                       output / "seed-position.log", args.deadline_seconds, env=env)
    seed_facts = {kind: artifacts.validate_floor(path, kind, hasher)
                  for path, kind in ((position, "position"), (transition, "transition"))}
    receipt["seed"] = {"openings_directory": args.openings_dir,
                       "surfaces_sha256": artifacts.sha256(surfaces), "floors": seed_facts}
    selector = output / "selected-witness.json"
    acceptance.command(["dotnet", catalog, "select-recorded-floor-witness",
                        "--witnessed-inputs", provenance["roles"]["witnessed-inputs"],
                        "--seed-position", position, "--seed-transition", transition,
                        "--installed-position", generation / artifacts.NAMES[0],
                        "--installed-transition", generation / artifacts.NAMES[1],
                        "--output", selector, "--skip-eligible", str(args.skip_eligible)],
                       output / "selector.log", args.deadline_seconds, env=env)
    witness = artifacts.read_json(selector)
    expected = {"witnessed_inputs": fact(provenance["roles"]["witnessed-inputs"]),
                "seed_position": fact(position), "seed_transition": fact(transition),
                "installed_position": selected["position"], "installed_transition": selected["transition"]}
    if (witness.get("schema") != "laplace.chess-recorded-floor-witness/v1"
            or witness.get("status") != "passed"
            or witness.get("seed") != {"position_absent": True, "transition_absent": True}
            or witness.get("installed", {}).get("position_verified") is not True
            or witness.get("installed", {}).get("transition_lookup_source") != "Persistent"):
        raise ValueError("canonical selector did not verify the corpus-only persistent witness")
    for role, value in expected.items():
        if witness.get("files", {}).get(role) != {k: value[k] for k in ("path", "bytes", "sha256")}:
            raise ValueError("canonical selector used another input: " + role)
    native_modules = witness.get("native_modules")
    if not isinstance(native_modules, list) or len(native_modules) != 1:
        raise ValueError("canonical selector did not bind exactly one native core")
    selected_module = native_modules[0]
    if (Path(selected_module["path"]).resolve(strict=True) != Path(catalog_native["path"])
            or selected_module.get("bytes") != catalog_native["bytes"]
            or selected_module.get("sha256") != catalog_native["sha256"]):
        raise ValueError("canonical selector loaded another native core")
    receipt["witness"] = witness
    artifacts.atomic_json(output / "receipt.json", receipt)

    token = os.environ.get(args.api_key_env) if args.api_key_env else None
    before = request(args.api_base, "/health/ready", token)
    artifacts.atomic_json(output / "readiness-before.json", before)
    observed_before = ready(before, manifest["files"])
    process_before = process(observed_before["process_id"], mapped)
    artifacts.atomic_json(output / "process-before.json", process_before)
    payload = {"fen": witness["selected"]["from_fen"], "depth": 1, "substrate": True}
    artifacts.atomic_json(output / "request.json", payload)
    started = time.monotonic()
    result = request(args.api_base, "/chess/bestmove", token, payload, seconds=120)
    elapsed = time.monotonic() - started
    artifacts.atomic_json(output / "response.json", result)
    after = request(args.api_base, "/health/ready", token)
    artifacts.atomic_json(output / "readiness-after.json", after)
    observed_after = ready(after, manifest["files"])
    process_after = process(observed_after["process_id"], mapped)
    artifacts.atomic_json(output / "process-after.json", process_after)
    if (process_before["pid"], process_before["start_ticks"], process_before["executable"]) != (
            process_after["pid"], process_after["start_ticks"], process_after["executable"]):
        raise ValueError("serving process identity changed during inference")
    changes = delta(observed_before, observed_after, result, witness["selected"]["legal_uci"])
    require_same_files(selected)
    if current.resolve(strict=True) != generation:
        raise ValueError("installed generation changed during acceptance")
    if artifacts.validate_export(provenance["receipt"]) != provenance:
        raise ValueError("recorded corpus provenance changed during acceptance")
    artifacts.verify_generation(generation, hasher)
    receipt.update(status="passed", request_seconds=elapsed, process=process_after,
                   counter_deltas=changes, canonical_recorded_witness_verified=True,
                   serving_installed_generation_mapped=True,
                   serving_persistent_transition_hit_observed=True)
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for flag in ("prefix", "catalog-dll", "position-emitter", "output"):
        parser.add_argument("--" + flag, required=True, type=Path)
    parser.add_argument("--app-dir", type=Path)
    parser.add_argument("--openings-dir", required=True,
                        help="Exact catalog openings directory, or NONE only when the installed seed used NONE")
    parser.add_argument("--api-base", default="http://127.0.0.1:5187")
    parser.add_argument("--api-key-env", default="LAPLACE_API_KEY")
    parser.add_argument("--deadline-seconds", type=int, default=600)
    parser.add_argument("--memory-bytes", type=int, default=64 * 1024 * 1024)
    parser.add_argument("--maximum-spill-bytes", type=int, default=2 * 1024 * 1024 * 1024)
    parser.add_argument("--skip-eligible", type=int, default=0)
    args = parser.parse_args()
    paths = [args.prefix, args.catalog_dll, args.position_emitter, args.output]
    if args.app_dir:
        paths.append(args.app_dir)
    if (any(not path.is_absolute() for path in paths) or args.deadline_seconds <= 0
            or args.memory_bytes <= 0 or args.maximum_spill_bytes <= 0 or args.skip_eligible < 0
            or (args.openings_dir != "NONE" and not Path(args.openings_dir).is_absolute())):
        parser.error("paths must be absolute and resource bounds positive")
    local_base(args.api_base)
    args.output.mkdir(parents=True, exist_ok=False)
    receipt = {"schema": SCHEMA, "status": "incomplete",
               "collector_sha256": artifacts.sha256(Path(__file__)),
               "records_games": False, "throughput_measured": False,
               "exclusive_request_attribution": False,
               "counter_scope": "Serving process lifetime deltas around one request; concurrent requests are not excluded.",
               "canonical_scope": "Full typed replay of one retained completed playing; selected position and transition absent from reproduced seed floors.",
               "position_hit_in_serving_process_claimed": False}
    artifacts.atomic_json(args.output / "receipt.json", receipt)
    started = time.monotonic()
    try:
        with acceptance.deadline(args.deadline_seconds):
            observe(args, args.output, receipt)
    except (Exception, KeyboardInterrupt) as error:
        receipt.update(status="failed", failure_type=type(error).__name__, failure=str(error))
    finally:
        receipt["wall_seconds"] = time.monotonic() - started
        artifacts.atomic_json(args.output / "receipt.json", receipt)
    print(json.dumps(receipt, sort_keys=True, separators=(",", ":"), allow_nan=False))
    return 0 if receipt["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
