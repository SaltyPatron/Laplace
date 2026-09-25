#!/usr/bin/env python3
"""Read-only native compatibility guard with distinct publication and recording scopes.

Observes the existing CMake configuration, a temporary DESTDIR CMake install for
exact installed-form native comparisons, raw ROM comparisons, actual PostgreSQL native file mappings, live extension
versions and the migration journal. Build execution and source provenance belong
to the build/install owner; configuration observation is not a substitute for them.
No bootstrap, installation into the live prefix, SQL writes, service action,
network DB auth or secret output.
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
FORMAT = 3
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
    "laplace_substrate.vocabulary_perfcache_path": "laplace_vocabulary_perfcache.bin",
    "laplace_substrate.chess_position_perfcache_path": "laplace_chess_position_perfcache.bin",
}
SQL = """
BEGIN READ ONLY;
WITH loaded AS MATERIALIZED (
 SELECT public.laplace_geom_version() AS geom,
        laplace.laplace_substrate_version() AS substrate
)
SELECT json_build_object(
 'native_mappings',(SELECT json_agg(line)
     FROM regexp_split_to_table(
       CASE WHEN loaded.geom IS NOT NULL AND loaded.substrate IS NOT NULL
            THEN pg_read_file('/proc/self/maps') END, chr(10)) AS line
     WHERE line ~ '/(liblaplace_(core|dynamics|synthesis)[.]so([.][0-9]+)*|laplace_(geom|substrate)[.]so|laplace_execution_[0-9a-f]{16}[.]so)( [(]deleted[)])?$'),
 'database',current_database(),
 'database_oid',(SELECT oid::bigint FROM pg_catalog.pg_database WHERE datname=current_database()),
 'system_identifier',(SELECT system_identifier::text FROM pg_catalog.pg_control_system()),
 'server_version',current_setting('server_version_num'),
 'postmaster_started',pg_postmaster_start_time(),
 'extensions',(SELECT json_object_agg(extname,extversion) FROM pg_extension
     WHERE extname IN ('laplace_geom','laplace_substrate')),
 'migrations',(SELECT json_agg(scriptname ORDER BY scriptname) FROM public.schemaversions),
 'running_ingests',(SELECT count(*) FROM laplace.ingest_run_journal WHERE status='running'),
 'roms',(SELECT json_object_agg(name,setting) FROM pg_settings
     WHERE name IN ('laplace_substrate.perfcache_path','laplace_substrate.highway_perfcache_path',
                   'laplace_substrate.vocabulary_perfcache_path',
                   'laplace_substrate.chess_position_perfcache_path')),
 'extension_functions',(SELECT md5(string_agg(pg_get_functiondef(p.oid),E'\\n' ORDER BY p.oid))
     FROM pg_proc p JOIN pg_depend d ON d.objid=p.oid AND d.classid='pg_proc'::regclass
       AND d.refclassid='pg_extension'::regclass AND d.deptype='e'
     JOIN pg_extension e ON e.oid=d.refobjid
     WHERE e.extname IN ('laplace_geom','laplace_substrate') AND p.prokind='f'))
