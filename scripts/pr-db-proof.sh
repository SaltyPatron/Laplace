#!/usr/bin/env bash
# Execute the exact pull-request native extension build against an isolated
# throwaway PostgreSQL cluster. The proof must not reuse the production
# postmaster: production preloads the installed laplace_substrate image, while
# PR proof preloads only the branch host image from the build tree. Loading both
# copies in one postmaster re-registers custom GUCs and makes CREATE EXTENSION
# fail before branch SQL is exercised.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

# shellcheck source=scripts/lib/storage.sh
source "$ROOT/scripts/lib/storage.sh"
laplace_storage_init

BUILD="$ROOT/build"
PG_PREFIX="${LAPLACE_PG_PREFIX:-/opt/laplace/pgsql-18}"
INSTALL_PREFIX="${LAPLACE_INSTALL_PREFIX:-/opt/laplace}"
REGRESS_DB="${LAPLACE_REGRESS_DB:-}"

action="${2:-all}"
[[ $# == 0 || ( $# == 2 && "$1" == --phase ) ]] || { echo "usage: pr-db-proof.sh [--phase PHASE]" >&2; exit 2; }
state_file="${LAPLACE_CI_SESSION_DIRECTORY:-$BUILD/test-results/pr-session}/private-db.json"

private_paths() {
  pgdata="$stage/pgdata"
  socket_dir="$stage/socket"
  control_root="$stage${INSTALL_PREFIX}/share/postgresql/18"
  build_library_path="$BUILD/extension/laplace_substrate:$BUILD/extension/laplace_geom:$BUILD/engine/core:$BUILD/engine/dynamics:$BUILD/engine/synthesis"
  branch_host="$BUILD/extension/laplace_substrate/laplace_substrate"
  t0_perfcache="$BUILD/engine/core/perfcache/laplace_t0_perfcache.bin"
  highway_perfcache="$BUILD/engine/core/perfcache/laplace_highway_perfcache.bin"
  chess_position_perfcache="$BUILD/engine/core/perfcache/laplace_chess_position_perfcache.bin"
  export PGHOST="$socket_dir" PGPORT=55432 PGUSER=laplace_admin PGDATABASE=postgres
  export PGOPTIONS="-c extension_control_path=${control_root}:\$system -c dynamic_library_path=${build_library_path}:\$libdir"
}

# This file is data, never shell code. Pin the private directory to this exact
# checkout/build and workflow invocation before starting any daemon.
private_state() {
  python3 - "$1" "$state_file" "$ROOT" "$BUILD" "$PG_PREFIX" "$INSTALL_PREFIX" "${stage:-}" "$REGRESS_DB" <<'PY_STATE'
import json, os, pathlib, stat, subprocess, sys
operation, name, root, build, pg, install, stage, database = sys.argv[1:]
path = pathlib.Path(name)
identity = {key: os.environ.get(key, '') for key in ('GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT', 'GITHUB_JOB')}
expected = dict(schema='laplace.private-db-session/v1', checkout=str(pathlib.Path(root).resolve()),
                build=str(pathlib.Path(build).resolve()), pg_prefix=str(pathlib.Path(pg).resolve()),
                install_prefix=install, identity=identity)
def owned_directory(directory):
    info = directory.lstat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o077:
        raise SystemExit('private database directory is not an owned private directory')
if operation == 'write':
    path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    owned_directory(path.parent)
    expected.update(stage=stage, database=database,
                    source=subprocess.check_output(['git', '-C', root, 'rev-parse', 'HEAD'], text=True).strip())
    temporary = path.with_name(path.name + '.next')
    descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(descriptor, 'w') as output:
        json.dump(expected, output); output.flush(); os.fsync(output.fileno())
    temporary.replace(path)
else:
    owned_directory(path.parent)
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    with os.fdopen(descriptor, 'rb') as source:
        info = os.fstat(source.fileno())
        if not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o077 or info.st_size > 8192:
            raise SystemExit('invalid private database session receipt')
        saved = json.load(source)
    if set(saved) != set(expected) | {'stage', 'database', 'source'} or any(saved.get(k) != v for k, v in expected.items()):
        raise SystemExit('private database session belongs to another checkout, build, or run')
    if operation != 'cleanup' and (saved['database'] != database or saved['source'] != subprocess.check_output(['git', '-C', root, 'rev-parse', 'HEAD'], text=True).strip()):
        raise SystemExit('private database source or database identity changed')
    directory = pathlib.Path(saved['stage'])
    if not directory.is_absolute() or directory.name.removeprefix('laplace-pr-db-proof.') == directory.name or any(c in str(directory) for c in "\n\r\x00'"):
        raise SystemExit('invalid private database stage path')
    if directory.exists():
        owned_directory(directory)
        if directory.resolve() != directory:
            raise SystemExit('private database stage traverses a symlink')
    elif operation != 'cleanup':
        raise SystemExit('private database stage is missing')
    print(directory)
PY_STATE
}

load_state() {
  stage="$(private_state "${1:-read}")" || return $?
  private_paths
}

retain_private_postgresql_log() {
  local evidence
  evidence="${LAPLACE_NATIVE_REGRESSION_EVIDENCE_DIRECTORY:-/build/laplace/work/native-regression-evidence/${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-0}-pr}/current"
  python3 - "$stage/postgresql.log" "$evidence/private-postgresql.log" <<'PY_LOG'
import json, os, pathlib, stat, sys
source, destination = map(pathlib.Path, sys.argv[1:])
try:
    descriptor = os.open(source, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
except FileNotFoundError:
    raise SystemExit(0)
with os.fdopen(descriptor, 'rb') as stream:
    info = os.fstat(stream.fileno())
    if not stat.S_ISREG(info.st_mode):
        raise SystemExit('private PostgreSQL log is not a regular file')
    stream.seek(max(0, info.st_size - 65536))
    tail = stream.read(65536)
destination.parent.mkdir(parents=True, exist_ok=True)
descriptor = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o600)
with os.fdopen(descriptor, 'wb') as output:
    output.write(tail)
print(f'PRIVATE_POSTGRESQL_LOG source_bytes={info.st_size} retained_bytes={len(tail)} path={destination}')
# Escape control bytes so server messages cannot become workflow commands.
print('PRIVATE_POSTGRESQL_LOG_TAIL ' + json.dumps(tail[-8192:].decode('utf-8', errors='replace')))
PY_LOG
}

cleanup() {
  [[ -f "$state_file" || -L "$state_file" ]] || return 0
  load_state cleanup || return $?
  # Keep the actual startup/phase error before removing its private directory.
  # Diagnostic failure must never replace the original phase or cleanup status.
  [[ "${1:-}" == quiet ]] || retain_private_postgresql_log || true
  # Bind the signal to the actual private postmaster, not a reusable PID. The
  # receipt also exists during startup, before pg_ctl has returned successfully.
  python3 - "$pgdata" "$PG_PREFIX/bin/postgres" <<'PY_STOP' || return $?
import os, pathlib, select, signal, sys
pgdata, executable = map(pathlib.Path, sys.argv[1:])
path = pgdata / 'postmaster.pid'
if not path.exists():
    raise SystemExit(0)
lines = path.read_text().splitlines()
if len(lines) < 2 or pathlib.Path(lines[1]).resolve() != pgdata.resolve():
    raise SystemExit('private postmaster PID file has another data directory')
try:
    pid = int(lines[0])
    if pid <= 0:
        raise ValueError()
    descriptor = os.pidfd_open(pid)
except ProcessLookupError:
    raise SystemExit(0)
except ValueError:
    raise SystemExit('invalid private postmaster PID')
try:
    process = pathlib.Path('/proc') / str(pid)
    try:
        arguments = (process / 'cmdline').read_bytes().split(b'\0')
        actual_executable = (process / 'exe').resolve(strict=True)
        owner = process.stat().st_uid
    except FileNotFoundError:
        if select.select([descriptor], [], [], 0)[0]:
            raise SystemExit(0)
        raise SystemExit('live private postmaster identity is unavailable; retaining its directory')
    if (owner != os.getuid() or actual_executable != executable.resolve()
            or b'-D' not in arguments or arguments[arguments.index(b'-D') + 1] != os.fsencode(pgdata)):
        raise SystemExit('refusing to signal a process that is not this private postmaster')
    try:
        signal.pidfd_send_signal(descriptor, signal.SIGQUIT)
    except ProcessLookupError:
        raise SystemExit(0)
    if not select.select([descriptor], [], [], 110)[0]:
        raise SystemExit('private postmaster did not stop; retaining its directory')
finally:
    os.close(descriptor)
PY_STOP
  rm -rf -- "$stage" || return $?
  rm -f -- "$state_file"
}

if [[ "$action" == cleanup ]]; then
  cleanup
  exit $?
fi

[[ -n "$REGRESS_DB" ]] || {
  echo "pr-db-proof: LAPLACE_REGRESS_DB is required" >&2
  exit 2
}
[[ "$REGRESS_DB" =~ ^laplace_[A-Za-z0-9_]+$ ]] || {
  echo "pr-db-proof: refusing non-isolated database name: $REGRESS_DB" >&2
  exit 2
}
[[ "$REGRESS_DB" != "${PGDATABASE:-laplace}" && "$REGRESS_DB" != "laplace" ]] || {
  echo "pr-db-proof: refusing canonical database: $REGRESS_DB" >&2
  exit 2
}
[[ -f "$BUILD/cmake_install.cmake" ]] || {
  echo "pr-db-proof: build/cmake_install.cmake is missing; build the exact revision first" >&2
  exit 2
}

for tool in initdb pg_ctl psql createdb dropdb; do
  [[ -x "$PG_PREFIX/bin/$tool" ]] || {
    echo "pr-db-proof: PostgreSQL tool missing: $PG_PREFIX/bin/$tool" >&2
    exit 2
  }
done


# The build selects one authenticated Kitware generation. Its companion CTest
# must read the discovery files generated by that CMake, regardless of PATH.
# Cleanup never enters this function or needs an available CMake generation.
cmake_tool() {
  python3 "$ROOT/scripts/provision-cmake.py" \
    --root "$INSTALL_PREFIX/tools/cmake" \
    --work "${LAPLACE_WORK_ROOT:-/build/laplace/work}/cmake" \
    --exec-tool "$@"
}

prepare_private_database() {
stage="$(realpath "$(mktemp -d -t laplace-pr-db-proof.XXXXXXXX)")"
private_paths
mkdir -p "$socket_dir"
private_state write


# Materialize the branch's generated extension SQL/control files without writing
# the live prefix. The installed-form SQL is the same artifact main delivery
# would publish, while the C modules themselves remain the exact build-tree ELFs.
DESTDIR="$stage" cmake_tool cmake -- --install "$BUILD" >/dev/null
staged_prefix="$stage${INSTALL_PREFIX}"
control_root="$staged_prefix/share/postgresql/18"
control_dir="$control_root/extension"

[[ -f "$control_dir/laplace_geom.control" ]] || {
  echo "pr-db-proof: staged laplace_geom.control missing: $control_dir" >&2
  exit 2
}
[[ -f "$control_dir/laplace_substrate.control" ]] || {
  echo "pr-db-proof: staged laplace_substrate.control missing: $control_dir" >&2
  exit 2
}

build_library_path="$BUILD/extension/laplace_substrate:$BUILD/extension/laplace_geom:$BUILD/engine/core:$BUILD/engine/dynamics:$BUILD/engine/synthesis"
branch_host="$BUILD/extension/laplace_substrate/laplace_substrate"
t0_perfcache="$BUILD/engine/core/perfcache/laplace_t0_perfcache.bin"
highway_perfcache="$BUILD/engine/core/perfcache/laplace_highway_perfcache.bin"
chess_position_perfcache="$BUILD/engine/core/perfcache/laplace_chess_position_perfcache.bin"
for blob in "$t0_perfcache" "$highway_perfcache" "$chess_position_perfcache"; do
  [[ -f "$blob" ]] || {
    echo "pr-db-proof: branch perfcache blob missing: $blob" >&2
    exit 2
  }
done

# Use a private postmaster for branch-native regression. The production host
# intentionally preloads its installed laplace_substrate image, so using that
# postmaster while dynamic_library_path points at a branch build can load two
# different copies of the extension into one process. Besides invalidating the
# proof, that redefines custom GUCs such as laplace_substrate.perfcache_path.
# Preload that exact branch host so fresh backends can call execution functions
# before any host SQL function: the execution module uses the host's cache and
# configuration symbols. This is the same module topology as production, with
# neither production libraries nor production cache files loaded.
# A private socket directory makes concurrent proofs independent; listen_addresses
# is empty, so the arbitrary fixed port never opens a TCP listener or conflicts
# with production.
"$PG_PREFIX/bin/initdb" -D "$pgdata" -A trust -U laplace_admin --no-sync >/dev/null
cat >>"$pgdata/postgresql.conf" <<EOF
listen_addresses = ''
port = 55432
unix_socket_directories = '$socket_dir'
shared_preload_libraries = '$branch_host'
extension_control_path = '$control_root:\$system'
dynamic_library_path = '$build_library_path:\$libdir'
laplace_substrate.perfcache_path = '$t0_perfcache'
laplace_substrate.highway_perfcache_path = '$highway_perfcache'
laplace_substrate.chess_position_perfcache_path = '$chess_position_perfcache'
EOF

"$PG_PREFIX/bin/pg_ctl" -D "$pgdata" -l "$stage/postgresql.log" -w start >/dev/null
export PGHOST="$socket_dir"
export PGPORT=55432
export PGUSER=laplace_admin
export PGDATABASE=postgres
# Keep the branch resolution settings explicit on every client as well. This
# prevents a caller-provided PGOPTIONS from redirecting extension discovery.
export PGOPTIONS="-c extension_control_path=${control_root}:\$system -c dynamic_library_path=${build_library_path}:\$libdir"

validate_private_server
}

validate_private_server() {
actual_data="$("$PG_PREFIX/bin/psql" -XAt -d postgres -v ON_ERROR_STOP=1 -c 'SHOW data_directory')"
[[ "$(realpath "$actual_data")" == "$pgdata" ]] || { echo "private database data directory changed" >&2; return 2; }
for setting in extension_control_path dynamic_library_path laplace_substrate.perfcache_path laplace_substrate.highway_perfcache_path laplace_substrate.chess_position_perfcache_path; do
  case "$setting" in
    extension_control_path) expected_value="$control_root:\$system" ;;
    dynamic_library_path) expected_value="$build_library_path:\$libdir" ;;
    laplace_substrate.perfcache_path) expected_value="$t0_perfcache" ;;
    laplace_substrate.highway_perfcache_path) expected_value="$highway_perfcache" ;;
    laplace_substrate.chess_position_perfcache_path) expected_value="$chess_position_perfcache" ;;
  esac
  actual_value="$("$PG_PREFIX/bin/psql" -XAt -d postgres -v ON_ERROR_STOP=1 -c "SHOW $setting")"
  [[ "$actual_value" == "$expected_value" ]] || { echo "private database setting changed: $setting" >&2; return 2; }
