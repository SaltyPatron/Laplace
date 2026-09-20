# Recorded-game chess perfcaches

The persistent chess floors are deterministic acceleration artifacts. PostgreSQL
retains the witnessed games and their provenance. Refreshing a floor does not
record another game, add observations, update consensus, or change position,
move, line, playing, or physicality identity.

The position floor contains finite typed atoms, the bounded move alphabet, seed
catalog boards, and boards reconstructed from the selected recorded corpus. Its
total record count is therefore not a corpus-position count. The transition floor
contains one canonical result for each `(from-position, typed-move)` key. Repeated
playings and repeated board occurrences can increase observed input counts
without increasing either floor's unique record count.

## General compositional-cache relation

Chess position/transition floors are one instance of the common compositional perfcache law in spec 33 / #1711.

~~~text
piece/square vocabulary
-> position records
-> transition records
-> line/replay composition
~~~

The same architecture now applies to image/audio/video and other deterministic recursive structures. Chess does not own a special cache ontology; it demonstrates mmap generation, canonical reuse, dependency-bound publication and process-lifetime hits that the common registry should generalize.

## Export verified recorded inputs

Use the normal matched managed/native build and the existing installed database
environment. The explicit mode is in the existing `ChessCatalogSurfaces`
executable; ordinary catalog-generation arguments keep their previous meaning.

```bash
catalog_dll="$(dotnet msbuild app/ChessCatalogSurfaces/ChessCatalogSurfaces.csproj \
  -property:Configuration=Release -getProperty:TargetPath)"
test -f "$catalog_dll"
export_dir="/build/laplace/recovery/chess-floor-exports/$(date -u +%Y%m%dT%H%M%SZ)-$$"
dotnet "$catalog_dll" export-recorded-floors \
  --output-dir "$export_dir" \
  --page-size 64 \
  --maximum-materialized-mib 128 \
  --maximum-retained-mib 512 \
  --deadline-seconds 3600 \
  --transition-buffer-records 65536 \
  --merge-fan-in 32 \
  --maximum-spill-mib 4096 \
  --maximum-export-mib 4096
python3 scripts/chess-floor-artifacts.py validate-export \
  --receipt "$export_dir/export-receipt.json"
```

The output directory must be new. The exporter reuses the starting-side
inventory's database identity, source-bound paging, byte preflight, strict
hydration, and completion reconciliation. Connections use the existing resolver,
bounded Serving transport, and `default_transaction_read_only=on`. The source
set is recorded PGN, book, and self-play playings.

The strict hydrator retains its already computed complete typed replay for this
export. That internal value is transient derived work, not serialized testimony
or an identity constituent. Strict replay uses the complete admitted move count
after its checked expanded-work reservation; the separate ordinary display replay
window remains unchanged. An unreadable or incomplete trajectory, competing
binding, exhausted byte allowance, cancellation, or count/identity mismatch
makes the export partial.

The immutable export consists of:

| File | Meaning |
| --- | --- |
| `positions.txt` | Canonical board interchange surfaces, one per verified board occurrence. Repeated surfaces are permitted. Surface spelling is not position identity. |
| `transitions.bin` | Corpus-only v1 transition floor, sorted and unique by the existing bytewise canonical key order. |
| `inventory/hydrated-playings.jsonl` | Retained exact witnessed inputs, canonical ids, starting side and all matching source bindings. |
| `inventory/summary.json` | The existing inventory's completed or partial read-interval receipt. |
| `export-receipt.json` | Schema `laplace.chess-recorded-floor-export/v1`, coverage counts, database identities, resource allowances, actual artifact lengths/SHA256 and completion status. |

`selected_playings` and `exported_playings` must reconcile.
`position_occurrences = transition_occurrences + exported_playings`.
`unique_transitions` is the actual completed writer result, not an estimate from
ply count. A failed later page cannot certify an earlier prefix as the complete
selected corpus. Partial exports retain their inventory failure and are refused
by the selection/build publisher.

The materialization allowance covers the strict reader's conservatively admitted
expanded work; it is not measured process RSS. Retained witnessed JSONL has its
own allowance. The transition buffer bounds the resident sort record array;
fan-in bounds open merge readers. Spill allowance covers owned run files plus the
unpublished final transition blob. Export-work allowance reserves exact surface
bytes plus one 32-byte potential transition record per occurrence and the
80-byte transition framing; deduplication can reduce final bytes. The native
position producer has separate declared memory and spill allowances.

