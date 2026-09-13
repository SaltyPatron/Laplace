# Search / resolution correction — 2026-09-13

## Scope

This records the corrected boundary between deterministic content identity, admitted substrate state, and Browse discovery in the Legacy repository. It does not claim complete cognition or search.

## Admitted-state invariant

A deterministic identity answers what an exact content identity would be. It does not prove that the entity is present in the admitted world. Direct reference resolution therefore must not return a calculated identity unless the substrate witnesses that identity.

Free-form discovery is a separate operation. Browse decomposes input into canonical members, reads admitted structures, and exposes the result provenance rather than promoting a calculated but absent root into an entity result.

## Current Browse result classes

The current native Browse implementation distinguishes:

- `name` — an entity reached through admitted name or alias evidence;
- `surface` — an exact witnessed queried identity;
- `contains_all` — an admitted structure containing all selected query members;
- `constituent` — an admitted canonical member produced by query decomposition.

The entity-facet batch remains the presence gate for structural candidates. An absent calculated identity does not survive that gate.

## Corrections in this change

1. `converse.resolve_ref(text)` now returns a literal or calculated identity only when `consensus.entity_exists(id)` confirms admitted state.
2. Historical `/explore/resolve/<surface>` routes exact 32-hex identities directly to the entity page and routes all other input through Browse.
3. A zero-hit Browse result no longer offers the calculated input as a synthetic entity/structural-neighborhood result.
4. The web type contract is narrowed to the match kinds actually produced by the current native Browse implementation.

## Preserved behavior

The merged constituent/containment implementation remains the authority. This correction does not replace its native matching code, ranking order, input validation, or PostgreSQL fixtures.

## Remaining product work

Complete Laplace search/cognition still requires the broader work tracked by the repository, including ordered/contiguous matching, contains-any/partial overlap where intended, upward expansion, lexical/case behavior, richer evidence lanes, ranking/folding, and the clean Refactor search/cognition architecture.
