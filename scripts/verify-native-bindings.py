#!/usr/bin/env python3
"""Verify each PostgreSQL C binding against the library that actually owns it."""
import argparse
from pathlib import Path
import re
import subprocess
import sys


def verify(libdir, bindings):
    exports = {}
    checked = 0
    for library, symbol in sorted(set(bindings)):
        name = library.removeprefix("$libdir/")
        if not re.fullmatch(r"laplace_(?:substrate|geom|execution_[0-9a-f]{16})", name):
            continue
        if name not in exports:
            path = libdir / (name + ".so")
            result = subprocess.run(["nm", "-D", "--defined-only", str(path)],
                                    capture_output=True, text=True, check=True)
            exports[name] = {line.split()[-1] for line in result.stdout.splitlines()
                             if len(line.split()) >= 3}
        if symbol not in exports[name]:
            raise ValueError(f"{name}.so does not export its bound symbol {symbol}")
        checked += 1
    if not checked:
        raise ValueError("no Laplace native bindings were supplied")
    return checked, len(exports)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--libdir", type=Path, required=True)
    parser.add_argument("--sql", type=Path)
    args = parser.parse_args()
    if args.sql:
        bindings = re.findall(r"\bAS\s+'([^']+)'\s*,\s*'([^']+)'",
                              args.sql.read_text(), re.IGNORECASE)
    else:
        bindings = []
        for line in sys.stdin:
            fields = line.rstrip("\n").split("\t")
            if len(fields) != 2:
                raise ValueError("catalog input requires library and symbol columns")
            bindings.append(tuple(fields))
    count, libraries = verify(args.libdir, bindings)
    print(f"Verified {count} native bindings against {libraries} owning libraries")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        raise SystemExit(f"native binding verification failed: {error}")