done

# Fail before pg_regress if the isolated server cannot discover the staged branch
# extensions through the same settings the regress clients inherit.
available="$($PG_PREFIX/bin/psql -X -A -t -d postgres -v ON_ERROR_STOP=1 -c \
  "SELECT string_agg(name, ',' ORDER BY name) FROM pg_available_extensions WHERE name IN ('laplace_geom','laplace_substrate');")"
if [[ "$available" != "laplace_geom,laplace_substrate" ]]; then
  echo "pr-db-proof: staged extensions are not discoverable (found: ${available:-<none>})" >&2
  "$PG_PREFIX/bin/psql" -X -d postgres -v ON_ERROR_STOP=1 -c "SHOW extension_control_path" >&2 || true
  exit 2
fi

# Prove the exact branch host, not the installed image, owns every backend.
preload="$($PG_PREFIX/bin/psql -X -A -t -d postgres -v ON_ERROR_STOP=1 -c \
  "SHOW shared_preload_libraries;")"
if [[ "$preload" != "$branch_host" ]]; then
  echo "pr-db-proof: isolated postmaster host mismatch: expected $branch_host, found $preload" >&2
  exit 2
fi

}

prove_native_database() {
# Every SQL regression executes CREATE EXTENSION against staged branch control/SQL
# and loads branch-native modules through dynamic_library_path. Preserve the
# pg_regress diffs in the job log on failure so a red gate names the actual SQL or
# native defect rather than collapsing back into an opaque CI failure.
# Verify the built CTest command, not merely the source registration: a cached
# BUILD_TESTING=OFF or missing pg_regress must not silently omit this acceptance.
mkdir -p "$BUILD/test-results"
native_selection="$BUILD/test-results/private-native-selection.json"
cmake_tool ctest -- --test-dir "$BUILD" --show-only=json-v1 -L regress > "$native_selection"
python3 - "$native_selection" <<'PY_NATIVE_SELECTION'
import json
import sys

selection = json.load(open(sys.argv[1], encoding="utf-8"))
suites = [test for test in selection.get("tests", [])
          if test.get("name") == "regress_laplace_substrate"]
if len(suites) != 1:
    raise SystemExit("private database proof requires the built substrate regression suite")
if any(prop.get("name") == "DISABLED" and prop.get("value")
       for prop in suites[0].get("properties", [])):
    raise SystemExit("private database proof cannot skip a disabled substrate regression suite")
command = suites[0].get("command", [])
required = ("physicality_descriptor_admission", "physicality_readback", "physicality_readback_cold")
if any(command.count(fixture) != 1 for fixture in required):
    raise SystemExit("private database proof is missing exact physicality admission/readback fixtures")
if command.index("physicality_readback") >= command.index("physicality_readback_cold"):
    raise SystemExit("private database proof must deposit retained forms before the cold-backend fixture")
PY_NATIVE_SELECTION
set +e
cmake_tool ctest -- --test-dir "$BUILD" --output-on-failure --no-tests=error -L regress
ctest_rc=$?
set -e

if (( ctest_rc != 0 )); then
  python3 scripts/capture-native-regression.py --repo-root "$ROOT" --build-root "$BUILD" \
    --output-dir "${LAPLACE_NATIVE_REGRESSION_EVIDENCE_DIRECTORY:-/build/laplace/work/native-regression-evidence/${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-0}-pr}/current" \
    --label native-db --print-diffs || true
  exit "$ctest_rc"
fi

}

