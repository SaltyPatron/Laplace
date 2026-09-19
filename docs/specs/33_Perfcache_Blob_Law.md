# Perfcache blob law

PostgreSQL is the system of record. A perfcache blob is a deterministic, derived,
one-way runtime acceleration structure. It is never independent truth, never the only
copy of testimony, and never a manual data store.

Every blob format defines:

- magic, version, byte order, record layout, and bounds;
- source database generation/fingerprint;
- relation/manifest/recipe compatibility inputs;
- complete-file checksum and, where needed, section checksums;
- deterministic ordering and duplicate handling;
- writer, loader, validation, and stale-file behavior;
- the canonical PostgreSQL operation that rebuilds or verifies it.

Loaders reject missing, truncated, corrupt, incompatible, or stale blobs explicitly.
They do not partially load and continue. Publication uses write-to-temporary, flush,
checksum, and atomic replace. Readers map immutable files and never mutate their source.

Cache contents preserve typed meaning. A point cache stores point facts; a trajectory
cache stores ordered manifests; a model-factor cache stores versioned factor records.
Packing values into one binary format does not erase their semantic classes.

Perfcache usage must preserve parity with the canonical database/native reference path.
Parity includes ids, ordering, scores, unknown behavior, and source scope. Performance
tests do not replace semantic parity tests.

A green CI job, a file on disk, or a PostgreSQL GUC pointing at a path is not proof
that the serving process can load the blob. Process-local native loaders, PostgreSQL
`MODULE` bindings, and prefix libraries must identify the same build.

## Roster

This table is the current blob catalog. It is law for *what exists as a
perfcache class*, not a claim that every row is loaded on the live host.
Observed install state belongs in CI receipts and `scripts/check-deployed-revision.sh`.

Tier-0 is always Unicode codepoints. Image, audio, and video do not mint a
private atom alphabet; they compose above T0 (digit → number → channel / sample
→ higher tiers). Glicko standing is not a perfcache. Attestation ids are not
codepoint ids.

| Blob | Role | Lookup | Rebuild | Notes |
|---|---|---|---|---|
| `laplace_t0_perfcache_<ucd>.bin` | Unicode 0..0x10FFFF: id, UCA order, PointZM, Hilbert, UAX flags, NFC compose/decomp | `records[cp]` | UCD emit (`codepoint_table` / Unicode decomposer) | Format v4 (`LPRF`). Legacy v3/banded geometry is rejected. |
| `laplace_highway_perfcache.bin` | Relation-law bit plane: canonical name, band, bit, rank | bit test / 256-bit mask | relation-manifest codegen | Does not populate live consensus band counts. |
| `laplace_modality_number_perfcache.bin` | Decimal content roots for integers 0..255 | `records[value]` | `modality_number_tables_emit` from T0 | Image channel bytes; in-range audio magnitudes. No PostgreSQL GUC as of 2026-09-19. |
| `laplace_chess_position_perfcache.bin` | Piece×square vocab and catalog boards | id → coord/hilbert/tier | recorded-floor export | GUC `laplace_substrate.chess_position_perfcache_path`. |
| `laplace_chess_transition_perfcache.bin` | Deterministic `(from, move) → to` | mmap search | recorded-floor export | Not a Glicko dump. App-side today; no PostgreSQL GUC. |
| Factor ROM | Versioned model-factor trajectories | pointer arithmetic | deposited factor physicalities | Designed (#526). Not installed. |
| Generation-corpus ROM | Cold `walk_text` / generation lane | mmap | generation corpus | Prescribed (#409). Not installed. |
| Separator-id ROM | Alphabet-bounded separator atoms/clusters | compiled set | T0 + grapheme law | Named in `docs/sql-cascade.md`; still a scan. |

New blobs land only with: a row here, a one-way rebuild path (blob never seeds
Postgres), determinism/staleness gates, a loader that refuses unknown versions,
and a serving-process probe that the deployed revision check actually runs.

The 2026-07-18 table in `docs/archive/specs-v1/33_Perfcache_Blob_Law.md` is
historical. Do not treat its “live/landing” column as current host state.