FROM loaded;
ROLLBACK;
"""


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def build_identity(root, prefix):
    """Observe the configured build; never invent or require build/install stamps."""
    root, prefix = Path(root).resolve(strict=True), Path(prefix).resolve(strict=True)
    build = (root / "build").resolve(strict=True)
    cache_path = build / "CMakeCache.txt"
    program = build / "cmake_install.cmake"
    cache = cache_path.read_text()

    def directory(key, kind):
        values = re.findall(r"^" + re.escape(key) + ":" + kind + r"=(.*)$",
                            cache, re.MULTILINE)
        if len(values) != 1 or not Path(values[0]).is_absolute():
            raise ValueError("configured CMake directory is missing or ambiguous: " + key)
        return Path(values[0]).resolve(strict=True)

    for key, kind, expected in (
            ("CMAKE_HOME_DIRECTORY", "INTERNAL", root),
            ("CMAKE_CACHEFILE_DIR", "INTERNAL", build),
            ("CMAKE_INSTALL_PREFIX", "PATH", prefix)):
        if directory(key, kind) != expected:
            raise ValueError("configured CMake directory differs: " + key)
    if not program.is_file():
        raise ValueError("configured CMake install program missing from prepared build")
    return {"directory": str(build), "sourceDirectory": str(root),
            "installPrefix": str(prefix), "cacheSha256": digest(cache_path),
            "installProgramSha256": digest(program)}

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
        # Locate the manifest in the configured staged extension share directory.
        manifests = list(expected_prefix.rglob("laplace_execution_module.txt"))
        if len(manifests) != 1:
            raise ValueError("tested install must contain one execution module manifest")
        name = manifests[0].read_text().strip()
        if not re.fullmatch(r"laplace_execution_[0-9a-f]{16}", name):
            raise ValueError("invalid native execution module identity")
        installed_paths = [*MODULES.values(), f"lib/postgresql/18/{name}.so",
                           str(manifests[0].relative_to(expected_prefix))]
        for installed in installed_paths:
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


def mapped_native_identities(prefix, database, hashes, *, required=None):
    """Bind this fresh backend's mapped files to the selected installed generation.

    Device/inode and pathname prove file generation, while installed_native_hashes
    verifies that generation's installed bytes. This does not claim to inspect
    private modified memory pages or authenticate an in-place library overwrite.
    The install owner must replace libraries and restart their preload owner.
    Before SQL upgrade, that owner may supply only the configured preload modules
    as required; an explicitly empty set permits an initially unloaded postmaster.
    """
    required = set(("lib/liblaplace_core.so", "lib/postgresql/18/laplace_geom.so",
                    "lib/postgresql/18/laplace_substrate.so") if required is None else required)
    rows = database.get("native_mappings")
    if not isinstance(rows, list) or (not rows and required) or len(rows) > 4096:
        raise ValueError("running PostgreSQL native mappings are missing or exceed their bound")
    selected = {}
    for relative in hashes:
        if relative.endswith(".so"):
            path = (prefix / relative).resolve(strict=True)
            if path in selected:
                raise ValueError("ambiguous selected native library path")
            selected[path] = relative
    observed = {}
    executable = set()
    for line in rows:
        if not isinstance(line, str) or len(line) > 8192:
            raise ValueError("invalid running PostgreSQL native mapping")
        fields = line.split(None, 5)
        if (len(fields) != 6 or re.fullmatch(r"[0-9a-f]+-[0-9a-f]+", fields[0]) is None
                or re.fullmatch(r"[r-][w-][x-][ps]", fields[1]) is None
                or re.fullmatch(r"[0-9a-f]+", fields[2]) is None
                or re.fullmatch(r"[0-9a-f]+:[0-9a-f]+", fields[3]) is None
                or re.fullmatch(r"[0-9]+", fields[4]) is None):
            raise ValueError("invalid running PostgreSQL native mapping")
        raw_path = fields[5]
        if raw_path.endswith(" (deleted)"):
            raise ValueError("running PostgreSQL retains a replaced native library; "
                             "restart the PostgreSQL preload owner: " + raw_path)
        path = Path(raw_path)
        if not path.is_absolute():
            raise ValueError("running PostgreSQL native library path is not absolute")
        path = path.resolve(strict=True)
        relative = selected.get(path)
        if relative is None:
            raise ValueError("running PostgreSQL maps an unselected native library: " + raw_path)
        details = path.stat()
        major, minor = (int(part, 16) for part in fields[3].split(":"))
        if (os.makedev(major, minor), int(fields[4])) != (details.st_dev, details.st_ino):
            raise ValueError("running PostgreSQL native mapping differs from the installed file; "
                             "restart the PostgreSQL preload owner: " + raw_path)
        identity = {"path": str(path), "device": details.st_dev, "inode": details.st_ino}
        if relative in observed and observed[relative] != identity:
            raise ValueError("running PostgreSQL maps conflicting native file generations")
        observed[relative] = identity
        if fields[1][2] == "x":
            executable.add(relative)
    if not required.issubset(executable):
        raise ValueError("running PostgreSQL lacks an executable mapping for a required native library")
    # PID and ASLR addresses vary per connection. Compare the verified file
    # generations, retaining all runtime/library compatibility distinctions.
    return {key: observed[key] for key in sorted(observed)}


def database_incarnation(database):
    """Require exact observed cluster/database identities for current proof.

    A database name and postmaster start do not identify a database across
    DROP/CREATE. Preserve the SQL bigint cluster identifier as decimal text, without
    a floating-point conversion, and the database OID as an exact JSON integer.
    """
    oid = database.get("database_oid")
    system = database.get("system_identifier")
    if type(oid) is not int or not 0 < oid <= 0xffffffff:
        raise ValueError("database incarnation requires an exact database OID")
    if (not isinstance(system, str) or re.fullmatch(r"-?[1-9][0-9]{0,18}", system) is None
            or not -0x8000000000000000 <= int(system) <= 0x7fffffffffffffff):
        raise ValueError("database incarnation requires an exact cluster system identifier")
    return system, oid


def snapshot(root, prefix, database, *, purpose="publication"):
    if purpose not in ("publication", "recording"):
        raise ValueError("unknown runtime guard purpose")
    database_incarnation(database)
    identity = build_identity(root, prefix)
    build = Path(identity["directory"])
    if not 180000 <= int(database["server_version"]) < 190000:
        raise ValueError("application runtime guard requires the deployed PostgreSQL 18 contract")
    if type(database["running_ingests"]) is not int or database["running_ingests"] < 0:
        raise ValueError("invalid ingest journal observation")
    if not database["extension_functions"]:
        raise ValueError("live extension function contract is missing")
    migrations = {p.name for p in (root / "db/migrations").glob("*.sql")}
    if not migrations or not migrations.issubset(set(database["migrations"] or [])):
        raise ValueError("pending/unknown migrations; use the full database pipeline")

    hashes = installed_native_hashes(root, prefix)
    mapped = mapped_native_identities(prefix, database, hashes)
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
    pair = build / "engine/core/perfcache/chess-floor-pair.json"
    installed_pair = prefix / "share/laplace/chess-floor/current/receipt.json"
    if pair.is_file() or installed_pair.exists():
        expected_pair = json.loads(pair.read_text())
        actual_pair = json.loads(installed_pair.read_text())
        if expected_pair != actual_pair:
            raise ValueError("installed chess floor generation differs from the sealed build pair")
        for filename, field in (
                ("laplace_chess_position_perfcache.bin", "laplace_substrate.chess_position_perfcache_path"),
                ("laplace_chess_transition_perfcache.bin", "laplace_chess_transition_perfcache.bin")):
            if actual_pair["files"][filename]["sha256"] != hashes[field]:
                raise ValueError("installed chess floor generation receipt differs from installed-file bytes")
        hashes["chess_floor_pair_receipt"] = digest(installed_pair)
    if build_identity(root, prefix) != identity:
        raise ValueError("configured native build changed during runtime observation")
    state = {"format": FORMAT, "build": identity, "artifacts": hashes,
             "database": copy.deepcopy(database)}
    state["database"]["native_mappings"] = mapped
    if purpose == "recording":
        state["purpose"] = purpose
    return state


def installed_runtime_snapshot(root, prefix, database):
    """Bind API-only publication to the already-installed native/SQL runtime.

    A managed-only candidate deliberately has no CMake tree. Its publication
    contract is that the installed native and database generation remains
    byte-for-byte and identity-for-identity unchanged across the API swap.
    """
    database_incarnation(database)
    if not 180000 <= int(database["server_version"]) < 190000:
        raise ValueError("application runtime guard requires the deployed PostgreSQL 18 contract")
    if type(database["running_ingests"]) is not int or database["running_ingests"] < 0:
        raise ValueError("invalid ingest journal observation")
    if not database["extension_functions"]:
        raise ValueError("live extension function contract is missing")
    migrations = {path.name for path in (root / "db/migrations").glob("*.sql")}
    if not migrations or not migrations.issubset(set(database["migrations"] or [])):
        raise ValueError("pending/unknown migrations; use the full database pipeline")

    manifest = prefix / "share/postgresql/18/extension/laplace_execution_module.txt"
    execution = manifest.read_text().strip()
    if not re.fullmatch(r"laplace_execution_[0-9a-f]{16}", execution):
        raise ValueError("invalid installed native execution module identity")
    installed_paths = [*MODULES.values(), "lib/liblaplace_syzygy.so",
                       f"lib/postgresql/18/{execution}.so",
                       str(manifest.relative_to(prefix))]
    hashes = {}
    for relative in installed_paths:
        path = prefix / relative
        if not path.is_file():
            raise ValueError(f"installed native artifact missing: {relative}")
        hashes[relative] = digest(path)

    mapped = mapped_native_identities(prefix, database, hashes)
    for name in ("laplace_geom", "laplace_substrate"):
        installed = control_version(prefix / "share/postgresql/18/extension" / f"{name}.control")
        if installed != database["extensions"].get(name):
            raise ValueError(f"{name} installed SQL version differs from the database")
    for setting in ROMS:
        path = Path(database["roms"].get(setting, ""))
        if not path.is_absolute() or not path.resolve().is_relative_to(prefix.resolve()):
            raise ValueError(f"unverified installed ROM path: {setting}")
        hashes[setting] = digest(path)
    for filename in ("laplace_chess_transition_perfcache.bin", "laplace_modality_number_perfcache.bin"):
        hashes[filename] = digest(prefix / "share/laplace" / filename)
    installed_pair = prefix / "share/laplace/chess-floor/current/receipt.json"
    if installed_pair.exists():
        hashes["chess_floor_pair_receipt"] = digest(installed_pair)

    state = {"format": FORMAT, "scope": "installed-runtime", "artifacts": hashes,
             "database": copy.deepcopy(database)}
    state["database"]["native_mappings"] = mapped
    return state


def compatible(before, after, *, purpose="publication"):
    """Compare runtime contracts while retaining journal progress as an observation.

    A journal row may describe either a live or an interrupted ingest. Its count
    does not change the native artifacts, SQL contract or application compatibility.
    Publication and recording preserve that count in their original receipts.
    Historical formats remain historical and cannot be upgraded by comparison;
    current proof requires the observed cluster/database incarnation on both sides.
    """
    if purpose not in ("publication", "recording"):
        raise ValueError("unknown runtime guard purpose")
    before = copy.deepcopy(before)
    after = copy.deepcopy(after)
    for state in (before, after):
        if type(state.get("format")) is not int or state["format"] != FORMAT:
            return False
        database_incarnation(state["database"])
        if purpose == "recording" and state.get("purpose") != "recording":
            raise ValueError("recording comparison requires recording snapshots")
        if purpose == "publication" and state.get("purpose") is not None:
            return False
        count = state["database"].pop("running_ingests")
        if type(count) is not int or count < 0:
            raise ValueError("invalid ingest journal observation")
    return before == after


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo-root", type=Path, default=ROOT)
    parser.add_argument("--snapshot", type=Path)
    parser.add_argument("--purpose", choices=("publication", "recording"), default="publication")
    parser.add_argument("--installed-runtime", action="store_true")
    parser.add_argument("--compare", type=Path)
    args = parser.parse_args()
    prefix = Path(os.environ.get("LAPLACE_INSTALL_PREFIX", "/opt/laplace"))
    pg_prefix = Path(os.environ.get("LAPLACE_PG_PREFIX", "/opt/laplace/pgsql-18"))
    database = read_database(pg_prefix)
    state = (installed_runtime_snapshot(args.repo_root, prefix, database)
             if args.installed_runtime else
             snapshot(args.repo_root, prefix, database, purpose=args.purpose))
    if args.compare and not compatible(json.loads(args.compare.read_text()), state, purpose=args.purpose):
        raise ValueError("native/database runtime changed during the guarded operation")
    if args.snapshot:
        with args.snapshot.open("x") as output:
            json.dump(state, output, sort_keys=True)
    scope = state.get("scope", "configured-build")
    print(f"PASS: installed-form and mapped native artifacts, installed SQL versions and applied migrations match; "
          f"scope={scope}; purpose={args.purpose}; observed_running_ingests={state['database']['running_ingests']}")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        # Do not dump subprocess stderr or inherited environment/credentials.
        raise SystemExit(f"application runtime guard failed: {error}")
