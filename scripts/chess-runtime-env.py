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
        "LAPLACE_CHESS_OPENINGS", "LAPLACE_DATA", "LAPLACE_EXTERNAL",
        "LAPLACE_STOCKFISH_SOURCE", "LAPLACE_CUTECHESS_BUILD", "LAPLACE_QT_BIN",
        "LAPLACE_CHESS_LAB_DIR", "LAPLACE_ZSTD_LIBRARY", "LAPLACE_ZSTD_WINDOW_LOG_MAX",
        "LAPLACE_ZSTD_SOURCE", "LAPLACE_ZSTD_BUILD",
        "LAPLACE_FASTCHESS", "LAPLACE_ORDO", "LAPLACE_LC0", "LAPLACE_LC0_NET", "LAPLACE_LC0_BACKEND",
        "LAPLACE_CHESS_BOOKS", "LAPLACE_UCI"}
KEYS.update("LAPLACE_STOCKFISH_EVAL_" + suffix for suffix in
            ("THREADS", "HASH_MB", "NUMA_POLICY", "SYZYGY_PATH", "FILE", "TIMEOUT_SECONDS", "PROCESSES"))


def configuration(prefix, keys=None, api_environment=None, include_environment=True, include_managed=True):
    allowed = KEYS if keys is None else keys
    result = {}
    application = Path(os.environ.get("LAPLACE_APP_DIR", str(prefix / "app"))) if include_environment else prefix / "app"
    files = [application / "laplace-api.env", application / "chess-lab.env",
             prefix / "chess-lab.env", prefix / "secrets/chess-lab.env",
             ROOT / "deploy/secrets/chess-lab.env"]
    if include_environment and os.environ.get("LAPLACE_CHESS_ENV"):
        declared = Path(os.environ["LAPLACE_CHESS_ENV"])
        if not declared.is_absolute() or not declared.is_file():
            raise ValueError("LAPLACE_CHESS_ENV must name an existing absolute configuration file")
        files.insert(0, declared)
    for path in files:
        supplied = api_environment if path == application / "laplace-api.env" else None
        if supplied is not None or path.is_file():
            selected = {}
            managed = False
            for line in (supplied if supplied is not None else path.read_text(encoding="utf-8")).splitlines():
                if line.strip() == '# >>> laplace-runner managed: chess-lab env >>>':
                    managed = True
                elif line.strip() == '# <<< laplace-runner managed: chess-lab env <<<':
                    managed = False
                    continue
                if managed and not include_managed:
                    continue
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
    parser.add_argument("--value", choices=sorted(KEYS), help="Print one public chess setting")
    parser.add_argument("--unmanaged", action="store_true", help="Ignore generated tool selections when resolving installation overrides")
    parser.add_argument("command", nargs=argparse.REMAINDER)
    arguments = parser.parse_args(argv)
    if arguments.value:
        print(configuration(arguments.prefix, keys={arguments.value}, include_managed=not arguments.unmanaged).get(arguments.value, ""))
        return
    command = arguments.command
    if command[:1] == ["--"]:
        command = command[1:]
    if not command:
        parser.error("provide an executable after --")
    environment = dict(os.environ)
    environment.update(configuration(arguments.prefix, keys=RUNTIME_KEYS | KEYS))
    environment["LAPLACE_INSTALL_PREFIX"] = str(arguments.prefix)
    # Connection settings may contain credentials. Keep values out of Actions
    # environment files, logs and measurement artifacts; replace this process so
    # the caller's timeout and process-group cleanup still cover the actual work.
    os.execvpe(command[0], command, environment)


if __name__ == "__main__":
    main()
