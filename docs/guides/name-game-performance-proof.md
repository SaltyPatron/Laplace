# The Name Game — human-vs-Laplace identity, realization and latency proof

The Name Game is one small Knowledge Arena surface that makes several Laplace laws visible to a human without first explaining the entire substrate.

It is deliberately **not** a declaration of Laplace's limits or a private game intelligence stack. Active ownership remains in the current Knowledge Arena/entity-world/forward issues; related work in other repositories is coordination only.

## Product hook

A useful answer to “is Laplace slow?” is not only a synthetic benchmark. Give a human and Laplace the same deterministic semantic task and race them while retaining an exact operation/resource receipt.

A simple chain looks like:

```text
Bobby Fischer
      F
Frank Sinatra
      S
Sandy Koufax
      K
...
```

One possible challenge rule is:

```text
family-name initial(current)
    ==
given-name initial(next)
```

The playable entity estate, realization/language law, no-repeat rule, world epoch, timing law and resource envelope are challenge state—not one universal notion of fame or naming.

## Why this is a Laplace benchmark instead of a string game

A submission should exercise something like:

```text
submitted Unicode text
-> exact decomposition/admission
-> query-relative identity/name coupling
-> resolve candidate canonical entity under challenge scope
-> select governed structured name realization
-> validate required endpoint/initial orientation
-> verify event-trajectory no-repeat state
-> accept/reject with typed reason
-> append accepted occurrence to event trajectory
-> advance next challenge state
```

That touches Unicode/grapheme handling, canonical identity, aliases/references, structured realization, event occurrence state, exact duplicate detection, indexed retrieval and deterministic validation.

It must not be implemented as `split(' ')` plus a private celebrity dictionary.

## Identity is not a display string

The same person/character/entity can have several names, aliases, transliterations and presentation orders. Those are contextual realization/reference state, not new canonical game pieces.

For example:

```text
entity: Itachi Uchiha
structured realization:
  given_name:  Itachi
  family_name: Uchiha
```

A UI may display `Itachi Uchiha` or `Uchiha Itachi` according to a selected cultural/language policy. That does not swap semantic roles merely because visible order changes.

Submitting both strings cannot manufacture two different playable entities under a no-repeat rule if they resolve to the same canonical referent.

Likewise provider handles, titles, nicknames, external identifiers and transliterations remain evidence/references used by resolution/realization; they do not mint extra game pieces simply because their surface strings differ.

Current executable ids are finite Hash128/BLAKE3-derived addresses under declared recipes, not the human label and not the abstract content itself.

## Unicode endpoint law

Initials come from the selected structured name components under the challenge's Unicode/grapheme/normalization law, not ASCII byte indexing.

A receipt preserves enough state to reproduce the decision:

```text
submitted text
resolved canonical entity
selected name/alias evidence
structured playable components
normalized grapheme/initial values used by the rule
language/realization policy
challenge generation / event prefix
```

Diacritics, punctuation, transliteration and multi-codepoint graphemes are therefore explicit fixtures rather than accidental edge cases.

## Mononyms, particles, titles and character names

The challenge declares its playable-name policy.

A strict two-role mode may reject a mononym when no governed second endpoint exists. Another explicitly named mode may allow it. Names such as `Malcolm X`, `Ludwig van Beethoven`, `Leonardo da Vinci`, `Martin Luther King Jr.`, `Dwayne "The Rock" Johnson`, `Monkey D. Luffy`, `Pope Francis` or fictional/title-heavy forms are resolved through structured realization rather than patched first/last-token heuristics.

Ambiguity produces a typed ambiguous/invalid/why-not disposition instead of silently guessing an endpoint.

## Event occurrence is not canonical identity

The player/event trajectory records which canonical entities have already occurred in **this match**.

```text
canonical entity E
  can occur in many games/challenges globally
  but may be forbidden from occurring twice in this event by firmware
```

No-repeat detection therefore uses exact event occurrence/trajectory state; it does not remint `E` with an event id or store a fake global “already used” property on the entity.

## Query-relative coupling

If a submitted surface has several possible referents/name senses, the game should not choose the first globally popular label and search only around it.

The exact submission plus challenge estate, language/realization law, prior event trajectory and current required initial constrain the eligible responses jointly. The canonical cognition order remains:

```text
RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE
        → PROPOSE → STEER → SELECT → REALIZE → WITNESS
```

A simple deterministic game may compile much of that into one coarse native operation, but the semantic receipt still distinguishes what was resolved, what responded, what constraints eliminated candidates and why one entity was selected/rejected.

## Performance law

The game is especially useful because its semantic work is small enough that orchestration overhead becomes obvious.

A conforming hot operation should look like:

```text
one bounded request/event state
-> prepared indexed/set-sized lookup
-> native C/C++ resolution/validation/event checks
-> bounded answer + receipt
```

It should **not** look like:

```text
split token
-> SQL lookup
-> managed loop
-> SQL alias lookup
-> SQL name-part lookup
-> SQL event duplicate lookup
-> SQL next-letter lookup
-> ...
```

When the meaningful native/indexed work is microseconds but per-step SQL/PInvoke/SPI/client orchestration turns the operation into milliseconds, that is an execution-grain defect—not the inherent cost of the Name Game or Laplace.

This is the same law used by decomposition, ingestion, cognition, analysis, reconstruction and export.

## Human-vs-Laplace measurement

A match can report both human and machine timing without confusing them.

Useful machine receipt fields include:

```text
submission bytes/codepoints/graphemes
resolved candidate count
indexed probes / rows/cells touched
alias/name evidence inspected
coupling/constraint eliminations
native/DB boundary-call count
CPU time / wall time
accepted/rejected disposition
selected entity / next-state fingerprint
```

The goal is not to manufacture a favorable “tokens/s” number. It is to show how much exact semantic/state work one bounded operation actually performs and how little of the world it needs to touch.

## Challenge fairness

A ranked generation pins:

```text
eligible entity estate / world epoch
name/reference/realization providers
grapheme/normalization law
starting entity/letter/queue
no-repeat/event firmware
ambiguity policy
resource envelope
server timing law
```

A later ingest/alias correction creates a new challenge generation rather than silently changing a completed ranked match.

## Witnessing

Accepted/rejected submissions and match results may be recorded as event observations/receipts. They do not become world truth simply because the game generated them.

A player's submitted alias can be evidence about usage; it is not automatically admitted as a canonical name without the normal source/evidence law.

## Acceptance

The Name Game proves the intended machine only if:

- aliases/display order cannot create duplicate canonical game pieces;
- structured name roles, not raw first/last whitespace tokens, determine endpoints;
- Unicode/grapheme normalization is explicit and replayable;
- ambiguous surfaces remain ambiguous/rejected when the challenge cannot resolve them lawfully;
- no-repeat uses event trajectory occurrence state without salting canonical identity;
- equivalent UI/API/CLI game operations share semantic behavior;
- the hot path uses coarse indexed/native execution rather than per-field/per-candidate RBAR;
- operation receipts expose enough work to distinguish useful semantic latency from boundary overhead;
- ranked challenge generations remain replayable after the live substrate changes;
- game outcomes can be witnessed without self-certifying new world facts.

The game is intentionally tiny. Its value is that identity, realization, event state, query-relative selection, sparse addressability and execution-grain performance become easy to falsify in front of a person.