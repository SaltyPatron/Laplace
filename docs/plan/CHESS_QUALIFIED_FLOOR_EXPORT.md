# Export recorded floors from the qualified installed main

The opt-in workflow chess-floor-export.yml prepares an authenticated recorded-corpus input for the normal cache build. It does not change ordinary PR proof, install files, restart services, mutate the database, or merge a pull request.

This operator is bound to merged main **4fa186e38de10ee5cc768d38f33ec9663cf2e314**, tree **8e5cde196b33dc2dbbc7a0d1643b26dd3b45f595**, as both the qualified source and installed pilot source. Its preceding pull-request proof was cancelled and supplies no successful qualification. The ordinary exact-main product lifecycle and subsequent installed pilot must actually complete before this operator can run. A different source or installed baseline requires an explicit reviewed update.

## Source and native compatibility

The driver verifies that the extension, migrations, substrate read implementation, native core source and headers, canonical hash owner, SQL catalog interface and chess composition/replay owners are unchanged between those revisions. It also checks the live PostgreSQL extensions, migration/function inventory, configured ROMs, installed module closure and absence of active ingests against the successful installed pilot's before/after snapshot. Candidate extension versions and its content-addressed execution-module name must match that installed baseline.

The general application deployment guard is unchanged and is reused in full. Source and installed generation are now identical: before and after export its snapshot owner checks the retained CMake installed form against actual installed native modules, SQL versions, migrations, ROMs, sealed floor pair, build/install fingerprints and idle ingestion. That snapshot must exactly equal the successful pilot baseline. An observed mismatch fails before selection; it does not infer ABI compatibility or install a substitute extension.

The main lifecycle retains its persistent checkout and build. This job rebuilds the managed catalog and focused tests from the exact qualified source into a new /build/laplace/build/chess-floor-export-<run>-<attempt> directory. The receipt explicitly calls those managed bytes rebuilt. It derives the retained native build from the successful main session's actual persistent checkout through the existing build-placement address, verifies every canonical product phase, matching build/install stamps and CMake source binding, and hashes the private session without retaining its token. App-local native libraries must match that selected build before any test or export. Current bytes are freshly hashed and focused-tested; the full installed-form guard binds that build to the pilot's observed installed generation. No direct historical comparison to a native hash saved by the workflow run is claimed.

## Required real identities

After the exact-main lifecycle and its installed pilot have completed successfully, put their observed identities in **.github/chess-floor-export-selection.json** on a separate reviewed operator branch. That file is deliberately absent from this candidate: no fictional run or successful pilot is supplied.

Required fields are:

| Field | Required value |
| --- | --- |
| candidate_commit | Exact merged main commit above |
| candidate_tree | Exact merged main tree above |
| installed_source | Exact installed source above |
| proof_run_id | Positive integer for the successful exact-main product lifecycle run |
| proof_run_attempt | Positive integer for that completed attempt |
| pilot_evidence_directory | Absolute permanent directory containing the completed corpus wrapper receipt and native snapshots |
| pilot_receipt_sha256 | Observed SHA256 of that directory's receipt.json |
| pilot_native_snapshot_sha256 | Observed SHA256 of its equal native-before.json and native-after.json |

The remote run must be a completed successful laplace.yml push to main with exactly the pinned commit and attempt. Its private RUN-ATTEMPT-product/session.json must show kind=product, stage=all, stopped cleanup, no active phase, and every phase returned by product-ci.sh all --list-phases in order with zero exit status. The normal main phase list is resolved independently of later export environment flags, so live acceptance cannot be omitted. Cancelled PRs, partial-stage main runs, another source, another attempt and missing or failed phases are refused. The private session's token is never copied or printed; its SHA256 is rechecked at completion.

Publishing the reviewed operator branch under verify/chess-floor-export-* starts the single job. This is a separate explicit host reservation; it must be scheduled after the lifecycle and pilot, with the other repository's host owner informed. The job holds the ordinary /build/laplace/work/host-resource.lock across source selection, managed rebuild/tests, read-only export, validation and selection. flock --no-fork and the exec chain keep its descriptor in the driver process through owned child cleanup and final receipt writing on cancellation. The new permanent candidate worktree is retained as source evidence.

