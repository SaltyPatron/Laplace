#!/usr/bin/env python3
"""Fail-closed, read-only guard for publishing apps against an unchanged engine.

Uses existing successful build/install fingerprints, a temporary DESTDIR CMake install
for exact installed-form native comparisons, raw ROM comparisons, live extension
versions and the migration journal. No bootstrap, installation into the live prefix,
SQL writes, service action, network DB auth or secret output.
"""
import argparse
import copy
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
MODULES = {
    "engine/core/liblaplace_core.so": "lib/liblaplace_core.so",
    "engine/dynamics/liblaplace_dynamics.so": "lib/liblaplace_dynamics.so",
    "engine/synthesis/liblaplace_synthesis.so": "lib/liblaplace_synthesis.so",
    "extension/laplace_geom/laplace_geom.so": "lib/postgresql/18/laplace_geom.so",
    "extension/laplace_substrate/laplace_substrate.so": "lib/postgresql/18/laplace_substrate.so",
}
ROMS = {
    "laplace_substrate.perfcache_path": "laplace_t0_perfcache.bin",
    "laplace_substrate.highway_perfcache_path": "laplace_highway_perfcache.bin",
    "laplace_substrate.chess_position_perfcache_path": "laplace_chess_position_perfcache.bin",
}
SQL = """
BEGIN READ ONLY;
SELECT json_build_object(
 'database',current_database(), 'server_version',current_setting('server_version_num'),
 'postmaster_started',pg_postmaster_start_time(),
 'extensions',(SELECT json_object_agg(extname,extversion) FROM pg_extension
     WHERE extname IN ('laplace_geom','laplace_substrate')),
 'migrations',(SELECT json_agg(scriptname ORDER BY scriptname) FROM public.schemaversions),
 'running_ingests',(SELECT count(*) FROM laplace.ingest_run_journal WHERE status='running'),
 'roms',(SELECT json_object_agg(name,setting) FROM pg_settings
     WHERE name IN ('laplace_substrate.perfcache_path','laplace_substrate.highway_perfcache_path',
                   'laplace_substrate.chess_position_perfcache_path')),
 'extension_functions',(SELECT md5(string_agg(pg_get_functiondef(p.oid),E'\\n' ORDER BY p.oid))
     FROM pg_proc p JOIN pg_depend d ON d.objid=p.oid AND d.classid='pg_proc'::regclass
       AND d.refclassid='pg_extension'::regclass AND d.deptype='e'
     JOIN pg_extension e ON e.oid=d.refobjid
     WHERE e.extname IN ('laplace_geom','laplace_substrate') AND p.prokind='f'));
ROLLBACK;
"""


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def native_fingerprint(root):
    subprocess.run(["git", "-C", str(root), "rev-parse", "--show-toplevel"],
                   check=True, capture_output=True, text=True, timeout=10)
    result = subprocess.run(["bash", "-c", 'ROOT="$1"; source "$ROOT/scripts/lib/fp.sh"; fp_native',
                             "application-guard", str(root)], check=True, capture_output=True,
                            text=True, timeout=30)
    value = result.stdout.strip()
    if not re.fullmatch(r"[0-9a-f]{64}", value):
        raise ValueError("native fingerprint unavailable")
    return value


def read_database(pg_prefix):
    socket = os.environ.get("PGHOST", "/var/run/postgresql")
    if not socket.startswith("/") or "," in socket:
        raise ValueError("application releases require a local PostgreSQL socket")
    env = {k: v for k, v in os.environ.items() if not k.startswith("PG")}
    env["PGOPTIONS"] = "-c default_transaction_read_only=on -c statement_timeout=10000"
    result = subprocess.run([str(pg_prefix / "bin/psql"), "-X", "-w", "-qAt",
                             "-h", socket, "-U", "laplace_admin", "-d",
                             os.environ.get("PGDATABASE", "laplace"),
                             "-v", "ON_ERROR_STOP=1", "-c", SQL],
                            env=env, check=True, capture_output=True, text=True, timeout=20)
    return json.loads(result.stdout)


def control_version(path):
    match = re.search(r"^default_version\s*=\s*'([^']+)'", path.read_text(), re.MULTILINE)
    if not match:
        raise ValueError(f"extension version missing: {path}")
    return match[1]


