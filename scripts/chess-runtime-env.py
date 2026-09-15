#!/usr/bin/env python3
"""Execute a chess check with the installed runtime settings, without printing them."""
import argparse
import importlib.util
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RUNTIME_KEYS = frozenset({
    "LD_LIBRARY_PATH", "LAPLACE_DB", "LAPLACE_PERFCACHE_BIN",
    "LAPLACE_ENGINE_BUILD", "LAPLACE_UCI_SUBSTRATE",
})


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
    spec = importlib.util.spec_from_file_location("chess_configuration", ROOT / "scripts/check-chess-dependencies.py")
    doctor = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(doctor)
    environment = dict(os.environ)
    environment.update(doctor.configuration(arguments.prefix, keys=RUNTIME_KEYS))
    environment["LAPLACE_INSTALL_PREFIX"] = str(arguments.prefix)
    # Connection settings may contain credentials. Keep values out of Actions
    # environment files, logs and measurement artifacts; replace this process so
    # the caller's timeout and process-group cleanup still cover the actual work.
    os.execvpe(command[0], command, environment)


if __name__ == "__main__":
    main()
