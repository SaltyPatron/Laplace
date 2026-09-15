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
        "LAPLACE_STOCKFISH_SOURCE", "LAPLACE_CUTECHESS_BUILD"}


def module(name):
    spec = importlib.util.spec_from_file_location(name.replace("-", "_"), ROOT / "scripts" / (name + ".py"))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


def configuration(prefix):
    result = {}
    for path in (prefix / "app/laplace-api.env", prefix / "app/chess-lab.env",
                 prefix / "chess-lab.env"):
        if path.is_file():
            for line in path.read_text(encoding="utf-8").splitlines():
                key, sep, value = line.partition("=")
                if sep and key.strip() in KEYS:
                    result[key.strip()] = value.strip().strip('"').strip("'")
    result.update({key: os.environ[key] for key in KEYS if os.environ.get(key)})
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
    roots = [Path(part) for part in configured.split(os.pathsep)] if configured else [chess / "syzygy"]
    files = [file for root in roots if root.is_dir() for file in root.rglob("*")
             if file.is_file() and file.suffix in (".rtbw", ".rtbz")]
    wdl = {file.stem for file in files if file.suffix == ".rtbw" and file.stat().st_size > 0}
    dtz = {file.stem for file in files if file.suffix == ".rtbz" and file.stat().st_size > 0}
    # Pairing detects missing companions; it is not a checksum or complete-roster proof.
    tables = {"paths": [str(root) for root in roots], "wdl": len(wdl), "dtz": len(dtz),
              "missing_dtz": sorted(wdl - dtz), "missing_wdl": sorted(dtz - wdl),
              "verification": "nonempty files and matching material names; checksums not verified"}
    candidates = ([Path(config["LAPLACE_CHESS_OPENINGS"])] if config.get("LAPLACE_CHESS_OPENINGS")
                  else [chess / "lichess-openings", chess / "openings"])
    opening_root = next((root for root in candidates if all((root / (letter + ".tsv")).is_file()
                                                          for letter in "abcde")), candidates[0])
    opening_files = [letter + ".tsv" for letter in "abcde" if (opening_root / (letter + ".tsv")).is_file()]
    return [
        {"name": "syzygy", "status": "present" if wdl and wdl == dtz else "incomplete",
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
    cc_default = (Path(config.get("LAPLACE_CUTECHESS_BUILD", str(Path(os.environ.get("LAPLACE_BUILD_ROOT", "D:/Data/Laplace")) / "build-cutechess")))
                  / "cutechess-cli.exe" if os.name == "nt" else args.prefix / "bin/cutechess-cli")
    cc = Path(config.get("LAPLACE_CUTECHESS", str(cc_default)))
    uci = args.uci or args.prefix / "app" / ("laplace-uci" + suffix)
    report = []
    version = json.loads((ROOT / "deploy/linux/stockfish-release.json").read_text())["version"]
    check(report, "stockfish", lambda: {"path": str(stockfish), "version": version,
          "search": module("install-stockfish").probe(stockfish, version)})
    check(report, "cutechess", lambda: cutechess(cc))
    check(report, "laplace-uci", lambda: {"path": str(uci), "bestmove": module("check-uci-runtime").check_runtime(uci)})
    data_root = Path(config.get("LAPLACE_DATA_ROOT", "D:/Data/Ingest" if os.name == "nt" else "/vault/Data"))
    data = data_inventory(config, data_root)
    for item in data[:2]:
        item["required"] = args.require_data
    report.extend(data)
    if args.check_latest:
        for name in ("install-stockfish", "provision-cutechess"):
            def latest(helper=name):
                reply = subprocess.run([sys.executable, str(ROOT / "scripts" / (helper + ".py")), "--check-latest"],
                                       check=True, capture_output=True, text=True, timeout=60)
                return reply.stdout.strip()
            check(report, name + "-latest", latest)
    failed = any(item["required"] and item["status"] not in ("ready", "present") for item in report)
    print(json.dumps({"executable_ready": all(item["status"] == "ready" for item in report[:3]),
                      "checks": report}, indent=2))
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
