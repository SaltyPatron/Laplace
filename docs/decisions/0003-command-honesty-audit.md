# Command honesty audit — what the shapes claim vs. what they do

Recorded 2026-08-10 against the live endpoint (`hart-server:8080`) while the
substrate was mid-seed. Every number below was measured, not inferred.

## CORRECTION (same session)

An earlier version of this document claimed "the sequential layer is empty, so
nothing can do a forward pass," built on the four row counts below. **That
conclusion was wrong and is retracted.**

`attends` and `completes_to` are **model-ingestion** relations — they carry
attention/continuation structure read out of an ingested transformer. No models
have been ingested (`modalities.models: 0`), so 0 rows is the expected and
correct state and says nothing about the document path. Lumping them together
with `precedes` into one invented "sequential layer," then blaming the document
decomposer, conflated two unrelated ingestion paths.

The row counts themselves are accurate for those four relation types. The
interpretation hung on them was not supported by the data. The command-behaviour
findings further below were observed directly from the API and do not depend on
this section.

## Row inventory (accurate; interpretation retracted above)

Estimated rows in `laplace.consensus_r_*` (summed across the `_h0`–`_h7` shards):

| relation | rows | what it is |
| --- | ---: | --- |
| `rdefault` | 3,399,293 | everything not given its own table |
| `has_synset_key` | ~1,000,000 | WordNet synset binding |
| `has_pos` | ~360,000 | part of speech |
| `has_language` | ~350,000 | ISO language axis |
| `has_sense` | ~215,000 | surface → sense |
| `is_synonym_of` / `is_sense_of` | ~207,000 each | lexical equivalence |
| **`precedes`** | **91** | **token-to-token trajectory** |
| **`appears_in`** | **10** | **occurrence** |
| **`attends`** | **0** | **the attention analogue** |
| **`completes_to`** | **0** | **continuation / next-token** |

`follows` and `co_occurs_with` have no tables at all.

`follows` and `co_occurs_with` have no dedicated tables; rows for any relation
type without its own LIST partition land in `consensus_rdefault` /
`attestations_rdefault`, so absence of a table is not absence of data. See
`partition-and-ladder-findings.md` for what is actually in the catch-alls.

## The actual defect: how each command behaves when its layer is empty

The failure is not that the layer is empty. It is that **only two commands admit
it.** The rest silently substitute a different layer and present the result as
if it were the thing asked for.

### Honest — reads what is there, says so when it isn't

| shape | behaviour |
| --- | --- |
| `complete` | `I hold "whale" but no outgoing consensus to walk yet.` — **the correct model.** `completes_to` is 0 rows and it says exactly that. |
| `path` | `path needs a second topic.` |
| `fallback` | returns glosses, and is named "fallback". |
| `examples` | `In the summer they like to go out and whale` — a real witnessed usage, μ 1124, 1 witness. |
| `define` / `what_is` / `describe` / `synonyms` / `is_a` / `languages` / `related` / `band_facts` | read the lexical layer and carry μ + witness counts. They are lookups and do not claim otherwise. |

### Pretending — claims behaviour it cannot perform, and fabricates from another layer

**`generate`** — the worst offender. On `whale` it returned:

    FULL STOP · RIGHT DOUBLE QUOTATION MARK · EM DASH · _Miriam

Those are **Unicode codepoint names** — L0 floor atoms — emitted as if they were
generated tokens. No μ, no witnesses. On `gravity` it returned **zero rows**.
It is reading the floor and calling it generation.

**`walk`** — summary claims *"steered seed-responsive generation over witnessed
text"*. Actual output:

    whale —member of verbnet class→ berry-13.7 —corresponds to→ log.03
          —has definition→ do the job of cutting trees into logs

A greedy strongest-edge hop through dictionary structure, rendered as prose with
arrows. Nothing steered it; nothing responded to a seed; no witnessed text was
walked. "whale" arrives at "cutting trees into logs".

**`beam`** — claims beam search. Enumerates one-hop edges by μ. The reported μ
*rises* along the chain (1168 → 1458 → 1172), so no path score is being carried
— it is not a beam.

**`neighbors`** — presents S³ geometry as a semantic neighbourhood. It is not
one:

- `whale` → `lawhe`, `wheal`, `wealh` — **character permutations of the same
  letters**, geodesic ~0.
- `gravity` → `genus euplectella`, `genus Heteroscelus`, `genus tortrix`,
  `false gromwell`, `genus Anthyllis` — unrelated biological genera.

The geometry is over **form**, not meaning (capabilities do distinguish
`embed-form` from `embed-meaning`, but the shape does not say which space it is
reporting). Presented unlabelled, this reads as "these are the nearest concepts",
which is false.

## Topic resolution: two different wrong answers, neither sequential

A multi-token prompt is not processed as a sequence by either path:

| path | input | what happened |
| --- | --- | --- |
| `/v1/chat/completions` | "What about whales?" | resolved the single token **"About"**, returned its WordNet gloss |
| `/v1/chat/completions` | "Why?" (turn 2 of a gravity session) | resolved **"why"**, returned its gloss |
| `/v1/query` | "What about whales" | hashed the whole literal string into a content-addressed entity that holds nothing → `no glosses have been witnessed` |

Chat picks one token and glosses it. Query hashes the raw phrase. Neither scans
`precedes`, neither uses the session, neither goes token to token.

## What honest degradation looks like

`complete` already does it: name the relation you needed, say it has no
consensus yet, return nothing. Every command above should degrade the same way —
*"`generate` needs `completes_to`/`precedes`; 0 rows witnessed for this topic"* —
rather than substituting codepoint names, dictionary hops, or form-space
proximity and presenting them as inference.

That change is cheap and makes the system's honesty a feature rather than
something the seeding state can quietly undermine.

## Related

- Sense ranking picks the wrong sense even where the right one is witnessed:
  `whale` → *Hulk*; `dog`'s IS_A ladder renders *unpleasant woman → disagreeable
  person* although `dog —is a→ domestic animal —is a→ animal` **is** witnessed.
  Same root: resolution, not storage.
- `/v1/completions` (free-associate) 503s at 30 s — the one path that claims
  generation over witnessed text cannot complete a request.