@contextmanager
def staged_install(root, prefix):
    """Materialize the tested build in a temporary DESTDIR using CMake's install law.

    Build-tree ELFs deliberately carry ``$ORIGIN`` while installed ELFs carry the
    configured absolute runtime search path. Therefore build bytes are not the installed
    artifact identity. Running the already-generated install program under DESTDIR applies
    the same RPATH/install transforms without writing the live prefix.
    """
    root = root.resolve()
    prefix = prefix.resolve()
    build = root / "build"
    if not (build / "cmake_install.cmake").is_file():
        raise ValueError("configured CMake install program missing from tested build")
    if not prefix.is_absolute():
        raise ValueError("application runtime install prefix must be absolute")

    with tempfile.TemporaryDirectory(prefix="laplace-installed-form-") as temporary:
        stage = Path(temporary)
        env = dict(os.environ)
        env["DESTDIR"] = str(stage)
        try:
            subprocess.run(
                ["cmake", "--install", str(build)],
                cwd=root,
                env=env,
                check=True,
                capture_output=True,
                text=True,
                timeout=120,
            )
        except subprocess.SubprocessError as error:
            raise ValueError("could not materialize tested CMake installed form") from error
        yield stage / prefix.relative_to(prefix.anchor)


def installed_native_hashes(root, prefix):
    """Prove live native files equal the installed form of the tested build."""
    hashes = {}
    with staged_install(root, prefix) as expected_prefix:
        for _built, installed in MODULES.items():
            expected = expected_prefix / installed
            actual = prefix / installed
            if not expected.is_file():
                raise ValueError(f"tested CMake install omitted native artifact: {installed}")
            if not actual.is_file():
                raise ValueError(f"installed native artifact missing: {installed}")
            expected_hash = digest(expected)
            actual_hash = digest(actual)
            if expected_hash != actual_hash:
                raise ValueError(
                    f"installed native artifact differs from tested installed form: {installed}"
                )
            hashes[installed] = actual_hash
    return hashes


def snapshot(root, prefix, database, fingerprint):
    build = root / "build"
    for stamp in ("build-native", "install-native"):
        path = build / ".stamps" / stamp
        if not path.is_file() or path.read_text().strip() != fingerprint:
            raise ValueError(f"{stamp} differs from target native sources; use the full engine pipeline")
    if not 180000 <= int(database["server_version"]) < 190000:
        raise ValueError("application runtime guard requires the deployed PostgreSQL 18 contract")
    if database["running_ingests"] != 0:
        raise ValueError("running/unresolved ingest journal entries; application publish postponed")
    if not database["extension_functions"]:
        raise ValueError("live extension function contract is missing")
    migrations = {p.name for p in (root / "db/migrations").glob("*.sql")}
    if not migrations or not migrations.issubset(set(database["migrations"] or [])):
        raise ValueError("pending/unknown migrations; use the full database pipeline")

    hashes = installed_native_hashes(root, prefix)
    for name in ("laplace_geom", "laplace_substrate"):
        built = control_version(build / "extension" / name / f"{name}.control")
        installed = control_version(prefix / "share/postgresql/18/extension" / f"{name}.control")
        if built != installed or built != database["extensions"].get(name):
            raise ValueError(f"{name} SQL version differs; use the full database pipeline")
    for setting, filename in ROMS.items():
        path = Path(database["roms"].get(setting, ""))
        if not path.is_absolute() or not path.resolve().is_relative_to(prefix.resolve()):
            raise ValueError(f"unverified installed ROM path: {setting}")
        actual = digest(path)
        if digest(build / "engine/core/perfcache" / filename) != actual:
            raise ValueError(f"installed ROM differs from tested build: {filename}")
        hashes[setting] = actual
    for filename in ("laplace_chess_transition_perfcache.bin", "laplace_modality_number_perfcache.bin"):
        actual = digest(prefix / "share/laplace" / filename)
        if digest(build / "engine/core/perfcache" / filename) != actual:
            raise ValueError(f"installed ROM differs from tested build: {filename}")
        hashes[filename] = actual
    return {"format": 1, "native_fingerprint": fingerprint, "artifacts": hashes,
            "database": copy.deepcopy(database)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo-root", type=Path, default=ROOT)
    parser.add_argument("--snapshot", type=Path)
    parser.add_argument("--compare", type=Path)
    args = parser.parse_args()
    prefix = Path(os.environ.get("LAPLACE_INSTALL_PREFIX", "/opt/laplace"))
    pg_prefix = Path(os.environ.get("LAPLACE_PG_PREFIX", "/opt/laplace/pgsql-18"))
    state = snapshot(args.repo_root, prefix, read_database(pg_prefix), native_fingerprint(args.repo_root))
    if args.compare and state != json.loads(args.compare.read_text()):
        raise ValueError("native/database runtime changed during application publish; refusing commit")
    if args.snapshot:
        with args.snapshot.open("x") as output:
            json.dump(state, output, sort_keys=True)
    print("PASS: tested installed-form native artifacts, installed SQL versions, applied migrations and idle ingest state match")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        # Do not dump subprocess stderr or inherited environment/credentials.
        raise SystemExit(f"application runtime guard failed: {error}")
