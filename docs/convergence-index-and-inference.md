# The convergence index — the queryable backbone of recall, inference, and generation

> Status: architectural model (the design thesis). Parts are **target, not current state** — the
> "current corruption" section is explicit about what does not yet hold. Treat the model as the spec
> the decomposer/identity work (`iridescent-cooking-waterfall`, WS3) is converging on; verify any
> behavioral claim against the code before quoting it.

## Thesis

The canonical referents a corpus carries — **ILI** (meaning/synset), **UPOS** (part of speech),
**ISO-639** (language), **FrameNet frames**, **VerbNet classes**, **PropBank rolesets** — are not
metadata bolted onto content. They are a **convergence index**: content-addressed nodes that every
source's equivalent referent dedups onto. Because identity is `blake3(content)`, **the index is not a
side structure — identity *is* the index**: equal meaning → equal address → same node, so "look it
up" and "have found it" are one operation. A SQL index points at rows; here the address *is* the row.

Once that index is real, every operation people associate with a transformer becomes a **plane-weighted
traversal of the consensus field over it** — an *indexed read*, not learned compute. Recall, multi-hop
reasoning, translation, generation, and GGUF export are **one mechanism** differing only in which
relation-rank planes they up-weight and where they enter and exit the graph.

## What the index is

Each backbone is a key-space that many sources converge onto:

| Backbone | Canonical id | Converges |
|---|---|---|
| **ILI** | `iN` (Interlingual Index, registry CILI) | WordNet/OMW/Wiktionary/ConceptNet synsets, all languages & versions |
| **UPOS** | the universal POS tag | WordNet `n/v/a/r`, PTB, language-specific XPOS |
| **ISO-639** | the language code | every source's language field |
| **FrameNet frame** | frame name | FrameNet + FN→WN/VN bridges |
| **VerbNet class / PropBank roleset** | class id / roleset id | VerbNet, PropBank, SemLink, PredicateMatrix |

There is not one highway but several, and the **bridge sources are the interchanges between them**:
SemLink / PredicateMatrix / MapNet exist only to connect `VN-class ↔ FN-frame ↔ WN-synset ↔
PB-roleset`. Getting a bridge's anchor right is paving an interchange; getting it wrong (see
"current corruption") is a missing on-ramp.

## Two axes — do not conflate them

A concept node sits on **two** orthogonal axes. Collapsing them into one is the recurring sin
(`EntityTier.Vocabulary = 5` jammed kind into depth).

| | What it is | Axis | Direction | Gives you |
|---|---|---|---|---|
| **Compositional** | `synset = merkle(member senses)`, `sense = compose(lemma ⊕ disambiguator)` | **tier** (depth = radius) | bottom-up build | geometry, trajectory, Hilbert locality — the *index structure* |
| **Semantic / referential** | `ILI ←lexicalized-by→ "noun"/"Substantiv"`, `dog IS_A canine`, `dog HAS_POS NOUN` | **attestation** (the typed FK) | top-down query | the *paths* you traverse |

Hypernymy proves the split: `dog` and `animal` are the **same tier**; `IS_A` is pure attestation, not a
tier step. The compositional ladder (`lemma → sense → synset → ILI`) is what gives the concept layer
real `coord` / `radius` / `trajectory` / Hilbert index; the attestation edges are the roads between
those indexed nodes. Referential integrity lives in the typed link — `(subject, type, object)` is exact
regardless of node sharing (the ghost-reference law: every referenced id must resolve to a real entity).

## Why it is queryable

When a concept is a real composed node (WS3) rather than a string-walk of its key, the full index
machinery applies to the **concept layer**, not just to text:

- **Tier / altitude** — filter a walk by tier to query at *concept* altitude instead of drowning in
  word-tier scaffolding (`PRECEDES`/`HAS_POS`/codepoints). This is the gold-path read.
- **Form (geometry)** — `coord` KNN (nd-GiST) and Hilbert range give structural neighbors *of concepts*
  (`structural_neighbors`); `radius` is compositional depth.
- **Constituents / citation** — a synset's `trajectory` packs its member-sense ids losslessly, so
  `trajectory_constituents` walks *into* it; a document/game trajectory packs its parts = citation, in-DAG.
- **Meaning (consensus)** — walk the attestation FKs ranked by `eff_mu = rating − 2·rd × relation rank`:
  `isa_path`, `relate_path`, `translate_to`, `define`, `synonyms`, surfaced through `recall`.

Today none of this is meaningful on concept nodes, because their identity is `blake3("i90107")`: the
geometry is the geometry of the *string* "i90107", `trajectory_constituents` yields the codepoints
`i 9 0 1 0 7`, and the name needs `label()` to reverse-resolve. The index exists but its entries are
unreadable and mis-placed.

