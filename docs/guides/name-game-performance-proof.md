# The Name Game — human-vs-Laplace performance, identity, realization, and event proof

This guide records a concrete Knowledge Arena proving surface discovered during product brainstorming.
It is intentionally **not** a declaration of the limits of Laplace or even of the game family. The
named games in `knowledge-arena.md` are examples that exercise reusable machine laws. They should be
read as one small visible pixel in a much larger design space, not as an exhaustive feature list.

Active product ownership remains `#1421` (Knowledge Arena), with common entity-world/search behavior
owned by `#1404/#1401`. The clean counterpart is `Laplace-Refactor#138` over the common
`#17/#18/#60/#68/#132` machine.

## Product hook

A useful response to the claim that Laplace is "slow" is not only a synthetic latency number. Give
Laplace and a human the same deterministic semantic task and let them race.

The Name Game is deliberately understandable without explaining the architecture first:

```text
Bobby Fischer
      F
Frank Sinatra
      S
Sandy Koufax
      K
...
```

The ordinary forward rule is:

```text
family-name initial(current)
    ==
given-name initial(next)
```

The game may admit famous real people, fictional characters, or another explicitly declared entity
set. Eligibility is installation/challenge state; it must not silently become one universal
"famousness" score.

## Why this is a Laplace benchmark rather than a string game

A submitted answer should execute approximately:

```text
submitted Unicode text
-> canonical decomposition
-> resolve candidate entity
-> select eligible identity/name evidence
-> realize structured playable name endpoints
-> validate required initial/orientation
-> verify entity has not already occurred in this event
-> accept/reject with reason
-> append the accepted occurrence to event physicality
-> advance firmware state / next required letter
```

The benchmark therefore exercises resolution, Unicode handling, alias/reference identity, realization,
entity typing, event state, duplicate detection, deterministic validation, search/selection and
physical execution timing through one small game.

It must not be implemented as `split(' ')` plus a private celebrity dictionary.

## Identity is not display order

Human-facing name order cannot define canonical identity or the playable endpoints.

For example, a selected identity can expose structured state equivalent to:

```text
entity: Itachi Uchiha

given_name:  Itachi
family_name: Uchiha
```

A realization may display `Itachi Uchiha` or `Uchiha Itachi` according to language/cultural/context
policy. That does not swap the semantic roles merely because the visible token order changed.

This closes a real cheese class:

```text
Uchiha Itachi
Uchiha Sasuke
```

A naive first-token/last-token parser can incorrectly treat both as `U -> ...`. A conforming validator
resolves the entities and validates their governed naming roles. Reversing the visible rendering is
not a legal way to manufacture another initial transition.

Likewise, submitting both:

```text
Itachi Uchiha
Uchiha Itachi
```

cannot produce two playable entities under a no-repeat rule. They resolve to the same canonical
referent for the event.

Provider handles, aliases, real names, transliterations and external references remain evidence used
by the identity/realization machinery; they do not mint extra game pieces merely because the strings
differ.

## Unicode endpoint law

Initials must be derived from the selected structured name components through the Unicode/grapheme
policy selected by the challenge, not ASCII byte indexing.

At minimum the receipt should preserve:

```text
submitted text
resolved canonical entity
selected playable realization
structured given/family endpoints
normalized initial values used by the rule
challenge language/realization policy
```

This makes cultural order, punctuation, diacritics, transliteration and multi-codepoint graphemes
explicit test cases rather than undefined behavior.

## Mononyms, particles, titles and character names

The base two-endpoint mode should fail closed when the selected realization cannot supply the two
required naming roles. Examples discussed during design:

- `Cher` is invalid in the strict two-endpoint mode unless another explicitly named mode defines a
  mononym rule.
- `Malcolm X` is valid when the governed realization supplies `Malcolm` as the first endpoint and
  `X` as the terminal naming endpoint.
- `Howard the Duck` must be decided from structured character-name realization rather than blindly
  treating the first and last whitespace tokens as given/family names.
- `Dr. Doom`, `Pope Francis`, `The Rock`, `Dwayne "The Rock" Johnson`, `Monkey D. Luffy`,
  `Ludwig van Beethoven`, `Leonardo da Vinci`, `Martin Luther King Jr.` and similar shapes are
  adversarial fixtures for the realization contract, not exceptions to be patched into the game.

A challenge recipe must state its playable-name policy. Ambiguity produces a typed invalid/why-not
result rather than a guessed endpoint.

## Doubles and direction reversal

The current brainstorm includes a special **double** mechanic for answers whose playable endpoints
share the same initial:

```text
Dom DeLuise
Donny Darko
Darkwing Duck
```

The proposed game effect is that a double reverses the endpoint/orientation rule for subsequent
play. The exact transition must be encoded in firmware and receipt state; it must not live in UI
special cases.

Conceptually:

```text
normal orientation:
  current FAMILY -> next GIVEN

double encountered:
  flip orientation

reverse orientation:
  current GIVEN -> next FAMILY
```

A later double may flip the orientation again if that is the selected recipe. Because this was
brainstormed as a game rule rather than a substrate invariant, the exact recipe remains explicit and
versioned instead of becoming universal identity semantics.

## No repeats in the same event

Accepted entities form another event physicality trajectory:

```text
Bobby Fischer
-> Frank Sinatra
-> Sandy Koufax
-> ...
```

A common mode requires a self-avoiding trajectory:

```text
next canonical entity NOT IN current event trajectory
```

