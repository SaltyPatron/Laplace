#!/usr/bin/env python3
"""Report installed chess tools and data, exercising the actual configured binaries.

No installation, game creation, account upgrade, or substrate writes. --check-latest
also checks official stable releases. Data coverage and online account readiness
are reported separately from executable readiness.
"""
import argparse
import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
KEYS = {"LAPLACE_STOCKFISH", "LAPLACE_CUTECHESS", "LAPLACE_CUTECHESS_GUI",
        "LAPLACE_CUTECHESS_GUI_RECEIPT", "LAPLACE_SYZYGY",
        "LAPLACE_CHESS_OPENINGS", "LAPLACE_DATA_ROOT", "LAPLACE_EXTERNAL",
        "LAPLACE_STOCKFISH_SOURCE", "LAPLACE_CUTECHESS_BUILD", "LAPLACE_QT_BIN",
        "LAPLACE_CHESS_LAB_DIR", "LAPLACE_ZSTD_LIBRARY", "LAPLACE_ZSTD_WINDOW_LOG_MAX",
        "LAPLACE_ZSTD_SOURCE", "LAPLACE_ZSTD_BUILD",
        "LAPLACE_FASTCHESS", "LAPLACE_ORDO", "LAPLACE_LC0", "LAPLACE_LC0_NET", "LAPLACE_LC0_BACKEND",
        "LAPLACE_CHESS_BOOKS"}
KEYS.update("LAPLACE_STOCKFISH_EVAL_" + suffix for suffix in
            ("THREADS", "HASH_MB", "NUMA_POLICY", "SYZYGY_PATH", "FILE", "TIMEOUT_SECONDS", "PROCESSES"))


def module(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), ROOT / "scripts" / (name + ".py"))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


def configuration(prefix, keys=None, api_environment=None, include_environment=True):
    allowed = KEYS if keys is None else keys
    result = {}
    # Match ChessRuntimeConfiguration: explicit environment, then the service's
    # installed env, then legacy chess files. Last assignment within a file wins.
    for path in (prefix / "app/laplace-api.env", prefix / "app/chess-lab.env",
                 prefix / "chess-lab.env", prefix / "secrets/chess-lab.env",
                 ROOT / "deploy/secrets/chess-lab.env"):
        supplied = api_environment if path == prefix / "app/laplace-api.env" else None
        if supplied is not None or path.is_file():
            selected = {}
            for line in (supplied if supplied is not None else path.read_text(encoding="utf-8")).splitlines():
                if line.lstrip().startswith("#"):
                    continue
                key, sep, value = line.partition("=")
                if sep and key.strip() in allowed:
                    value = value.strip()
                    if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
                        value = value[1:-1]
                    selected[key.strip()] = value
            for key, value in selected.items():
                if value.strip():
                    result.setdefault(key, value)
    if include_environment:
        result.update({key: os.environ[key].strip() for key in allowed if os.environ.get(key, "").strip()})
        if os.environ.get("LAPLACE_STOCKFISH_SOURCE", "").strip() and not os.environ.get("LAPLACE_STOCKFISH", "").strip():
            result.pop("LAPLACE_STOCKFISH", None)
    return result


def check(result, name, action, required=True):
    try:
        # Imported tool helpers may print success receipts; keep JSON well formed.
        with contextlib.redirect_stdout(io.StringIO()):
            detail = action()
        result.append({"name": name, "status": "ready", "required": required, "detail": detail})
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError, KeyError, TypeError) as error:
        result.append({"name": name, "status": "failed", "required": required,
                       "detail": str(error)})


def cutechess(binary):
    lock = json.loads((ROOT / "deploy/cutechess-release.json").read_text())
    return module("provision-cutechess").probe(binary, lock)


def sha256(path):
    value = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            value.update(block)
    return value.hexdigest()


def ladder_lock():
    return json.loads((ROOT / "deploy/chess-ladder-release.json").read_text())


