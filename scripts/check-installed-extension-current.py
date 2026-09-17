#!/usr/bin/env python3
"""Is the INSTALLED laplace_substrate extension built from the current configured source?

pg_regress tests the INSTALLED extension, never an edited .sql.in. So a green regress run
describes whatever was last installed, and says nothing about the tree -- the same shape as
managed tests loading /opt/laplace/lib/liblaplace_core.so instead of build/engine/core
(fixed 2026-08-24). That one was silent; this one is documented, and still undetectable
without recomputing the configured extension version.

The extension version is a content hash of its SQL inputs plus the configured native
execution-module identity (extension/laplace_substrate/CMakeLists.txt). Ordinary shipped
SQL inputs are hashed directly. The two manifest-generated attestation-law seed fragments
are hashed from their canonical generator + manifest inputs, exactly as CMake does, so a
post-codegen working tree cannot disagree with the version that was just configured and
installed.

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


def _sha256_file(path):
    try:
        return hashlib.sha256(path.read_bytes()).hexdigest()
    except OSError as exc:
        print(f"missing hashed input: {path} ({exc})", file=sys.stderr)
        return None


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

    generated_relation_seed = SQL / "generated" / "seed_relation_types.sql.in"
    generated_pos_seed = SQL / "generated" / "seed_pos.sql.in"
    generated = {generated_relation_seed, generated_pos_seed}

    codegen_hash = relation_manifest_hash = pos_manifest_hash = None
    if any(p in generated for p in ordered):
        codegen_hash = _sha256_file(ROOT / "scripts" / "codegen-attestation-law.py")
        relation_manifest_hash = _sha256_file(ROOT / "engine" / "manifest" / "relation_types.toml")
        pos_manifest_hash = _sha256_file(ROOT / "engine" / "manifest" / "pos_tags.toml")
        if None in (codegen_hash, relation_manifest_hash, pos_manifest_hash):
            return None

    acc = ""
    for p in ordered:
        if p == generated_relation_seed:
            canonical = (
                "generated=seed_relation_types;"
                f"generator={codegen_hash};manifest={relation_manifest_hash}"
            )
            acc += hashlib.sha256(canonical.encode()).hexdigest()
        elif p == generated_pos_seed:
            canonical = (
                "generated=seed_pos;"
                f"generator={codegen_hash};manifest={pos_manifest_hash}"
            )
            acc += hashlib.sha256(canonical.encode()).hexdigest()
        else:
            digest = _sha256_file(p)
            if digest is None:
                return None
            acc += digest

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
