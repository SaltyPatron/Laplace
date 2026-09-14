#!/usr/bin/env python3
"""Is the INSTALLED laplace_substrate extension built from the current configured source?

pg_regress tests the INSTALLED extension, never an edited .sql.in. So a green regress run
describes whatever was last installed, and says nothing about the tree -- the same shape as
managed tests loading /opt/laplace/lib/liblaplace_core.so instead of build/engine/core
(fixed 2026-08-24). That one was silent; this one is documented, and still undetectable
without recomputing the configured extension version.

The extension version is a content hash of its SQL inputs plus the configured native
execution-module identity (extension/laplace_substrate/CMakeLists.txt):

    inputs  = manifest.install modules + manifest.upgrade modules
              + laplace_substrate.control.in + laplace_substrate.sql.in
              + laplace_substrate_upgrade.sql.in + sqldefines.h.in
              + manifest.install + manifest.upgrade
    dedupe, sort by path
    version = SHA256( concat( SHA256(file) for file in inputs )
                      + "module_pathname=<v>;execution=<configured execution module>" )[:16]

The execution module is itself content/configuration-derived by CMake, so this checker must
consume the exact configured build manifest rather than pretending the extension version is
source-only. Recomputing the SQL side from source and combining it with that build identity,
then comparing against the installed laplace_substrate--<version>.sql, answers the parity
question mechanically.

Exit 0 current, 1 stale, 2 cannot determine. Stale is a real answer, not an error: it means
a regress result must not be read as evidence about the tree.
"""
import argparse
import hashlib
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
EXT = ROOT / "extension" / "laplace_substrate"
SQL = EXT / "sql"
DEFAULT_EXECUTION_MANIFEST = ROOT / "build" / "extension" / "laplace_substrate" / "laplace_execution_module.txt"
EXECUTION_RE = re.compile(r"laplace_execution_[0-9a-fA-F]{16}")


def manifest_files(manifest):
    """laplace_manifest_files: one relative path per non-comment, non-blank line."""
    out = []
    for line in manifest.read_text(encoding="utf-8").splitlines():
        line = line.split("#", 1)[0].strip()
        if line:
            out.append(SQL / line)
    return out


def configured_execution_module(explicit=None, manifest=DEFAULT_EXECUTION_MANIFEST):
    """Return the exact execution-module name emitted by this configured build."""
    if explicit is not None:
        name = explicit.strip()
    else:
        try:
            name = pathlib.Path(manifest).read_text(encoding="utf-8").strip()
        except OSError as exc:
            print(
                f"missing configured execution-module manifest: {manifest} ({exc}); "
                "run pipeline.sh build before checking installed/source parity",
                file=sys.stderr,
            )
            return None
    if not EXECUTION_RE.fullmatch(name):
        print(f"invalid configured execution-module identity: {name!r}", file=sys.stderr)
        return None
    return name


def source_version(module_pathname, execution_module):
    inputs = manifest_files(SQL / "manifest.install") + manifest_files(SQL / "manifest.upgrade")
    inputs += [EXT / "laplace_substrate.control.in",
               SQL / "laplace_substrate.sql.in",
               SQL / "laplace_substrate_upgrade.sql.in",
               SQL / "sqldefines.h.in",
               SQL / "manifest.install",
               SQL / "manifest.upgrade"]
    # dedupe then sort, exactly as CMakeLists does before hashing.
    seen, ordered = set(), []
    for p in inputs:
        s = str(p)
        if s not in seen:
            seen.add(s)
            ordered.append(p)
    ordered.sort(key=str)

    acc = ""
    for p in ordered:
        if not p.exists():
            print(f"missing hashed input: {p}", file=sys.stderr)
            return None
        # CMake file(SHA256) and string(SHA256) both emit LOWERCASE hex, and the outer
        # hash is taken over that text, so the case has to match exactly.
        acc += hashlib.sha256(p.read_bytes()).hexdigest()
    acc += f"module_pathname={module_pathname};execution={execution_module}"
    return hashlib.sha256(acc.encode()).hexdigest()[:16]


def installed_versions():
    found = []
    for base in ("/opt/laplace/share/postgresql", "/opt/laplace/pgsql-18/share"):
        d = pathlib.Path(base)
        if not d.exists():
            continue
        for p in d.rglob("laplace_substrate--*.sql"):
            m = re.fullmatch(r"laplace_substrate--([0-9a-fA-F]{16})\.sql", p.name)
            if m:
                found.append((m.group(1), p))
    return found


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--module-pathname", default="laplace_substrate",
                    help="EXT_MODULE_PATHNAME the install was configured with")
    ap.add_argument("--execution-module",
                    help="override configured execution-module identity (tests/debug only)")
    ap.add_argument("--execution-module-file", type=pathlib.Path,
                    default=DEFAULT_EXECUTION_MANIFEST,
                    help="configured laplace_execution_module.txt emitted by CMake")
    ap.add_argument("--print-source-version", action="store_true",
                    help="print only the source+configured-build extension version")
    a = ap.parse_args()

    execution_module = configured_execution_module(a.execution_module, a.execution_module_file)
    if execution_module is None:
        return 2
    want = source_version(a.module_pathname, execution_module)
    if want is None:
        return 2
    if a.print_source_version:
        print(want)
        return 0

    installed = installed_versions()
    if not installed:
        print("no installed laplace_substrate--<version>.sql found", file=sys.stderr)
        return 2

    names = {v for v, _ in installed}
    print(f"execution module : {execution_module}")
    print(f"source version   : {want}")
    for v, p in sorted(installed):
        print(f"installed        : {v}  {p}")

    if want in names:
        print("installed extension is built from the current configured source")
        return 0
    print("\nSTALE: no installed extension matches the current configured source.\n"
          "pg_regress tests the INSTALLED extension, so a green regress run describes that\n"
          "artifact and not this tree. Run pipeline.sh build install before reading a\n"
          "regress result as evidence.",
          file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
