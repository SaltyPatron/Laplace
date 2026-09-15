#!/usr/bin/env python3
"""Report installed chess tools and data, exercising the actual configured binaries.

No installation, game creation, account upgrade, or substrate writes. --check-latest
also checks official stable releases. Data coverage and online account readiness
are reported separately from executable readiness.
"""
import argparse
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
KEYS = {"LAPLACE_STOCKFISH", "LAPLACE_CUTECHESS", "LAPLACE_SYZYGY",
        "LAPLACE_CHESS_OPENINGS", "LAPLACE_DATA_ROOT", "LAPLACE_EXTERNAL",
        "LAPLACE_STOCKFISH_SOURCE", "LAPLACE_CUTECHESS_BUILD", "LAPLACE_QT_BIN",
        "LAPLACE_CHESS_LAB_DIR", "LAPLACE_ZSTD_LIBRARY", "LAPLACE_ZSTD_WINDOW_LOG_MAX",
        "LAPLACE_ZSTD_SOURCE", "LAPLACE_ZSTD_BUILD"}
KEYS.update("LAPLACE_STOCKFISH_EVAL_" + suffix for suffix in
            ("THREADS", "HASH_MB", "NUMA_POLICY", "SYZYGY_PATH", "FILE", "TIMEOUT_SECONDS", "PROCESSES"))


def module(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), ROOT / "scripts" / (name + ".py"))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


def configuration(prefix, keys=None):
    allowed = KEYS if keys is None else keys
    result = {}
    # Match ChessRuntimeConfiguration: explicit environment, then the service's
    # installed env, then legacy chess files. Last assignment within a file wins.
    for path in (prefix / "app/laplace-api.env", prefix / "app/chess-lab.env",
                 prefix / "chess-lab.env", prefix / "secrets/chess-lab.env",
                 ROOT / "deploy/secrets/chess-lab.env"):
        if path.is_file():
            selected = {}
            for line in path.read_text(encoding="utf-8").splitlines():
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
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        result.append({"name": name, "status": "failed", "required": required,
                       "detail": str(error)})


def cutechess(binary):
    lock = json.loads((ROOT / "deploy/cutechess-release.json").read_text())
    return module("provision-cutechess").probe(binary, lock)


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
    parser.add_argument("--require-data", action="store_true", help="Fail if openings or paired Syzygy files are missing; does not certify complete tablebase coverage")
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
    print(json.dumps({"executable_ready": all(item["status"] == "ready" for item in report[:3]),
                      "configured_settings": {
                          "scope": "Selected environment and installed configuration files; live process settings require a separate runtime observation.",
                          "values": config},
                      "checks": report}, indent=2))
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