def run_version(binary, *arguments):
    reply = subprocess.run([str(binary), *arguments], capture_output=True, text=True, timeout=60,
                           stdin=subprocess.DEVNULL)
    return (reply.stdout + reply.stderr).strip()


def pinned_binary(name, binary, lock):
    """An executable the ladder uses: present, one of the lock's pinned binaries by SHA-256, and saying its version."""
    binary = Path(binary)
    if not binary.is_file():
        raise ValueError(f"{name} not found at {binary}")
    digest = sha256(binary)
    pinned = {asset["binary_sha256"]: key for key, asset in lock["assets"].items() if asset.get("binary_sha256")}
    if digest not in pinned:
        raise ValueError(f"{name} at {binary} has SHA-256 {digest}, not a binary pinned in deploy/chess-ladder-release.json")
    return {"path": str(binary), "sha256": digest, "asset": pinned[digest], "version": lock["version"]}


def fastchess(binary):
    lock = ladder_lock()["fastchess"]
    detail = pinned_binary("fastchess", binary, lock)
    line = run_version(binary, "-version")
    if lock["version_line"] not in line or lock["commit"][:7] not in line:
        raise ValueError(f"fastchess says '{line}', the lock is {lock['version_line']} at {lock['commit'][:7]}")
    detail["says"] = line.splitlines()[-1]
    return detail


def ordo(binary):
    lock = ladder_lock()["ordo"]
    detail = pinned_binary("ordo", binary, lock) if os.name == "nt" else {"path": str(binary)}
    line = run_version(binary, "-v")
    if lock["version_line"] not in line:
        raise ValueError(f"ordo says '{line}', the lock is {lock['version_line']}")
    detail["says"] = line.splitlines()[0]
    return detail


def lc0(binary, network, backend):
    """Lc0's binary and network by SHA-256. The engine is not started here: it needs the GPU, which on
    HART-DESKTOP is lent to it only in chess-GPU mode (Laplace-Operations chess/chess-gpu.ps1)."""
    lock = ladder_lock()["lc0"]
    detail = pinned_binary("lc0", binary, lock) if os.name == "nt" else {"path": str(binary)}
    network = Path(network) if network else None
    if network is None or not network.is_file():
        raise ValueError(f"LAPLACE_LC0_NET does not name a network file ({network})")
    digest = sha256(network)
    pinned = lock["networks"].get(network.name)
    if not pinned or pinned["sha256_tofu"] != digest:
        raise ValueError(f"network {network.name} has SHA-256 {digest}, not the one deploy/chess-ladder-release.json pins")
    detail.update({"network": str(network), "network_sha256": digest, "backend": backend or "lc0 default (cuda-auto)"})
    return detail


def books(directory):
    lock = ladder_lock()["books"]
    directory = Path(directory)
    files = {}
    for name, digest in lock["files"].items():
        path = directory / name
        if not path.is_file():
            raise ValueError(f"opening suite {path} is missing")
        actual = sha256(path)
        if actual != digest:
            raise ValueError(f"opening suite {path} has SHA-256 {actual}, the lock says {digest}")
        files[name] = digest
    return {"path": str(directory), "commit": lock["commit"], "files": files}


