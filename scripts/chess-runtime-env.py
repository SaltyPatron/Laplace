#!/usr/bin/env python3
"""Execute a chess check with the installed runtime settings, without printing them."""
import argparse
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RUNTIME_KEYS = frozenset({
    "LD_LIBRARY_PATH", "LAPLACE_DB", "LAPLACE_PERFCACHE_BIN",
    "LAPLACE_ENGINE_BUILD", "LAPLACE_UCI_SUBSTRATE",
})


# The installed chess settings, read as ChessRuntimeConfiguration and laplace-uci (Lab/ChessLabConfig.cs) read them:
# explicit environment, then the service's installed env, then the chess-lab files. Last assignment within a file wins.
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


def configuration(prefix, keys=None, api_environment=None, include_environment=True):
    allowed = KEYS if keys is None else keys
    result = {}
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


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prefix", type=Path, default=Path(os.environ.get("LAPLACE_INSTALL_PREFIX", "/opt/laplace")))
    parser.add_argument("command", nargs=argparse.REMAINDER)
    arguments = parser.parse_args(argv)
    command = arguments.command
    if command[:1] == ["--"]:
        command = command[1:]
    if not command:
        parser.error("provide an executable after --")
    environment = dict(os.environ)
    environment.update(configuration(arguments.prefix, keys=RUNTIME_KEYS))
    environment["LAPLACE_INSTALL_PREFIX"] = str(arguments.prefix)
    # Connection settings may contain credentials. Keep values out of Actions
    # environment files, logs and measurement artifacts; replace this process so
    # the caller's timeout and process-group cleanup still cover the actual work.
    os.execvpe(command[0], command, environment)


if __name__ == "__main__":
    main()
