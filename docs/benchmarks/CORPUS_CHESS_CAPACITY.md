# Recorded capacity from an existing chess corpus

`laplace chess measure-corpus` measures new PLAYING occurrences from an existing, unchanged PGN file through `ChessPgnIngestor`, its ordinary composition/consensus writer, synchronous PostgreSQL commit acknowledgement, and the shared exact native readback owner. It does not generate engine games. An imported occurrence retains the original event, site, date, round, players, line and result identity.

Use a new evidence directory and an explicitly selected plain UTF-8 PGN:

```sh
laplace chess measure-corpus \
  --pgn /absolute/path/to/games.pgn \
  --evidence-root /absolute/path/to/new-corpus-evidence \
  --games 75000 \
  --minimum-seconds 30 \
  --replays 1 \
  --deadline-seconds 3600 \
  --expected-sha256 <observed-or-independently-verified-lowercase-sha256>
```

The expected hash is optional; when supplied, it must match the actual source bytes. The source receipt always records the raw file length and SHA256. An observed local-file hash identifies those bytes; it does not establish how a remote dataset was acquired. Preserve a provider acquisition receipt separately when one exists. This command creates no substitute provider URL, match event, round number or occurrence ID.

Current source configuration references corpora beneath `LAPLACE_DATA_ROOT` or `/vault/Data/Games/Chess`, including Lumbras/TWIC/player exports. Those references do not establish that a particular file exists on the running machine. Select an actual observed file. Compressed exports must be obtained/decompressed before this command; the initial measured route deliberately uses the existing strict UTF-8 plain-file reader. It rejects UTF-16/32 BOM substitution and malformed UTF-8.

## Preparation and eligibility

Preparation runs before the timed admission window. The ordinary native PGN owner verifies complete syntax containing one source game, a finished serialized result agreeing with its header, coherent SetUp/FEN declarations, legal complete mainline replay and valid king configuration. Human resignation, time forfeiture and agreed draw can finish a source game while legal moves remain. This is separately labeled from the stronger board-terminal completion policy used for normal CuteChess selfplay measurements.

Unfinished `*`, absent/conflicting results, illegal/truncated syntax, abandoned/unterminated games, inconsistent PlyCount and invalid setup are rejected. Empty games are excluded from the capacity selection. A forced mate/stalemate cannot contradict the declared result. Claimable repetition/fifty-move states do not cause the collector to truncate a genuine source continuation.

Selection uses the ordinary PLAYING presence probe and keeps the first requested eligible occurrences that are absent at preparation time. Duplicate selected/current-chunk candidates are excluded. Existing source occurrences remain existing; replays never receive new identities. Presence is checked again by normal admission, so a concurrent import cannot silently turn an already-present occurrence into a fresh numerator.

`selection.jsonl` retains each selected original game ordinal, the framed text hash, native PLAYING/line/start IDs, full ply count and declared result. Framing uses the existing PGN reader: original headers/movetext are preserved, a UTF-8 BOM is consumed if present, and line endings are normalized to LF. This framed hash is distinct from the raw-file hash. Preparation reports scanned, complete/legal, rejected, empty, duplicate-selection, already-present, selected and eligible-over-request counts. It also reports line/start diversity and full-game ply ranges.

No measured admission starts if the source cannot supply the requested number of eligible distinct fresh occurrences. The actual source and selection-manifest bytes are bound throughout the operation. Preparation naturally warms parser/identity caches; the receipt labels this workload accordingly.

## The measured numerator and window

The fresh numerator requires every requested selected PLAYING to be newly admitted, synchronously committed and exactly read back. Entity, physicality and attestation counters remain separate. Reused content does not become a new entity merely because another authentic playing uses it.

The single contiguous fresh window includes source reading/framing, repeated native parsing and normalization, ordinary shared composition, writer/consensus work, WAL acknowledgement, exact native game/witness/carrier readback, chunk evidence serialization and the final exact state manifest. Source acquisition, preparation and bootstrap are outside that window. Replay is measured separately. The production chunk resolver remains authoritative; its actual resolved value is recorded, with no private benchmark batch tuning.

This measures one existing in-process admission lane. It is not a whole-machine maximum or a comparison of engine playing speed. Laplace-vs-Stockfish generation and Stockfish-vs-Stockfish generation are different workloads and do not supply this numerator.

