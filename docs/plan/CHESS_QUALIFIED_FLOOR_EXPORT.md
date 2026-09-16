# Export recorded floors from the qualified installed main

The opt-in workflow chess-floor-export.yml prepares an authenticated recorded-corpus input for the normal cache build. It does not change ordinary PR proof, install files, restart services, mutate the database, or merge a pull request.

Each invocation selects one complete immutable main commit and tree in **.github/chess-floor-export-selection.json**. That exact source must also be the installed source observed by a successful recorded-corpus pilot. The selected installation must have reached one of the verified terminal outcomes below, and the pilot must have completed successfully. The normal route requires the full lifecycle to pass. A second, narrowly defined route permits only a failed required lexical-foundation phase after every build, test, native-install and database-maintenance prerequisite passed; that lifecycle remains explicitly failed. An explicit native-only installation may also qualify independently of application publication, using the separate native receipt contract below. A successor release uses its own selection and evidence; the operator does not contain a release-specific source pin.

## Source and native compatibility

The driver verifies that the candidate checkout has the selected complete commit and tree, has no local changes, and uses the same source identity as the installed pilot. It checks the live PostgreSQL extensions, migration/function inventory, configured ROMs, installed module closure and absence of active ingests against the successful installed pilot's before/after snapshot. Candidate extension versions and its content-addressed execution-module name must match that installed baseline.

The general application deployment guard is unchanged and is reused in full. Source and installed generation are now identical: before and after export its snapshot owner checks the retained CMake installed form against actual installed native modules, SQL versions, migrations, ROMs, sealed floor pair, build/install fingerprints and idle ingestion. That snapshot must exactly equal the successful pilot baseline. An observed mismatch fails before selection; it does not infer ABI compatibility or install a substitute extension.

The main lifecycle retains its persistent checkout and build. This job rebuilds the managed catalog and focused tests from the exact qualified source into a new /build/laplace/build/chess-floor-export-<run>-<attempt> directory. The receipt explicitly calls those managed bytes rebuilt. It derives the retained native build from the selected main session's actual persistent checkout through the existing build-placement address, verifies the applicable canonical phase outcomes, matching build/install stamps and CMake source binding, and hashes the private session without retaining its token. App-local native libraries must match that selected build before any test or export. Current bytes are freshly hashed and focused-tested; the full installed-form guard binds that build to the pilot's observed installed generation. No direct historical comparison to a native hash saved by the workflow run is claimed.

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

The v1 native receipt did not store a historical checkout path or build fingerprint. Accordingly, the exporter verifies the explicitly observed checkout's current HEAD/tree, tracked cleanliness, actual build symlink, CMake source/cache placement and matching build/install stamps. It labels this as current verification, not historical receipt content. The report retains full_lifecycle_passed=false and managed_publication=not_attempted. These facts do not prevent the independently qualified read-only export.

## Required real identities

After the exact-main lifecycle has reached one of the accepted terminal outcomes and its installed pilot has completed successfully, put their observed identities in **.github/chess-floor-export-selection.json** on a separate reviewed operator branch. That file is deliberately absent from this candidate: no fictional run or successful pilot is supplied.

Required fields are:

| Field | Required value |
| --- | --- |
| candidate_commit | Complete lowercase 40-character commit identity from the selected terminal main lifecycle |
| candidate_tree | Complete lowercase 40-character tree identity for that commit |
| installed_source | Same commit as candidate_commit, authenticated by the installed pilot |
| proof_run_id | Positive integer for the selected exact-main product lifecycle run |
| proof_run_attempt | Positive integer for that completed attempt |
| pilot_evidence_directory | Absolute permanent directory containing the completed corpus wrapper receipt and native snapshots |
| pilot_receipt_sha256 | Observed SHA256 of that directory's receipt.json |
| pilot_native_snapshot_sha256 | Observed SHA256 of its equal native-before.json and native-after.json |

The remote run must be a completed laplace.yml run on main with exactly the selected commit and attempt. Current installations use an explicit workflow_dispatch with stage=all; ordinary pushes run development tests only. Historical full push lifecycles remain eligible only when their complete retained phase evidence satisfies the same requirements. Its private RUN-ATTEMPT-product/session.json must show kind=product, stage=all, no active phase and cleanup_exit_code=0. The full activation phase list is resolved with GITHUB_EVENT_NAME=workflow_dispatch and default foundation/generation flags, independently of the operator invocation. The actual remote event is authenticated and retained separately as lifecycle_event; this phase-list calculation does not relabel a push as a dispatch. The two accepted outcomes are:

- Full success: the remote conclusion is success, the private session is stopped, and every canonical product phase completed in order with zero exit status. This existing route is unchanged.
- Lexical-only failure: the remote conclusion and private session remain failure/failed. The exact ordered prefix policy, dependencies, build, native-dev, managed-dev, uci-dev, browser-dev, native-install and database-maintenance must have succeeded, followed by one nonzero lexical-foundation result and no later executed phase. The remote attempt must contain exactly one matching completed failed job. Its installed-prerequisite steps and host-reservation release must have succeeded in order, required lexical foundation must be its sole failed step, and every later ordinary delivery step must be explicitly skipped.

