# Chess re-key design

## Goal

Split position **identity tokens** (faithful FEN-equivalent substructures) from optional **feature tokens** (derived chess patterns) so the substrate can learn which structural features predict outcomes without changing position identity for existing ingested games.

## Token classes

| Class | Examples | Purpose |
|-------|----------|---------|
| Identity | `stm`, `cr`, `ep`, piece placement `Pe2`, pawn skeletons, `mat` | Transposition-stable position id (default surface) |
| Feature (gated) | `mob`, `open`, `kzone`, `outpost` | Pattern sharing for novel positions; enabled with `LAPLACE_CHESS_REKEY=1` |

Feature tokens append to `PositionContent.Surface` only when the env var is set. Default surface is unchanged → backward compatible reads.

## Orphan / migration strategy

1. Ship code with feature tokens **off** (default).
2. Parent runs full corpus re-ingest with `LAPLACE_CHESS_REKEY=1` after validation.
3. Old identity-only nodes remain addressable; new nodes share substructures where identity tokens match.
4. No destructive delete — orphans decay via witness competition.

## Re-ingest plan (parent-owned)

```cmd
set LAPLACE_CHESS_REKEY=1
scripts\win\seed-step.cmd chess D:\Data\Ingest\Games\Chess
```

Validate with substrate-test + ladder before production re-ingest.

## Acceptance

- `PositionContentTests` pass with and without `LAPLACE_CHESS_REKEY=1`
- Identity surface unchanged when flag unset
- Feature tokens present when flag set