The wrapper loads the ordinary installed chess configuration in memory and requires the existing exact database resolver: /var/run/postgresql:5432, database laplace, role laplace_admin. Credentials and captured toolchain environment are not retained.

## Execution and evidence

The fresh durable output is /build/laplace/corpus-exports/chess/<run>-<attempt>. Its full parent chain must be on the real build mount with inherited laplace-runner group ownership, setgid and write access; stale malformed parents fail before export. The existing install prefix's etc directory must allow the ordinary selection writer. The driver refuses existing outputs or conflicting explicit export overrides; it does not repair permissions or overwrite a prior export.

The direct command, under the same host ownership and prepared environment, is:

    python3 scripts/export-qualified-chess-floors.py --selection /absolute/reviewed-selection.json --candidate-root /build/laplace/worktrees/exact-cache-source --output /build/laplace/corpus-exports/chess/new-unique-child

Eight local protocol and filesystem/process controls first check exact-main phase/source/build selection and post-publication failure, absent/different selection, failed readback, and SIGTERM lock ownership through child cleanup/finalization. They use explicitly synthetic publisher framing and do not claim corpus/native acceptance. It then builds ChessCatalogSurfaces, Laplace.Core.Tests and Laplace.Chess.Tests, then runs the existing floor serialization/checksum/lifetime, recorded export, recorded witness and starting-side inventory classes. The retained TRX must contain every selected class, at least one result, and no failed or skipped result. The published catalog and all three app-local native libraries are hashed before and after.

The actual database read is the existing command:

    dotnet <rebuilt-catalog>/ChessCatalogSurfaces.dll export-recorded-floors --output-dir <durable-output>/export --page-size 16 --maximum-materialized-mib 128 --maximum-retained-mib 4096 --deadline-seconds 3600 --transition-buffer-records 65536 --merge-fan-in 32 --maximum-spill-mib 4096 --maximum-export-mib 4096

These are finite resource bounds, not truncation rules. Every selected complete playing must finish strict canonical hydration and legal full-line replay. If any selected line is unresolved or a bound is exceeded, the existing exporter reports partial/failure and nothing is selected. No move ceiling or synthetic game replacement is introduced.

The existing artifact owner validates the completed receipt, database identity, full inventory disposition, witnessed-input checksum and all four files. The selected native hash owner verifies the entire transition v1 framing, checksum, ordered unique keys and exact count. A successful export must contain recorded transitions. The driver repeats installed runtime/database and managed/native identity checks before the existing select-export owner atomically writes /opt/laplace/etc/chess-corpus-export.json.

The exported files stay on durable storage. The workflow artifact contains logs, exact closure hashes, selected source/proof/pilot identities, focused TRX results, export/inventory summary and selection receipt. It does not upload multi-gigabyte corpus or managed outputs. This proves a complete observed read interval; it does not claim a transaction-wide snapshot, coverage of historically unrecorded games, throughput, or an installed serving cache.

## Completion after selection

A successful selection is the input to the ordinary main build. A subsequent ordinary build/install/publish at this same main source reauthenticates the export, includes it in the native fingerprint, regenerates seed coverage, merges recorded transitions, produces and seals the position/transition pair, and atomically installs the pair through the existing owner. Main activation and the existing verify-chess-floor-serving.py observer must then prove the running API maps the installed generation and records a persistent transition lookup. The export job itself performs none of those serving actions.

On any failure, retain its exact receipt and phase logs. A partial export is not selected. Every attempted publication reconciles the actual persisted selection, including after subprocess failure or cancellation. A matching validated export records selection_completed=true even when the operation failed. An absent/different observed selection records false; a failed or interrupted readback records null with selection_observation=unknown. The original failure remains primary; no rollback is assumed.