Aliases, alternate renderings, handle spellings or name-order changes cannot bypass this because the
repeat check is on canonical entity identity.

As with the other Arena games, no-repeat is firmware, not a global substrate law.

## Race modes

### Alternating duel

Human and Laplace share one trajectory. Each accepted answer determines the next state for the other
contestant:

```text
human:   Bobby Fischer
Laplace: Frank Sinatra
human:   Sandy Koufax
Laplace: ...
```

This rewards both response speed and adversarial choice. A player can choose a valid answer whose
outgoing letter leaves the opponent a smaller legal frontier.

### Parallel sprint

Both contestants receive the exact same start entity, world/eligibility boundary, name policy and
clock. They independently build the longest valid chain during a fixed interval.

### Trap / frontier strategy

The best move need not be the most obvious valid name. A strategic player can optimize approximately:

```text
valid answer
+ preserve own future options
+ minimize opponent's next eligible frontier
```

`Malcolm X` is a useful example because a terminal `X` may be much harder to answer than a common
letter under a given admitted world.

Difficulty levels should change search/selection firmware rather than inserting artificial sleep:

- easy: any valid eligible continuation;
- stronger: prefer well-supported/recognizable continuations;
- adversarial: minimize opponent frontier under the same declared rules;
- deeper strategy: account for orientation flips/no-repeat/future escape options.

## Performance receipt

The race can expose performance humans understand while retaining machine-level measurements.

For every turn, record at least:

```text
contestant
server receive timestamp
resolution duration
identity/realization duration
validation duration
search/selection duration where Laplace chooses
accepted/rejected disposition
canonical entity id when resolved
required initial + orientation before/after
frontier size/work when measured
```

A match summary may report:

```text
valid answers
invalid answers
median / p95 decision latency
total wall time
entities/frontier candidates examined
name-order/alias cheese attempts rejected
rarest terminal initial reached
longest double chain
unique domains/entity classes crossed
```

This is a **behavioral performance proof**, not a replacement for low-level composition, database,
energy or throughput benchmarks. Synthetic and interactive benchmarks answer different questions and
should coexist.

## Same Tetris fairness law

Ranked play pins the same state for every contestant:

```text
world/evidence epoch
eligible entity/type/source boundary
starting entity or ordered starting queue
language/name-realization policy
normal/reverse endpoint rule
double behavior
no-repeat policy
clock/scoring law
server timing law
```

A later ingest or identity repair creates a new challenge generation rather than changing an already
published match.

Practice mode may intentionally use the live installation.

## Post-game facts

Because every move is receipted, the game can expose useful, inspectable tidbits:

- fastest valid human and Laplace responses;
- letters with the smallest/largest eligible frontiers;
- answers that most reduced the opponent's frontier;
- aliases/name-order substitutions that resolved to an already-used entity;
- attempted cultural-order cheese blocked by structured naming roles;
- longest valid chain under the pinned world;
- alternate routes/chains neither contestant used;
- entity classes/domains traversed;
- answers valid in one installation/epoch but absent in another.

These are facts about the named match/world boundary, not universal claims about all possible data.

## Acceptance fixtures

A conforming implementation should include adversarial cases rather than only easy Western names:

- [ ] `Bobby Fischer -> Frank Sinatra -> Sandy Koufax` validates the ordinary rule.
- [ ] submitted display order cannot change the given/family role used for validation.
- [ ] `Uchiha Itachi` / `Itachi Uchiha` resolve to the same entity where identity evidence says so.
- [ ] `Uchiha Itachi -> Uchiha Sasuke` cannot cheese the first-token initial rule.
- [ ] alias/name-order resubmission of one entity fails no-repeat.
- [ ] strict mode handles `Cher` as non-playable rather than inventing a family name.
- [ ] `Malcolm X` follows the declared endpoint policy.
- [ ] particles/titles/suffixes/character names use structured realization, not whitespace position.
- [ ] multi-codepoint/Unicode initials use the declared grapheme/codepoint policy.
- [ ] doubles change orientation only through the selected versioned firmware.
- [ ] alternating and parallel races use server-side timing and identical challenge generations.
- [ ] a human may beat Laplace despite slower mean response time by producing harder frontier states;
      the receipt must make that outcome explainable.
- [ ] all accepted answers append to event physicality without manufacturing permanent PRECEDES
      testimony for game order.

## Non-success

- first-token/last-token string slicing presented as identity;
- separate celebrity/name database disconnected from entity-world semantics;
- alias or cultural name-order change creating a second playable identity;
- ASCII-only initial extraction;
- hardcoded `Uchiha`, `van`, `Jr.`, mononym, title, or fictional-character exceptions as the
  governing algorithm;
- client-side clocks deciding ranked outcomes;
- random/stochastic opponent state giving contestants different semantic boards;
- artificial sleep presented as difficulty;
- accepting a new game-specific search/identity/realization engine when the common Laplace machine
  already owns those operations.

## Non-exhaustive design-space rule

Knowledge Golf, Highway Race, relays, Collision/CTF, Constraint Crossing, Witness Hunt, Graphle,
COMBINE, chess-map hybrids, Choose Your Own Adventure and The Name Game are **proving fixtures**.
They demonstrate that different products can be firmware/programs over common canonical identity,
physicality, evidence, search, realization, effect and witness machinery.

New ideas should normally extend the set of proving surfaces rather than redefine the previous ones.
A later brainstorm is additional design evidence unless an explicit decision supersedes an earlier
contract.

The product should therefore optimize for reusable machine primitives and receipts, not for closing a
finite checklist of game names.