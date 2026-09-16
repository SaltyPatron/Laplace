# Export recorded floors from the qualified installed source

The opt-in workflow chess-floor-export.yml prepares an authenticated recorded-corpus input for the normal cache build. It does not install files, restart services, mutate the database, or merge a pull request.

Each invocation selects a complete immutable commit and tree in **.github/chess-floor-export-selection.json**. That source must be the installed source observed by a successful recorded-corpus pilot. Qualification accepts the retained successful exact-main lifecycle or the independent native-only installation described below. Failed lexical-foundation lifecycles are not an accepted route. A successor release uses its own selection and evidence.

## Source and native compatibility

The candidate checkout must have the selected complete commit and tree and no local changes. The installed-form runtime guard checks retained CMake installed bytes, live PostgreSQL extensions, migration/function inventory, configured ROMs, installed module closure, sealed chess floor pair and actual build/install fingerprints. Candidate extension versions and its content-addressed execution-module name must match the installed pilot.

Export and pilot comparisons use the guard's explicit recording purpose. Every runtime/database identity remains equal; only the observed nonnegative integer running_ingests count may change. Each original count remains in its snapshot. A journal row may describe an interrupted unrelated ingest and is not an active writer lock. This does not remove the ordinary writer's transaction locking or change the stricter publication guard. A publication-purpose or unlabelled historical snapshot cannot substitute for a recording snapshot.

The job rebuilds the managed catalog and focused tests from the exact qualified source into a new /build/laplace/build/chess-floor-export-<run>-<attempt> directory. The receipt labels those managed bytes rebuilt. It follows the selected checkout's actual build symlink and checks CMake source/cache placement plus matching build/install stamps. App-local native libraries must match that selected build before tests or export. Current bytes are freshly hashed and tested; no missing historical native hash is inferred.

## Independent native-only installation

Set proof_kind to native-only-install only for the reviewed runner-owned PostgreSQL18.6/native installation operator. This is distinct from the main lifecycle and has no ci-session receipt. The selected product source remains candidate_commit/candidate_tree/installed_source; proof_operator_commit identifies the separate workflow source. The completed native workflow does not establish application publication, database recreation or foundation ingestion.

In addition to the common pilot/source fields below, supply these observed values:

| Field | Required value |
| --- | --- |
| proof_operator_commit | Exact immutable commit of the completed native operator |
| native_workflow_blob | Reviewed Git blob of .github/workflows/chess-floor-serving-controls.yml at that operator commit |
| native_checkout | Absolute permanent checkout actually used by the native operator, verified again now |
| native_receipt_sha256 | SHA256 of the authenticated artifact's receipt.json and matching retained private file |
| native_selection_sha256 | SHA256 of the authenticated artifact's selection.json and matching retained private file |
| native_artifact_id | Positive GitHub artifact ID for original-native-install-RUN-ATTEMPT |
| native_artifact_sha256 | SHA256 of that exact authenticated artifact ZIP |

The existing dispatcher authenticates the artifact archive and emits its document identities; copy those observed hashes into the selection. The export driver authenticates remote artifact metadata and the separately selected private receipt hashes. It does not download the ZIP again or claim that a metadata-only check re-hashed archive contents.

The remote run must be the successful push of the selected operator to verify/chess-floor-serving-controls-20260916, with the exact workflow, run and attempt. Its one completed job must contain the actual native execution, outcome and evidence steps in order. Under /build/laplace/recovery/native-install/RUN-ATTEMPT, private runner-owned selection.json, receipt.json, workflow-outcome.json and completed-phases.txt must agree on the exact source, operator, release and outcome. The completed phase sequence must include the PostgreSQL pin/source/build checks, native/managed build, private PostgreSQL test, native/managed/UCI tests, quiet-state check, native install, extension SQL, database health and PostgreSQL activation. Only the actual optional pg-fetch phase is optional.

The receipt must retain server180006 and the exact installed core/postgres/pg_config file identities. Those actual files are hashed now and rechecked around the export, and the live server must still be180006. All common pilot/database/SQL/native closure checks below remain required.

Early v1 receipts did not store a historical checkout path or build fingerprint. The exporter always verifies the explicitly observed checkout’s current HEAD/tree, tracked cleanliness, actual build symlink, CMake source/cache placement and matching build/install stamps. When the receipt includes checkout, buildDirectory, buildNativeStamp and installNativeStamp, those fields must match that observation; a selection checkout field is also checked. Missing historical fields are not invented. The report retains full_lifecycle_passed=false and managed_publication=not_attempted. These facts do not prevent the independently qualified read-only export.

## Required real identities

Put the selected proof and completed pilot identities in **.github/chess-floor-export-selection.json** on the existing reviewed operator branch. No successful run, artifact or pilot identity is supplied by the source change.

| Field | Required value |
| --- | --- |
| candidate_commit | Complete lowercase 40-character installed product commit |
| candidate_tree | Complete lowercase 40-character tree for that commit |
| installed_source | Same commit as candidate_commit, authenticated by the pilot |
| proof_kind | native-only-install for the independent route; absent or main-lifecycle for the existing main route |
| proof_run_id | Positive integer for the selected proof run |
| proof_run_attempt | Positive integer for that completed attempt |
| pilot_evidence_directory | Absolute permanent completed corpus receipt and native snapshot directory |
| pilot_receipt_sha256 | Observed SHA256 of receipt.json |
| pilot_native_snapshot_sha256 | Observed SHA256 of native-before.json; native-after.json must be recording-compatible |