def data_inventory(config, data_root):
    chess = data_root / "Games/Chess"
    configured = config.get("LAPLACE_SYZYGY")
    roots = list(dict.fromkeys(Path(os.path.abspath(part.strip())) for part in configured.split(os.pathsep)
                               if part.strip())) if configured else [chess / "syzygy"]
    missing_roots = [str(root) for root in roots if not root.is_dir()]
    files = list(dict.fromkeys(file for root in roots if root.is_dir() for file in root.rglob("*")
                              if file.is_file() and file.suffix.lower() in (".rtbw", ".rtbz")))
    empty_files = sorted(str(file) for file in files if file.stat().st_size == 0)
    wdl = {file.stem for file in files if file.suffix.lower() == ".rtbw" and file.stat().st_size > 0}
    dtz = {file.stem for file in files if file.suffix.lower() == ".rtbz" and file.stat().st_size > 0}
    # Pairing detects missing companions; it is not a checksum or complete-roster proof.
    tables = {"paths": [str(root) for root in roots], "wdl": len(wdl), "dtz": len(dtz),
              "missing_roots": missing_roots, "empty_files": empty_files,
              "native_path": os.pathsep.join(sorted({str(file.parent) for file in files})),
              "missing_dtz": sorted(wdl - dtz), "missing_wdl": sorted(dtz - wdl),
              "verification": "nonempty files and matching material names; checksums not verified"}
    candidates = ([Path(config["LAPLACE_CHESS_OPENINGS"])] if config.get("LAPLACE_CHESS_OPENINGS")
                  else [chess / "lichess-openings", chess / "openings"])
    opening_root = next((root for root in candidates if all((root / (letter + ".tsv")).is_file()
                                                          for letter in "abcde")), candidates[0])
    opening_files = [letter + ".tsv" for letter in "abcde" if (opening_root / (letter + ".tsv")).is_file()]
    return [
        {"name": "syzygy", "status": "present" if roots and not missing_roots and not empty_files and wdl and wdl == dtz else "incomplete",
         "required": False, "detail": tables},
        {"name": "openings", "status": "present" if len(opening_files) == 5 else "incomplete",
         "required": False, "detail": {"path": str(opening_root), "files": opening_files}},
        {"name": "lichess", "status": "check-service", "required": False,
         "detail": "GET /chess/lichess/status reports verified BOT account and bot:play access; no separate lichess-bot or python-chess installation is used."},
    ]


