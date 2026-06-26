# Vocabulary, tiers, and the meta↔content boundary

Status: **model corrected; implementation NOT started.** An earlier draft of this file proposed
"vocabulary IS content (content-root of its name)". That is **rejected** — it conflates meta into
content (see §"Rejected"). This file records the model that actually holds.

## The axes (orthogonal — do not collapse into `tier`)

| axis | meaning | who reads it |
|---|---|---|
| **tier** | compositional depth = geometric radius. codepoints on the S³ surface (0), composition falls inward `max(child)+1`. EMERGENT, never a category, not a fixed 0–4. | geometry, dedup, BRIN |
| **physicality** | CONTENT / BUILDING_BLOCK / PROJECTION (`pk_*` metas) | structural role |
| **type_id** | meta-type: Word, POS, RelationType, Language, Synset, `grammar/{modality}/{node}` | "what kind is it" |
| **trust class / source** | provenance layer: Mandate(app/meta) · Academic/Corpus(seed) · UserPrompt/Response(user) · Adversarial | meta vs substrate vs user |
| **attested vs not** | has consensus relations or not | reads |

"App/meta vs substrate vs user" is answered by **type_id + physicality + trust/source** — never by
tier, never by merging into the content node. The two sins being fixed are both axis-conflations:
the fabricated `Vocabulary = 5` tier (KIND jammed into DEPTH), and the path-hash **dead-leaf** anchor
(meta severed from content). Neither is fixed by making vocabulary into content.

## Tiers are per-modality, grammar-fed

`tier` is whatever depth the modality's segmentation grammar composes to. UAX29 is only the *text*
grammar (codepoint→grapheme→word→sentence→doc). `grammar_compose(modality_id, AST)` folds any AST
`tier = max(child)+1`, typed `substrate/type/grammar/{modality}/{node}`:

- **code** — the language's tree-sitter grammar (hundreds exist). token→expr→stmt→block→fn→module.
- **chess (PGN)** — a position is content composed from its canonical surface (emergent tier); the
  move/game structure is **attestation edges** (`state —move→ state`, scored by result), not deeper
  tiers (`TurnSubstrate`).
- **AV** (conceptual) — perceptual segmentation: sample/pixel→frame→segment/region→phrase/object→clip.
- **SCADA** (conceptual) — protocol + event/time windowing: reading→transaction→event→episode→session.

Within-unit nesting → tiers. Between-unit relations → attestations (the universal weight layer:
Q/K/V/O/gate/up/down generalized to hundreds of Glicko-weighted relation types, stacked across tiers;
the foundry exports them as GGUF weights).

## The meta↔content link (the richness that's missing today)

Meta vocabulary nodes are **distinct** (typed meta) and must be **linked into content**:

```
dog —HAS_POS→ POS:NOUN(meta)         <- distinct, language-neutral, mandate trust
POS:NOUN ←realized/labeled by— "noun"(en), "Substantiv"(de), "nom"(fr) …   <- real tier-2 content
"noun"(en) —HAS_DEFINITION→ "a word that…"  —IS_A→ part-of-speech …
```

A query traverses `dog → POS → "noun" → translations → definition`. Today `PosReference` emits the
POS as a `Vocabulary`-tier path-hash **dead leaf** — no link to the word "noun", no translations.
That impoverishment, plus the fabricated tier, is the actual damage.

Referential integrity lives in the **typed links**: `(subject, type, object)` is exact regardless of
node sharing. `merkle_dedup` stores content once and records occurrences as attestations — nothing is
hidden, nothing flattened, as long as the links are never merged or collapsed.

## Rejected: content-root unification

Making `IS_A`/`NOUN`/`eng` *be* `content_root(name)` puts them on the manifold but **flattens** them
into every literal text occurrence of that string — dedup makes the relation-type the same node as a
random token, and `attestation.type_id` (millions of edges) loses its distinct, stable referent.
Distinctness is not optional; it is carried by type_id/physicality/trust, and the node stays separate
from its name-as-text.

## OPEN — decide before any edit

The exact **identity** of a meta node (content-addressed-but-distinct from the bare text node, vs a
namespaced scheme, vs other) and its physicality/tier treatment are unresolved and are Anthony's
call. No reseed-class change until that is pinned.