The receipt preserves the actual lifecycle conclusion, full_lifecycle_passed flag, terminal session status, cleanup result, exact phase results and, for the failed route, the failed exit code, unexecuted phase suffix and observed workflow step outcomes. ci-session.py normally leaves a cleaned failed session marked failed; a subsequent stop command does not relabel it stopped. A cancelled run, another failed phase, missing or reordered prerequisites, incomplete cleanup, an active phase, another source, another attempt or ambiguous job is refused. The private session's token is never copied or printed; its SHA256 is rechecked at completion.

This exception covers only a verified chess export and future-build selection. Lexical admission, application publication and skipped live/generation checks remain failed or unexecuted as observed. They are not dependencies of the read-only typed chess exporter. The installed native/SQL/migration/ROM checks, completed fresh pilot with exact zero-writer replay, focused export controls, complete artifact checks and before/after identities remain mandatory. The application runtime guard still refuses active/unresolved ingest journals or a changed database/runtime.

The workflow's operator driver and its protocol tests execute from the reviewed operator checkout. The catalog, native/runtime owners and canonical phase list still come from the selected immutable candidate source. The workflow source and candidate source are retained separately; an exporter-only correction does not pretend to be a new installed native generation.

Publishing the reviewed operator branch under verify/chess-floor-export-* starts the single job. This is a separate explicit host reservation; it must be scheduled after the lifecycle and pilot, with the other repository's host owner informed. The job holds the ordinary /build/laplace/work/host-resource.lock across source selection, managed rebuild/tests, read-only export, validation and selection. flock --no-fork and the exec chain keep its descriptor in the driver process through owned child cleanup and final receipt writing on cancellation. The new permanent candidate worktree is retained as source evidence.

The wrapper loads the ordinary installed chess configuration in memory and requires the existing exact database resolver: /var/run/postgresql:5432, database laplace, role laplace_admin. Credentials and captured toolchain environment are not retained.

## Execution and evidence

The fresh durable output is /build/laplace/corpus-exports/chess/<run>-<attempt>. Its full parent chain must be on the real build mount with inherited laplace-runner group ownership, setgid and write access; stale malformed parents fail before export. The existing install prefix's etc directory must allow the ordinary selection writer. The driver refuses existing outputs or conflicting explicit export overrides; it does not repair permissions or overwrite a prior export.

The direct command, under the same host ownership and prepared environment, is:

    python3 scripts/export-qualified-chess-floors.py --selection /absolute/reviewed-selection.json --candidate-root /build/laplace/worktrees/exact-cache-source --output /build/laplace/corpus-exports/chess/new-unique-child

Twenty-eight local protocol and filesystem/process controls first check exact-main phase/source/build selection, the sole lexical-failure exception and its refusal cases, explicit dispatch qualification under a push operator and refusal of development-only push receipts, successor release evidence binding, immutable source selection, and post-publication failure, absent/different selection, failed readback, and SIGTERM lock ownership through child cleanup/finalization. They use explicitly synthetic publisher framing and do not claim corpus/native acceptance. It then builds ChessCatalogSurfaces, Laplace.Core.Tests and Laplace.Chess.Tests, then runs the existing floor serialization/checksum/lifetime, recorded export, recorded witness and starting-side inventory classes. The retained TRX must contain every selected class, at least one result, and no failed or skipped result. The published catalog and all three app-local native libraries are hashed before and after.

The actual database read is the existing command:

    dotnet <rebuilt-catalog>/ChessCatalogSurfaces.dll export-recorded-floors --output-dir <durable-output>/export --page-size 16 --maximum-materialized-mib 128 --maximum-retained-mib 4096 --deadline-seconds 3600 --transition-buffer-records 65536 --merge-fan-in 32 --maximum-spill-mib 4096 --maximum-export-mib 4096

These are finite resource bounds, not truncation rules. Every selected complete playing must finish strict canonical hydration and legal full-line replay. If any selected line is unresolved or a bound is exceeded, the existing exporter reports partial/failure and nothing is selected. No move ceiling or synthetic game replacement is introduced.

The existing artifact owner validates the completed receipt, database identity, full inventory disposition, witnessed-input checksum and all four files. The selected native hash owner verifies the entire transition v1 framing, checksum, ordered unique keys and exact count. A successful export must contain recorded transitions. The driver repeats installed runtime/database and managed/native identity checks before the existing select-export owner atomically writes /opt/laplace/etc/chess-corpus-export.json.

The exported files stay on durable storage. The workflow artifact contains logs, exact closure hashes, selected source/proof/pilot identities, focused TRX results, export/inventory summary and selection receipt. It does not upload multi-gigabyte corpus or managed outputs. This proves a complete observed read interval; it does not claim a transaction-wide snapshot, coverage of historically unrecorded games, throughput, or an installed serving cache.

## Completion after selection

A successful selection is the input to the ordinary main build. A subsequent ordinary build/install/publish at this same main source reauthenticates the export, includes it in the native fingerprint, regenerates seed coverage, merges recorded transitions, produces and seals the position/transition pair, and atomically installs the pair through the existing owner. Main activation and the existing verify-chess-floor-serving.py observer must then prove the running API maps the installed generation and records a persistent transition lookup. The export job itself performs none of those serving actions.

On any failure, retain its exact receipt and phase logs. A partial export is not selected. Every attempted publication reconciles the actual persisted selection, including after subprocess failure or cancellation. A matching validated export records selection_completed=true even when the operation failed. An absent/different observed selection records false; a failed or interrupted readback records null with selection_observation=unknown. The original failure remains primary; no rollback is assumed.