prove_operational_database() {
# Exercise source admission, native execution and stable session Projection writes
# while the private branch postmaster is still alive. This DB-tier acceptance is
# excluded from the later managed DEV profile, so its exact selection must run
# here. A fresh TRX receipt prevents a missing or skipped test from passing.
bash scripts/sync-managed-native-artifacts.sh
exemplar_results="$BUILD/test-results/operational-exemplar"
managed_results="$exemplar_results"
mkdir -p "$exemplar_results"
rm -f "$exemplar_results/exemplar.json" "$exemplar_results/execution.json" "$exemplar_results/bundle.json" \
  "$exemplar_results/antonym-exemplar.json" "$exemplar_results/antonym-execution.json" \
  "$managed_results/operational-source-execution.trx"
PATH="$PG_PREFIX/bin:$PATH" \
LAPLACE_DB="Host=$socket_dir;Port=$PGPORT;Username=$PGUSER;Database=laplace_substratecrud_test" \
LAPLACE_PERFCACHE_BIN="$t0_perfcache" \
LAPLACE_OPERATIONAL_EXEMPLAR_RECEIPT="$exemplar_results/exemplar.json" \
LAPLACE_CHESS_OBSERVATION_TEST_DIRECTORY="$exemplar_results/chess-position-observation" \
LD_LIBRARY_PATH="$BUILD/engine/core:$BUILD/engine/dynamics:$BUILD/engine/synthesis:${LD_LIBRARY_PATH:-}" \
  dotnet test app/Laplace.Substrate.Tests/Laplace.Substrate.Tests.csproj \
    -c Release --no-build --nologo --verbosity minimal \
    --filter 'FullyQualifiedName=Laplace.SubstrateCRUD.Tests.OperationalSourceExecutionTests.AuthoredTaskSource_ExecutesNovelRequestAfterSharedAdmissionAndFold|FullyQualifiedName=Laplace.SubstrateCRUD.Tests.OperationalSourceExecutionTests.AuthoredTaskSource_BindsSynsetThroughTwoWitnessedNamingHops|FullyQualifiedName=Laplace.SubstrateCRUD.Tests.OperationalSourceExecutionTests.AuthoredAntonymExemplar_AdmitsCompleteSourceWithNativeParseProvenance|FullyQualifiedName=Laplace.SubstrateCRUD.Tests.OperationalSourceExecutionTests.AuthoredAntonymTask_ExecutesNovelRequestThroughAdmittedWordBinding|FullyQualifiedName=Laplace.SubstrateCRUD.Tests.NativeSqlBatchTests.ConversationWriterResumesProjectionWithoutForgingContent|FullyQualifiedName=Laplace.SubstrateCRUD.Tests.NativeSqlBatchTests.LegacySessionContentIsPreservedAndRequiresExplicitRecovery|FullyQualifiedName=Laplace.SubstrateCRUD.Tests.ChessPositionPlayingPersistenceTests.CompleteDistinctPlayingsFoldOnceAndExactReplayPreservesEvidenceAndStanding|FullyQualifiedName=Laplace.SubstrateCRUD.Tests.NativeSqlBatchTests.WitnessScopesExcludeCrossProductsButRetainConflictingObjects|FullyQualifiedName=Laplace.SubstrateCRUD.Tests.PhysicalityObservationWriterTests.OrdinaryWriterRetainsBothRawFormsAndReusesDurableDescriptorViewEvidence|FullyQualifiedName=Laplace.SubstrateCRUD.Tests.PhysicalityObservationWriterTests.SupplementalRawRowsCannotExcludeSelectedBodiesOrDuplicateTheirWitness|FullyQualifiedName=Laplace.SubstrateCRUD.Tests.PhysicalityObservationWriterTests.ConsensusFoldsGeneratedEvidenceOncePerDistinctActualSourceUnit|FullyQualifiedName=Laplace.SubstrateCRUD.Tests.PhysicalityObservationWriterTests.SourceOnlyJournalBackfillRequiresFreshVerificationAndAtomicGeneratedEvidence|FullyQualifiedName=Laplace.SubstrateCRUD.Tests.PhysicalityObservationWriterTests.SourceOnlyConversationBackfillDoesNotAppendTheOriginalTurnAgain|FullyQualifiedName=Laplace.SubstrateCRUD.Tests.PhysicalityObservationWriterTests.InvalidRawMetadataIsRejectedBeforeOpeningTheDatabase|FullyQualifiedName=Laplace.SubstrateCRUD.Tests.SessionPhysicalityObservationTests.ExistingTurnAppendRetainsOldAndNewFormsAndWriterReplayDoesNotAppendAgain|FullyQualifiedName=Laplace.SubstrateCRUD.Tests.SessionPhysicalityObservationTests.NativeSessionRollbackRetainsOriginalProjectionEvidenceAndFold|FullyQualifiedName=Laplace.SubstrateCRUD.Tests.SessionPhysicalityObservationTests.WaitingReadCommittedAppenderReadsTheBodyCommittedAfterItsStatementStarted|FullyQualifiedName=Laplace.SubstrateCRUD.Tests.PhysicalityObservationWriterTests.MissingCarrierRetainsDescriptorAndLaterContentCompletesOnlyItsView' \
    --logger 'trx;LogFileName=operational-source-execution.trx' \
    --results-directory "$managed_results"
python3 - "$managed_results/operational-source-execution.trx" <<'PY'
from collections import Counter
import re
import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
counters = root.find("{*}ResultSummary/{*}Counters")
expected = {"total": "22", "executed": "22", "passed": "22", "failed": "0", "notExecuted": "0"}
if counters is None or any(counters.get(key) != value for key, value in expected.items()):
    raise SystemExit("private database proof did not execute and pass all 22 required acceptance cases")
prefix = "Laplace.SubstrateCRUD.Tests."
expected_names = Counter([
    prefix + "OperationalSourceExecutionTests.AuthoredTaskSource_ExecutesNovelRequestAfterSharedAdmissionAndFold",
    prefix + "OperationalSourceExecutionTests.AuthoredTaskSource_BindsSynsetThroughTwoWitnessedNamingHops",
    prefix + "OperationalSourceExecutionTests.AuthoredAntonymExemplar_AdmitsCompleteSourceWithNativeParseProvenance",
    prefix + "OperationalSourceExecutionTests.AuthoredAntonymTask_ExecutesNovelRequestThroughAdmittedWordBinding",
    prefix + "NativeSqlBatchTests.ConversationWriterResumesProjectionWithoutForgingContent(batchPrefix: false)",
    prefix + "NativeSqlBatchTests.ConversationWriterResumesProjectionWithoutForgingContent(batchPrefix: true)",
    prefix + "NativeSqlBatchTests.LegacySessionContentIsPreservedAndRequiresExplicitRecovery",
    prefix + "ChessPositionPlayingPersistenceTests.CompleteDistinctPlayingsFoldOnceAndExactReplayPreservesEvidenceAndStanding",
    prefix + "NativeSqlBatchTests.WitnessScopesExcludeCrossProductsButRetainConflictingObjects",
    prefix + "PhysicalityObservationWriterTests.OrdinaryWriterRetainsBothRawFormsAndReusesDurableDescriptorViewEvidence",
    prefix + "PhysicalityObservationWriterTests.SupplementalRawRowsCannotExcludeSelectedBodiesOrDuplicateTheirWitness(variant: 0, transportedForms: 1, expectedWitnesses: 1)",
    prefix + "PhysicalityObservationWriterTests.SupplementalRawRowsCannotExcludeSelectedBodiesOrDuplicateTheirWitness(variant: 1, transportedForms: 2, expectedWitnesses: 2)",
    prefix + "PhysicalityObservationWriterTests.SupplementalRawRowsCannotExcludeSelectedBodiesOrDuplicateTheirWitness(variant: 2, transportedForms: 3, expectedWitnesses: 2)",
    prefix + "PhysicalityObservationWriterTests.ConsensusFoldsGeneratedEvidenceOncePerDistinctActualSourceUnit",
    prefix + "PhysicalityObservationWriterTests.SourceOnlyJournalBackfillRequiresFreshVerificationAndAtomicGeneratedEvidence",
    prefix + "PhysicalityObservationWriterTests.SourceOnlyConversationBackfillDoesNotAppendTheOriginalTurnAgain",
    prefix + "PhysicalityObservationWriterTests.InvalidRawMetadataIsRejectedBeforeOpeningTheDatabase(partialTrajectory: false)",
    prefix + "PhysicalityObservationWriterTests.InvalidRawMetadataIsRejectedBeforeOpeningTheDatabase(partialTrajectory: true)",
    prefix + "SessionPhysicalityObservationTests.ExistingTurnAppendRetainsOldAndNewFormsAndWriterReplayDoesNotAppendAgain",
    prefix + "SessionPhysicalityObservationTests.NativeSessionRollbackRetainsOriginalProjectionEvidenceAndFold",
    prefix + "SessionPhysicalityObservationTests.WaitingReadCommittedAppenderReadsTheBodyCommittedAfterItsStatementStarted",
    prefix + "PhysicalityObservationWriterTests.MissingCarrierRetainsDescriptorAndLaterContentCompletesOnlyItsView",
])
results = root.findall("{*}Results/{*}UnitTestResult")
# xUnit adapters render Boolean argument values with either .NET or C# casing.
# Only that spelling may vary; both distinct theory rows must execute once.
names = Counter(re.sub(r"(?<=: )(True|False)(?=\))",
                       lambda match: match.group(0).lower(), result.get("testName", ""))
                for result in results)
if names != expected_names or any(result.get("outcome") != "Passed" for result in results):
    raise SystemExit("private database proof is missing an exact passing source/session/physicality/chess acceptance case")
print("OPERATIONAL_SOURCE_EXECUTION_OK selected=3 executed=3 passed=3 skipped=0 postgres=isolated")
print("OPERATIONAL_EXEMPLAR_ADMISSION_OK selected=1 executed=1 passed=1 skipped=0 postgres=isolated")
print("SESSION_PROJECTION_EXECUTION_OK selected=3 executed=3 passed=3 skipped=0 postgres=isolated")
print("CHESS_PLAYING_OBSERVATION_OK selected=1 executed=1 passed=1 skipped=0 postgres=isolated")
print("CHESS_WITNESS_SCOPE_OK selected=1 executed=1 passed=1 skipped=0 postgres=isolated")
print("PHYSICALITY_WRITER_EXECUTION_OK selected=10 executed=10 passed=10 skipped=0 postgres=isolated")
print("SESSION_PHYSICALITY_EXECUTION_OK selected=3 executed=3 passed=3 skipped=0 postgres=isolated")
PY

# The endpoint database tier is also excluded from the later managed profile.
# Exercise real identity and billing stores against the canonical migration
# chain and branch-owned SQL catalog in this exact private postmaster.
identity_database="${REGRESS_DB}_identity"
identity_receipt="$managed_results/browser-identity.trx"
rm -f -- "$identity_receipt"
"$PG_PREFIX/bin/createdb" "$identity_database"
"$PG_PREFIX/bin/psql" -X -v ON_ERROR_STOP=1 -d "$identity_database" \
  -f "$ROOT/db/migrations/20260611000000_app_billing.sql" \
  -f "$ROOT/db/migrations/20260722000000_app_billing_identity.sql" \
  -f "$ROOT/db/migrations/20260807020000_app_consume_credit.sql" \
  -f "$ROOT/db/migrations/20260915000000_app_identity_sessions.sql" \
  -f "$ROOT/db/migrations/20260916000000_app_workspace_invitations.sql" \
  -f "$ROOT/db/migrations/20260916000100_app_subscription_sync.sql" \
  -f "$ROOT/db/migrations/20260916000200_browser_ticket_identity_binding.sql" >/dev/null
LAPLACE_DB="Host=$socket_dir;Port=$PGPORT;Username=$PGUSER;Database=$identity_database" \
LAPLACE_PERFCACHE_BIN="$t0_perfcache" \
LD_LIBRARY_PATH="$BUILD/engine/core:$BUILD/engine/dynamics:$BUILD/engine/synthesis:${LD_LIBRARY_PATH:-}" \
  dotnet test app/Laplace.Endpoints.OpenAICompat.Tests/Laplace.Endpoints.OpenAICompat.Tests.csproj \
    -c Release --no-build --nologo --verbosity minimal \
    --filter 'FullyQualifiedName=Laplace.Endpoints.OpenAICompat.Tests.BrowserIdentityTests.PostgresIdentityStorePersistsAccountSessionAndConversation' \
    --logger 'trx;LogFileName=browser-identity.trx' \
    --results-directory "$managed_results"
python3 - "$identity_receipt" <<'PY_IDENTITY'
import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
counters = root.find("{*}ResultSummary/{*}Counters")
expected = {"total": "1", "executed": "1", "passed": "1", "failed": "0", "notExecuted": "0"}
results = root.findall("{*}Results/{*}UnitTestResult")
name = "Laplace.Endpoints.OpenAICompat.Tests.BrowserIdentityTests.PostgresIdentityStorePersistsAccountSessionAndConversation"
if (counters is None or any(counters.get(key) != value for key, value in expected.items())
        or len(results) != 1 or results[0].get("testName") != name
        or results[0].get("outcome") != "Passed"):
    raise SystemExit("private database proof did not execute and pass the exact identity store acceptance case")
print("BROWSER_IDENTITY_EXECUTION_OK selected=1 executed=1 passed=1 skipped=0 postgres=isolated")
PY_IDENTITY

# Run every durable store contract against the same freshly migrated private
# database. The exact TRX inventory makes an unavailable/skipped store a failure.
billing_receipt="$managed_results/billing-stores.trx"
rm -f -- "$billing_receipt"
LAPLACE_DB="Host=$socket_dir;Port=$PGPORT;Username=$PGUSER;Database=$identity_database" \
LAPLACE_PERFCACHE_BIN="$t0_perfcache" \
LD_LIBRARY_PATH="$BUILD/engine/core:$BUILD/engine/dynamics:$BUILD/engine/synthesis:${LD_LIBRARY_PATH:-}" \
  dotnet test app/Laplace.Endpoints.OpenAICompat.Tests/Laplace.Endpoints.OpenAICompat.Tests.csproj \
    -c Release --no-build --nologo --verbosity minimal \
    --filter 'FullyQualifiedName~Laplace.Endpoints.OpenAICompat.Tests.PostgresBillingStoreContractTests.' \
    --logger 'trx;LogFileName=billing-stores.trx' \
    --results-directory "$managed_results"
python3 - "$billing_receipt" <<'PY_BILLING'
import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
counters = root.find("{*}ResultSummary/{*}Counters")
methods = {
    "QuoteStore_PutGetUpdate_RoundTrips",
    "Ledger_RecordsAndReadsNewestFirst",
    "Entitlements_ActivateConsumeExhaustDeactivate",
    "Entitlements_RenewResetsUsedCredits",
    "WebhookEvents_DuplicateBeginIsRejected",
    "PriceMap_SetOverwritesAndGets",
    "ApiKeys_PutGetRevokeAndLabelLookup",
    "Config_SetOverwritesAndGets",
}
prefix = "Laplace.Endpoints.OpenAICompat.Tests.PostgresBillingStoreContractTests."
names = {prefix + method for method in methods}
expected = {"total": "8", "executed": "8", "passed": "8", "failed": "0", "notExecuted": "0"}
results = root.findall("{*}Results/{*}UnitTestResult")
if (counters is None or any(counters.get(key) != value for key, value in expected.items())
        or len(results) != len(names)
        or {result.get("testName") for result in results} != names
        or any(result.get("outcome") != "Passed" for result in results)):
    raise SystemExit("private database proof did not execute and pass all eight exact billing store contracts")
print("BILLING_STORE_EXECUTION_OK selected=8 executed=8 passed=8 skipped=0 postgres=isolated")
PY_BILLING
"$PG_PREFIX/bin/dropdb" "$identity_database"

}

