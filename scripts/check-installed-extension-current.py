#!/usr/bin/env python3
"""Is the INSTALLED laplace_substrate extension built from the current source?

pg_regress tests the INSTALLED extension, never an edited .sql.in. So a green regress run
describes whatever was last installed, and says nothing about the tree -- the same shape as
managed tests loading /opt/laplace/lib/liblaplace_core.so instead of build/engine/core
(fixed 2026-08-24). That one was silent; this one is documented, and still undetectable
without recomputing the version.

The extension version IS a content hash of its own SQL inputs
(extension/laplace_substrate/CMakeLists.txt:28-52):

    inputs  = manifest.install modules + manifest.upgrade modules
              + laplace_substrate.control.in + laplace_substrate.sql.in
              + laplace_substrate_upgrade.sql.in + sqldefines.h.in
              + manifest.install + manifest.upgrade
    dedupe, sort by path
    version = SHA256( concat( SHA256(file) for file in inputs ) + "module_pathname=<v>" )[:16]

Recomputing it from source and comparing against the installed
laplace_substrate--<version>.sql answers the question mechanically.

Exit 0 current, 1 stale, 2 cannot determine. Stale is a real answer, not an error: it means
a regress result must not be read as evidence about the tree.
"""
import argparse, hashlib, pathlib, re, subprocess, sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
EXT = ROOT / "extension" / "laplace_substrate"
SQL = EXT / "sql"

def manifest_files(manifest):
    """laplace_manifest_files: one relative path per non-comment, non-blank line."""
    out = []
    for line in manifest.read_text(encoding="utf-8").splitlines():
        line = line.split("#", 1)[0].strip()
        if line:
            out.append(SQL / line)
    return out

def source_version(module_pathname):
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
            seen.add(s); ordered.append(p)
    ordered.sort(key=str)

    acc = ""
    for p in ordered:
        if not p.exists():
            print(f"missing hashed input: {p}", file=sys.stderr)
            return None
        # CMake file(SHA256) and string(SHA256) both emit LOWERCASE hex, and the outer
        # hash is taken over that text, so the case has to match exactly. Assuming
        # uppercase produced a confident STALE against an extension that was current.
        acc += hashlib.sha256(p.read_bytes()).hexdigest()
    acc += f"module_pathname={module_pathname}"
    return hashlib.sha256(acc.encode()).hexdigest()[:16]

def installed_versions():
    found = []
    for base in ("/opt/laplace/share/postgresql", "/opt/laplace/pgsql-18/share"):
        d = pathlib.Path(base)
        if not d.exists(): continue
        for p in d.rglob("laplace_substrate--*.sql"):
            m = re.fullmatch(r"laplace_substrate--([0-9a-fA-F]{16})\.sql", p.name)
            if m: found.append((m.group(1), p))
    return found

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--module-pathname", default="laplace_substrate",
                    help="EXT_MODULE_PATHNAME the install was configured with")
    a = ap.parse_args()

    installed = installed_versions()
    if not installed:
        print("no installed laplace_substrate--<version>.sql found", file=sys.stderr)
        return 2

    want = source_version(a.module_pathname)
    if want is None:
        return 2

    names = {v for v, _ in installed}
    print(f"source version   : {want}")
    for v, p in sorted(installed):
        print(f"installed        : {v}  {p}")

    if want in names:
        print("installed extension is built from the current source")
        return 0
    print("\nSTALE: no installed extension matches the current source.\n"
          "pg_regress tests the INSTALLED extension, so a green regress run describes that\n"
          "artifact and not this tree. Run pipeline.sh install (and build-extensions\n"
          "--reconfigure for SQL changes) before reading a regress result as evidence.",
          file=sys.stderr)
    return 1

if __name__ == "__main__":
    sys.exit(main())
