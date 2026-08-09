# Chess modality guide

The normative chess contract is
[`docs/specs/11_Chess_Provenance_Consensus_Spec.txt`](../specs/11_Chess_Provenance_Consensus_Spec.txt).
This guide describes how to discover and operate the installed surface without embedding
live counts or issue status.

## Verify the environment

Connect to `laplace`, set `search_path = laplace, public`, then inspect:

```sql
SELECT * FROM substrate_health();
SELECT * FROM api('chess');
SELECT * FROM source_roster();
```

Do not infer chess capability from bootstrap source registration alone. Use the
source-specific verification operation exposed by `api()` and name the seed profile.

## Identity

- Resolve/compose positions through the chess modality surface; do not hash FEN/display
  strings externally.
- Position identity includes complete canonical board state required by the modality.
- Games are ordered trajectories with source and context provenance.
- Equivalent positions reached from games, books, openings, and self-play converge while
  their occurrences remain distinct witnesses.

## Evidence lanes

- Recorder: literal headers, participants, result, movetext, annotations, clocks, and
  source metadata.
- Analyzer: replay-derived positions, transitions, motifs, evaluations, and quality under
  a versioned calculated source.
- Runtime/self-play: played actions and outcomes under their own source/trust class.

Recorded game outcomes and calculated move quality must never masquerade as the same
witness. See the record-versus-calculate specification.

## Canonical reads

Discover exact signatures with `api('chess')`. Typical operations include position
continuations, player-scoped continuations, legal actions, move application, evaluation,
best move, game/line realization, and lab status. Rank consensus reads conservatively
and preserve source/context scope.

## Ingest and analysis

Use repository seed/workflow/CLI entry points rather than direct table writes. One ingest
at a time. Recorder ingestion precedes versioned analysis; independent verification
proves completeness. Re-running a source requires its marker/idempotency contract.

## Playing as a forward pass

The conforming program is:

`COMPOSE → ORIENT → PROPOSE → STEER → SELECT → REALIZE → WITNESS`

Classical search may propose candidates. Substrate evidence, line continuity, source
scope, consensus, clocks, tablebase facts, and domain testimony steer them. Finished
actions and outcomes witness back through the ordinary fold.

## Testing

Acceptance names the corpus/seed profile and proves:

- position/game round-trip;
- legal action conditioning on full state;
- recorded/calculated source separation;
- deterministic source-scoped rankings;
- per-action semantic receipts;
- feedback from played games affecting a subsequent decision.

Use [chess-lab.md](chess-lab.md) for the lab runner. Active defects and work ownership
belong in GitHub issues, not this guide.