prove_highway_recovery() {
# Exercise actual registry unavailability and WAL recovery in this private
# postmaster. The normal public C deposit and SQL batch orchestrators run
# unchanged; the fixture varies only the real registry file and process lifetime.
LAPLACE_PG_PREFIX="$PG_PREFIX" bash scripts/test-highway-registry-recovery.sh \
  "$pgdata" "$highway_perfcache" "${REGRESS_DB}_highway"

}

prove_legacy_repairs() {
# Use the same isolated branch-native postmaster to prove exact legacy row
# repairs, durable pre-mutation receipts, rejected evidence, and SQL rollback.
# The harness creates a unique disposable database and keeps receipts with the
# build's other test results for inspection after the private cluster is gone.
LAPLACE_PG_PREFIX="$PG_PREFIX" python3 scripts/test-legacy-content-repair.py \
  --pgdata "$pgdata" --database-stem "$REGRESS_DB" \
  --receipt-root "$BUILD/test-results/legacy-content-repair"

}

run_private_phase() {
  case "$1" in
    native-db|operational-db|highway-recovery|legacy-repair-db) validate_private_server ;;
  esac
  case "$1" in
    private-db-start) prepare_private_database ;;
    native-db) prove_native_database ;;
    operational-db) prove_operational_database ;;
    highway-recovery) prove_highway_recovery ;;
    legacy-repair-db) prove_legacy_repairs ;;
    private-db-stop) cleanup quiet ;;
    *) echo "unknown private database phase: $1" >&2; return 2 ;;
  esac
}