def main():
    default = (Path(os.environ.get("LAPLACE_TOOLS", "tools")) / "chess" if os.name == "nt"
               else Path("/opt/laplace"))
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prefix", type=Path, default=Path(os.environ.get("LAPLACE_INSTALL_PREFIX", str(default))))
    parser.add_argument("--uci", type=Path, help="Published laplace-uci executable")
    parser.add_argument("--check-latest", action="store_true")
    parser.add_argument("--cutechess-gui", action="store_true", help="Require the installed official GUI and selected Qt offscreen runtime")
    parser.add_argument("--require-data", action="store_true", help="Fail if openings or paired Syzygy files are missing; does not certify complete tablebase coverage")
    parser.add_argument("--require-ladder", action="store_true", help="Fail unless fastchess, Ordo, Lc0 with its network, and the opening suites are installed as deploy/chess-ladder-release.json pins them")
    args = parser.parse_args()
    config = configuration(args.prefix)
    suffix = ".exe" if os.name == "nt" else ""
    external = Path(config.get("LAPLACE_EXTERNAL", str(ROOT / "external") if os.name == "nt" else "/build/external"))
    source = Path(config.get("LAPLACE_STOCKFISH_SOURCE", str(external / "stockfish")))
    stockfish = Path(config.get("LAPLACE_STOCKFISH", str(source / "src" / ("stockfish" + suffix))))
    cc_default = (Path(config["LAPLACE_CUTECHESS_BUILD"]) / ("cutechess-cli" + suffix)
                  if config.get("LAPLACE_CUTECHESS_BUILD") else
                  Path(os.environ.get("LAPLACE_BUILD_ROOT", "D:/Data/Laplace")) / "build-cutechess/cutechess-cli.exe"
                  if os.name == "nt" else args.prefix / "bin/cutechess-cli")
    cc = Path(config.get("LAPLACE_CUTECHESS", str(cc_default)))
    uci = args.uci or args.prefix / "app" / ("laplace-uci" + suffix)
    report = []
    version = json.loads((ROOT / "deploy/linux/stockfish-release.json").read_text())["version"]
    check(report, "stockfish", lambda: {"path": str(stockfish), "version": version,
          "search": module("install-stockfish").probe(stockfish, version)})
    check(report, "cutechess", lambda: cutechess(cc))
    check(report, "laplace-uci", lambda: {"path": str(uci), "bestmove": module("check-uci-runtime").check_runtime(uci)})
    check(report, "zstandard-pgn-codec", lambda: module("check-zstd-runtime").probe(
        config.get("LAPLACE_ZSTD_LIBRARY"), int(config.get("LAPLACE_ZSTD_WINDOW_LOG_MAX", "27")),
        json.loads((ROOT / "deploy/zstd-release.json").read_text())["version"]))
    # the ladder: the conductor, the rating tool, Lc0 and the opening suites (deploy/chess-ladder-release.json)
    data_root_for_books = Path(config.get("LAPLACE_DATA_ROOT", "D:/Data/Ingest" if os.name == "nt" else "/vault/Data"))
    for name, key, action in (
            ("fastchess", "LAPLACE_FASTCHESS", lambda: fastchess(config["LAPLACE_FASTCHESS"])),
            ("ordo", "LAPLACE_ORDO", lambda: ordo(config["LAPLACE_ORDO"])),
            ("lc0", "LAPLACE_LC0", lambda: lc0(config["LAPLACE_LC0"], config.get("LAPLACE_LC0_NET"), config.get("LAPLACE_LC0_BACKEND"))),
            ("opening-suites", "LAPLACE_CHESS_BOOKS", lambda: books(config.get("LAPLACE_CHESS_BOOKS")
                                                                     or data_root_for_books / ladder_lock()["books"]["directory"]))):
        if key in config or args.require_ladder or name == "opening-suites":
            check(report, name, action, required=args.require_ladder)
        else:
            report.append({"name": name, "status": "not-configured", "required": False,
                           "detail": f"set {key} in chess-lab.env (deploy/windows/chess-lab.env.example)"})
    gui_configured = bool(config.get("LAPLACE_CUTECHESS_GUI") or config.get("LAPLACE_CUTECHESS_GUI_RECEIPT"))
    if args.cutechess_gui or gui_configured:
        gui = Path(config.get("LAPLACE_CUTECHESS_GUI", str(args.prefix / "bin" / ("cutechess" + suffix))))
        gui_receipt = Path(config.get("LAPLACE_CUTECHESS_GUI_RECEIPT",
                          str(Path(config.get("LAPLACE_CUTECHESS_BUILD", "/build/cutechess")) / "laplace-cutechess-gui-build.json")))
        check(report, "cutechess-gui", lambda: module("provision-cutechess").verify_gui_install(
            gui, gui_receipt, json.loads((ROOT / "deploy/cutechess-release.json").read_text())))
    else:
        report.append({"name": "cutechess-gui", "status": "not-configured", "required": False,
                       "detail": "Linux host setup and publish provision the official GUI; --cutechess-gui requires its retained build and offscreen runtime proof."})
    data_root = Path(config.get("LAPLACE_DATA_ROOT", "D:/Data/Ingest" if os.name == "nt" else "/vault/Data"))
    data = data_inventory(config, data_root)
    for item in data[:2]:
        item["required"] = args.require_data
    report.extend(data)
    if args.check_latest:
        for name in ("install-stockfish", "provision-cutechess", "install-zstd"):
            def latest(helper=name):
                reply = subprocess.run([sys.executable, str(ROOT / "scripts" / (helper + ".py")), "--check-latest"],
                                       check=True, capture_output=True, text=True, timeout=60)
                return reply.stdout.strip()
            check(report, name + "-latest", latest)
    failed = any(item["required"] and item["status"] not in ("ready", "present") for item in report)
    print(json.dumps({"executable_ready": all(item["status"] == "ready" for item in report
                                               if item["required"] and item["name"] in ("stockfish", "cutechess", "laplace-uci", "cutechess-gui")),
                      "configured_settings": {
                          "scope": "Selected environment and installed configuration files; live process settings require a separate runtime observation.",
                          "values": config},
                      "checks": report}, indent=2))
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