## One field, many readings

Every "inference" is the same traversal under a different plane weighting of the relation-rank bands
(`engine/manifest/relation_types.toml [ranks]`):

| Operation | Up-weights | Enters / exits |
|---|---|---|
| **Recall / define** | definitional 0.97, taxonomic 0.90 | a word → its ILI concept → gloss/hypernyms |
| **Reasoning** | taxonomic / causal `IS_A`, `CAUSES` | concept → climb the ladder |
| **Translation** | `IS_TRANSLATION_OF`, ILI convergence | lang-A lemma → ILI → lang-B lemma |
| **Generation** | sequential / syntactic `PRECEDES`, deprel | concept backbone + word-order planes |
| **Export (foundry)** | the consensus planes as tensors | reads the whole index into GGUF |

The plane weights are tunable per task; the *graph* is the one converged field. This is why "queryable
against the substrate" and "part of inference/generation" are the same statement: the index is what you
query, and querying it under a plane weighting **is** the forward pass. Export just freezes that forward
pass into llama-arch tensors (embed = concept geometry, each head = traversal along one relation type,
lm_head = readout) — see `model-extraction-philosophy`, `foundry-synthesis-findings`.

## Why convergence is the denoiser

Because every source's equivalent referent lands on the *same* node, attestations **reinforce** there:
ConceptNet's "a dog is a mammal" and WordNet's `dog → canine → … → mammal` accumulate Glicko games on
the same edges, and the consensus fold denoises in place (online, in `laplace_apply_batch`). The index
also **de-biases**: source-trust and Elo-style weighting let high-authority evidence dominate the fold
(the chess corpus's "Scholar's Mate" collapses once weighted by defender strength; the same shape holds
for knowledge — a popular-but-wrong assertion loses to weighted corroboration). The highways are what
make the field dense and clean enough to pour a model from.

## Current corruption (the gap WS3 closes)

The index is real in design but **corrupt in the code today**:

1. **Opaque keys.** Concept identity is a string-walk: `ConceptAnchor.EmitAnchor` →
   `ContentEmitter.Emit("i90107")`; `SenseAnchor`/`CategoryAnchor` walk `"dog%1:05:00"` / frame keys
   flat. The node can't describe itself; `label()` (`20_converse.sql.in`) carries an 8-deep regexp
   tower whose job is to *detect and suppress* these keys and reverse-walk `HAS_DEFINITION`/
   `HAS_NAME_ALIAS`. That regexp tower is the tax of an index whose keys are unreadable.
2. **Missing entries / highway gaps.** ILI resolution is an external **file lookup that returns null on
   miss** (`SourceEntityIdConventions.WordNetIli` → `IliMap.Resolve`). A miss drops the synset edge
   silently — no record, no conflict. A whole source can ingest "successfully" with every synset bridge
   gone (`WarnIfCiliMapMissing` logs, then proceeds). Convergence then depends on a file's coverage.
3. **Format-coincidence interchanges.** `CategoryAnchor.Normalize` notes frame/class/roleset keys
   "all happen to agree on the same surface convention today — but there is no canonical lookup table
   backing that agreement, only convention." One format drift = a silent fork at the interchange.
4. **No `noun → ILI`.** `PosReference` resolves `NOUN` to a UPOS-tag hash whose only meaning-bearing
   edge is `HAS_NAME_ALIAS → "noun"`. It has no `IS_A` supertype and no link to the ILI concept
   `noun.n.01`, so `dog —HAS_POS→ NOUN` lands on an island, not on the concept's tree.

**WS3 paves the index:** make each ILI/frame/roleset node a real, self-describing, *composed*
convergence node that CILI/the source **always** emits, resolved deterministically (no file gamble),
with surface lemmas and POS/category tags **linked into** it (`HAS_SENSE`, `CORRESPONDS_TO`, lexicalized-by).
Then the concept layer is the queryable lookup described above, the highways have no holes, and recall /
reasoning / translation / generation / export read it as exact indexed traversals. This is the
**meta-node identity keystone** — still the one OPEN decision everything here hangs on.

## See also

- Memories: `convergence-index-the-backbone`, `vocabulary-is-content-not-anchors` (the two axes +
  identity), `laplace-convergence-architecture` (hash/dedup/native engine), `laplace-4d-geometry-architecture`
  (form geometry), `model-extraction-philosophy` + `foundry-synthesis-findings` (export).
- Plan: `iridescent-cooking-waterfall` (WS3 compositional synsets/ILI; WS4 POS supertype; WS7 walk).
- `docs/refounding-vocabulary-on-content-addresses.md` (tier/kind/identity model).