## Select an exact retained recording scope

The same exporter accepts `--recorded-selection <absolute-manifest>` together
with `--recorded-selection-sha256 <sha256>`. Both are required. This selects only
the named playing identities through the existing source-bound hydration and
complete legal replay. Missing, extra, duplicated, incomplete or conflicting
selected games fail the export; global discovery is not a fallback.

The selection schema is `laplace.chess-recorded-selection/v1`. It binds the
original PGN, original selection JSONL and each contiguous complete sealed chunk
by absolute path, byte length and SHA256. Playing identities use lowercase
hexadecimal of their canonical native bytes. The original failed or completed
benchmark receipts remain unchanged.

For a failed capacity run with independently sealed complete chunks,
`scripts/verify-recorded-chess-selection.py` prepares this explicit manifest and
runs `laplace chess verify-recorded-corpus` against the current matched
source/native/database generation. Its declared source hashes, parent outcome,
complete count and evidence paths are explicit arguments; `--help` lists them.
The operation skips source bootstrap and prohibits writer work before apply.

The two verification passes compare current native game bodies, exact source
witnesses and full legal lines while checking the original retained EntityIds,
PhysicalityIds, WitnessIds and observation counts. Every retained identity must
still belong to current canonical composition. Newer completion metadata and
calculated repair lanes are outside this retained recording scope. The receipt
field `retainedScopeReplayVerified` therefore does not claim that whole current
ingestion is a no-op or that newer metadata has been installed.

An export from that manifest reports `count_scope: explicit-recorded-selection`
and retains the selected manifest, source and native-byte playing-ID digest.
A verified subset can supply a product cache even when its parent capacity run
failed. It does not satisfy that benchmark's larger requested game count,
duration or throughput criteria. Selection, normal build/install, consumer reload
and the actual persistent-hit proof below remain required for a usable cache.

## Build and publish the declared generation

The sole artifact helper validates export receipts and all four declared hashes.
The persistent selection owner stores the selected receipt outside the disposable
build tree. The pipeline passes that absolute selection through
`LAPLACE_CHESS_CORPUS_EXPORT`; CMake does not read the database during an ordinary
build.

CMake always regenerates opening/960 seed inputs through their existing owner
when their declared contents change. It adds the selected recorded surfaces to
the native position emitter and merges the selected corpus transition floor with
fresh seed transitions through the one transition writer. The same fixed v1
position and transition layouts remain in use. Fixed-size sorted runs and bounded
merges handle corpus size; duplicate keys with competing deterministic records
refuse the build.

On UNIX, `laplace_chess_position_perfcache` depends on
`build/engine/core/perfcache/chess-floor-pair.json` as well as both outputs.
Sealing verifies the native position input recipe, full file framing/order/body
checksums, selected input provenance, and corpus transition survival in the
merged output. Installation requires this complete receipt and activates the pair
through one generation pointer. A partial export, corrupt artifact, changed
input, or interrupted candidate cannot activate a half-built pair. Legacy
native-only and Windows seed installation retain their existing behavior;
recorded-corpus pair activation is explicitly UNIX-only.

Already running application and PostgreSQL processes can retain old mappings.
Complete activation therefore includes the existing controlled reload/restart
owner and actual serving-process readback. A file hash at the configured path or
`pg_reload_conf()` alone does not prove that a previously loaded process remapped
the new generation.

## Required proof

The normal controls cover canonical typed replay beyond the display window,
byte refusal, repeated line observations, unique transitions, incomplete later
pages, output limits, bounded multi-run merges, corruption/conflicting keys,
deterministic output and complete-only publication.

Live proof must bind the selected export, both installed hashes, and the actual
serving process. Use at least one verified corpus-derived position/transition
absent from the seed floor, establish canonical parity, and demonstrate a
persistent lookup hit after activation. Report persistent hits separately from
the process-local `Remember` cache. Report source games, board/transition
occurrences, unique corpus records, finite alphabet records and actual bytes
separately. Neither fixture success nor a total floor record count proves live
corpus coverage or recorded-game throughput.

An export reports an observation interval. Independent page reads are not an
MVCC snapshot, and matching before/after counts do not prove that no concurrent
replacement occurred. Retained source identity and receipt times describe the
observed selection; they do not establish prior database contents.