The existing exact-main route requires a successful completed laplace.yml push to main at the selected commit and attempt. Its private RUN-ATTEMPT-product/session.json must show kind=product, stage=all, no active phase, cleanup_exit_code=0 and every canonical full product phase completed in order. A development-only push does not establish installation. A failed, cancelled, incomplete, wrong-source or reordered lifecycle cannot qualify. The private session token is never retained; its receipt SHA256 is checked again before selection. The independent native-only route proves its own narrower installation scope and never claims application publication or full lifecycle success.

The workflow's operator driver and its protocol tests execute from the reviewed operator checkout. The catalog, native/runtime owners and canonical phase list still come from the selected immutable candidate source. The workflow source and candidate source are retained separately; an exporter-only correction does not pretend to be a new installed native generation.

Publishing the reviewed operator branch under verify/chess-floor-export-* starts the single job. This is a separate explicit host reservation; it must be scheduled after the lifecycle and pilot, with the other repository's host owner informed. The job holds the ordinary /build/laplace/work/host-resource.lock across source selection, managed rebuild/tests, read-only export, validation and selection. flock --no-fork and the exec chain keep its descriptor in the driver process through owned child cleanup and final receipt writing on cancellation. The new permanent candidate worktree is retained as source evidence.

The wrapper loads the ordinary installed chess configuration in memory and requires the existing exact database resolver: /var/run/postgresql:5432, database laplace, role laplace_admin. Credentials and captured toolchain environment are not retained.

## Execution and evidence

The fresh durable output is /build/laplace/corpus-exports/chess/<run>-<attempt>. Its full parent chain must be on the real build mount with inherited laplace-runner group ownership, setgid and write access; stale malformed parents fail before export. The existing install prefix's etc directory must allow the ordinary selection writer. The driver refuses existing outputs or conflicting explicit export overrides; it does not repair permissions or overwrite a prior export.

The direct command, under the same host ownership and prepared environment, is:

    python3 scripts/export-qualified-chess-floors.py --selection /absolute/reviewed-selection.json --candidate-root /build/laplace/worktrees/exact-cache-source --output /build/laplace/corpus-exports/chess/new-unique-child

Twenty-three local protocol and filesystem/process controls check exact-main and independent native-install source/build selection, receipt/artifact/phase identity, recorded placement, recording-purpose runtime comparisons, publication failure reconciliation and actual SIGTERM lock ownership through child cleanup. They use explicitly synthetic publisher framing and do not claim corpus/native acceptance. It then builds ChessCatalogSurfaces, Laplace.Core.Tests and Laplace.Chess.Tests, then runs the existing floor serialization/checksum/lifetime, recorded export, recorded witness and starting-side inventory classes. The retained TRX must contain every selected class, at least one result, and no failed or skipped result. The published catalog and all three app-local native libraries are hashed before and after.

The actual database read is the existing command:

    dotnet <rebuilt-catalog>/ChessCatalogSurfaces.dll export-recorded-floors --output-dir <durable-output>/export --page-size 16 --maximum-materialized-mib 128 --maximum-retained-mib 4096 --deadline-seconds 3600 --transition-buffer-records 65536 --merge-fan-in 32 --maximum-spill-mib 4096 --maximum-export-mib 4096

These are finite resource bounds, not truncation rules. Every selected complete playing must finish strict canonical hydration and legal full-line replay. If any selected line is unresolved or a bound is exceeded, the existing exporter reports partial/failure and nothing is selected. No move ceiling or synthetic game replacement is introduced.

The existing artifact owner validates the completed receipt, database identity, full inventory disposition, witnessed-input checksum and all four files. The selected native hash owner verifies the entire transition v1 framing, checksum, ordered unique keys and exact count. A successful export must contain recorded transitions. The driver repeats installed runtime/database and managed/native identity checks before the existing select-export owner atomically writes /opt/laplace/etc/chess-corpus-export.json.

The exported files stay on durable storage. The workflow artifact contains logs, exact closure hashes, selected source/proof/pilot identities, focused TRX results, export/inventory summary and selection receipt. It does not upload multi-gigabyte corpus or managed outputs. This proves a complete observed read interval; it does not claim a transaction-wide snapshot, coverage of historically unrecorded games, throughput, or an installed serving cache.

## Completion after selection

A successful selection is the input to the ordinary main build. A subsequent ordinary build/install/publish at this same main source reauthenticates the export, includes it in the native fingerprint, regenerates seed coverage, merges recorded transitions, produces and seals the position/transition pair, and atomically installs the pair through the existing owner. Main activation and the existing verify-chess-floor-serving.py observer must then prove the running API maps the installed generation and records a persistent transition lookup. The export job itself performs none of those serving actions.

On any failure, retain its exact receipt and phase logs. A partial export is not selected. Every attempted publication reconciles the actual persisted selection, including after subprocess failure or cancellation. A matching validated export records selection_completed=true even when the operation failed. An absent/different observed selection records false; a failed or interrupted readback records null with selection_observation=unknown. The original failure remains primary; no rollback is assumed.
