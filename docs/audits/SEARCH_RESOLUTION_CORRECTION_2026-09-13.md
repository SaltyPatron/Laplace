# Search / resolution correction — 2026-09-13

## Scope

This is a defect record for user-facing search, Browse and direct resolution behavior in the older `Laplace` repository. It records concrete implementation contradictions. It is **not** a claim that the complete Laplace cognition/search architecture is implemented or accepted.

## Invariant

A deterministic content identity answers what the canonical identity of exact content would be. It does **not** establish that the entity is admitted in the current Laplace world. Likewise, failure to find an exact queried composition does not erase admitted constituents, occurrences, containers, name evidence or other queryable world state.

## Defect 1 — arbitrary input was treated as a resolved topic

On `main@12353b45fc7cd2a8cc0ef1b89dd2500618710d98`, `converse.resolve_ref(text)` returned a decoded 32-hex id or `laplace.word_id(p_ref)` with no admitted-state presence check.

`/v1/query` calls `ResolveTopicAsync`, which consumes `ResolveRefAsync`, which invokes this function. Therefore arbitrary text could obtain a deterministic 128-bit identity and proceed as a resolved query topic even when that id had never been admitted.

This explains the empty/unlinked-entity class of behavior: a search string could have a valid-looking canonical id without corresponding world state.

### Immediate correction

The corrective branch returns the calculated id only when `consensus.entity_exists(id)` is true. This is deliberately fail-closed. Free-form discovery belongs to decomposition/Browse/query, not to fabrication of an unwitnessed topic.

## Defect 2 — Explore converted an unwitnessed calculation into entity-like navigation

The legacy Explore resolver calculated ids for arbitrary surface input and returned `exists=false` when an id was not witnessed. React `ResolveRedirect` then routed that state to `/explore/notfound/<surface>`, where a calculated geometry/anchor was presented for the unwitnessed content.

Hypothetical structure may be a valid explicit calculation. It must not replace admitted constituents and containers when the user is trying to search the world that actually exists.

### Immediate correction

Historical `/explore/resolve/<surface>` links now behave as compatibility redirects: exact 32-hex ids remain direct entity addresses; arbitrary surface text goes to `/explore?q=<surface>` so Browse performs decomposition and admitted-state discovery. The Browse zero-result UI no longer offers the hypothetical structural neighborhood as an entity result.

## Defect 3 — Browse hid decomposed members and collapsed provenance

Browse already decomposes the query into canonical identities, uses trajectory/content-membership machinery to find stored structures containing all selected members, attaches eligible name/alias evidence, and batch-reads entity facets and labels.

However, result assembly omitted the admitted query-member identities themselves and labeled direct non-name results with the generic `surface` match kind. For `Sodium Chloride`, this can hide exactly the state expected from the invention: `Sodium`, `Chloride`, and stored compositions containing both.

### Immediate correction

The corrective branch distinguishes:

- `exact` — exact queried composition exists;
- `member` — decomposed query constituent exists;
- `container` — stored composition contains all selected query constituents;
- `name` — entity reached through eligible name/alias testimony.

Every calculated direct id remains presence-gated by the existing entity-facet batch before output. If it is not admitted, it is not returned as an entity hit. The browser labels the provenance explicitly. An E2E fixture for `Sodium Chloride` requires admitted `Sodium`, admitted `Chloride`, and a containing composition as separate results.

## What this correction does not implement

This branch is not complete Laplace search. #1533 remains open for ordered contiguous matching, contains-any/partial overlap, upward expansion, richer evidence lanes, ranking/folding of candidate classes, and measured bounded execution. #1537 remains open for the repository-wide search/find/filter/resolve semantic audit.

The clean Refactor #17/#60 cognition/search law is broader than this older Browse implementation. This correction must not be cited as proof that the older repository implements the complete clean-machine search architecture.

## Required negative controls

Future acceptance must reject:

1. `hash(input) -> resolved entity` without admitted-state proof;
2. an unwitnessed exact phrase replacing admitted member/container results;
3. Browse omitting admitted query members;
4. rendered substring matching substituted for canonical decomposition/containment discovery;
5. geometric proximity or a hypothetical anchor substituted for admitted-state discovery;
6. hidden match provenance behind a generic `surface` label;
7. empty exact-root lookup promoted to global absence.

## Owning work

- #1533 — compositional Browse fallback and result lanes;
- #1537 — repository-wide search/find/filter/resolve semantic audit;
- #1534 — lexical/case behavior in Browse;
- #1398 — exact referent identity versus fuzzy/name-derived contamination.

The immediate branch removes one false entity-resolution path and exposes more of the existing admitted structural state. It does not close those owners.