A completed integrity run can remain unqualified for a sustained-rate claim. Qualification requires at least the requested minimum duration (never less than 30 seconds), varied source lines and successful exact replays. The default 75,000 fresh occurrences is the volume required for 2,500/s over 30 seconds; it is not a guarantee of that rate or duration. A faster run needs more authentic fresh occurrences to establish the same sustained window. Short runs retain their measured rate with an explicit unqualified verdict.

## Exact readback, replay and retained evidence

The common recording verifier remains the owner of stored witness identity/body/membership, canonical line carrier vertices and native hydrated full game bodies. Corpus mode omits the CuteChess-specific experiment witness because no CuteChess match was created. Source provenance comes from the actual bound PGN bytes and unchanged occurrence metadata.

Each ordinary chunk is retained before its move graph is released. The collector does not hold 75,000 full move graphs in memory. It retains small selected-occurrence metadata and per-chunk file identities. Sorted scope files are folded on disk with at most 32 input files open per merge. Shared rows must have continuous observation counts across fresh chunks; their latest exact state forms the post-pool manifest.

Every replay uses the same source and canonical occurrence identities. It requires:

- Identical ordered game-body fingerprints after another complete native database readback.
- Zero for every writer counter, zero newly recorded/applied occurrences and no new durability receipt.
- Identical before/after scope rows for every replay chunk.
- An aggregate replay state identical to the original post-pool state, detecting growth between phases as well as during replay.
- Unchanged source, selection manifest and retained original chunk/body/scope/state evidence.

`corpus-recording.json` is the aggregate result and includes phase receipts, timing scope, actual resolved chunk size, source identities, counts, qualification and target verdict. Each phase contains `recording.json`, `chunks.jsonl`, exact chunk bodies, sorted scope inputs and the aggregate state. The chunk manifest itself is compared against the exact observed chunk sequence before it is sealed. Failure/cancellation retains available evidence and cannot establish the target; interrupted counts are separated from already-present occurrences.

Exact scope follows the existing recording owner: explicit source EntityRows, selected nonempty line Content physicalities and source playing/header/setup/result witnesses with observation counts. Native text-stage interior rows, calculated analysis lanes, shared bootstrap rows and unrelated writes remain outside this particular snapshot. Writer accounting still includes the ordinary applied work.

## Validation ownership

The managed solution owns CLI argument, source-byte/completeness and disk-evidence controls. Native source tests preserve the existing recorded fixture; synthetic parser/transport controls are explicitly test fixtures and never benchmark corpus input. The disk controls include more than 32 merge inputs, shared-row count evolution, every writer counter, altered game order/body, incomplete replay and retained-evidence corruption.

Passing these controls does not establish a throughput result. The source-built CLI must run against an observed real corpus and the deployed PostgreSQL/native writer/readback path, and its exact retained receipt must be inspected.

## Operational observation and execution

The explicit `chess-corpus-evidence.yml` workflow provides separate inventory and measurement invocations. Its operator branch patterns are `verify/chess-corpus-inventory-*` and `verify/chess-corpus-measure-*`; ordinary main pushes do not select a measurement. Inventory lists actual configured Lumbras OTB, elite and TWIC files within bounded directory/file limits. Compressed files remain visible and ineligible. Directory symlinks are not traversed. Automatic measurement selection requires exactly one observed nonempty plain PGN in the selected OTB-2025 or elite family; an explicit observed absolute path can select another existing plain file.

Run inventory first and inspect `source-inventory.json`. After the requested revision has been built and deployed, measurement hashes the selected actual source and passes that hash to the managed command. It follows the existing `scripts/laplace` source-built CLI route, including the ordinary Release build and native synchronization. The repository currently publishes API/UCI/MCP/Lichess services; this receipt does not pretend that the CLI is separately installed.

The wrapper reuses `check-application-runtime.py` before and after admission to bind the source fingerprint, installed-form native ELF/ROM bytes, installed PostgreSQL extensions and migration state. CLI app-local native files must match the shared build that the installed-form owner verifies. Complete managed/native CLI file identities and the original launcher are retained before and after execution. Both the managed connection and the native guard select the same local `laplace` database. No database reset, source eviction, synthetic occurrence rewrite or engine generation belongs to this operation.

One shared host lock spans checkout, identity checks, source selection, CLI preparation, all admission/replay work and postflight checks. A postflight identity failure leaves the overall result failed and cannot establish the target, even when an inner phase completed. Partial logs and evidence upload after failures. Source file acquisition remains labeled as observed local bytes unless separate authentic provider acquisition evidence is available.