finish_private_proof() {
  local proof_rc="$1" cleanup_rc=0
  if [[ "$action" == all || "$proof_rc" != 0 ]]; then
    cleanup || cleanup_rc=$?
  fi
  if (( proof_rc == 0 )); then proof_rc="$cleanup_rc"; fi
  exit "$proof_rc"
}

if [[ "$action" == all ]]; then
  [[ ! -e "$state_file" && ! -L "$state_file" ]] || { echo "private database session already exists" >&2; exit 2; }
  trap 'finish_private_proof "$?"' EXIT
  trap 'exit 130' INT
  trap 'exit 143' TERM
  for phase in private-db-start native-db operational-db highway-recovery legacy-repair-db private-db-stop; do
    run_private_phase "$phase"
  done
else
  if [[ "$action" != private-db-start ]]; then
    [[ -f "$state_file" ]] || { echo "private database session is missing" >&2; exit 2; }
    load_state
  else
    [[ ! -e "$state_file" && ! -L "$state_file" ]] || { echo "private database session already exists" >&2; exit 2; }
  fi
  # A successful step leaves the private cluster for the next named phase.
  # Failure or cancellation cleans it before returning the original exit status.
  trap 'finish_private_proof "$?"' EXIT
  trap 'exit 130' INT
  trap 'exit 143' TERM
  run_private_phase "$action"
fi
