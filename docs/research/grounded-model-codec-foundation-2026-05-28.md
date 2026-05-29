# Grounded substrate foundation — model ingest + T0 (2026-05-28)

> Note: the word "codec" in this file's path is a banned cute-name per
> docs/SUBSTRATE-FOUNDATION.md truth 10 (it implies round-trip *preservation*, which is
> forbidden — see the bit-perfect/Vampire points below). The mechanism is **semantic
> ingest → consensus → fresh synthesis**, not a codec. Filename retained to avoid breaking
> references; the framing is corrected.

**Status:** Consolidated from Anthony's direct teaching across the 2026-05-28 session.
Awaiting his ratification. NOT an edit to user-authored docs beyond what he authorized.
Where a point is his explicit statement it is marked **[ratified]**; where it is my
inference it is marked **[inference — confirm]**. I was confidently wrong twice this
session (claimed "T0 = bits"; treated model weights as stored number-content); both are
corrected below.

## T0 and the tier ladder **[ratified]**

- **T0 is Unicode codepoints — ONLY codepoints.** The fixed 1,114,112, pre-computed once
  into the perf-cache. There is no "bit/byte" T0; that was my conventional-CS pattern-match.
- Everything composes UP the Merkle DAG from codepoints. Anthony's ladder:
  **codepoint → number → pixel → patch → region → image → video** (and text:
  codepoint → grapheme → word → sentence → … ).
  - `255` is a **T1 n-gram** of digit codepoints `['2','5','5']` (RLE folds the two `5`s).
  - a pixel `(255,255,255)` is a **T2 n-gram** of three `255` entities; a patch is an
    n-gram of pixels; a region of patches; an image of regions; a video of images.
- Because everything is content-addressed, **`255` is ONE entity** shared across every
  modality and context (a price `$255`, a pixel channel, an audio sample, a page number).
  Codepoint `5` is the same atom everywhere. **That sharing IS cross-modal reachability** —
  the reason a codepoint-only T0 spans all of digital reality. (The "numeral vs number
  conflation" I cried earlier was wrong; the unification is the invention.)
- π = `[3,.,1,4,…]` — a finite prefix stored as a digit-codepoint trajectory; the infinite
  quantity is *referenced by attestation* ("= π"), never stored. Dedup happens at the
  codepoint atom level (the ~13 digit/punct codepoints) and at the entity level (a repeated
  number/prime dedups to one row). RLE folds runs; incompressible sequences just keep
  run_length 1 — the mechanism doesn't promise compression, it promises faithful
  content-addressed storage.

## Content vs knowledge — and why model weights are NOT content **[ratified]**

- **Seed / text / user numbers are CONTENT** — stored as codepoint n-gram entities
  (above). This is one data class.
- **Model weights are NOT content. They are never stored as entities, and never
  bit-perfect (Vampire mode — drain, discard the bytes).** A model contributes almost no
  content (its tokenizer vocab dedups into existing text entities); it contributes
  **knowledge**.
- **A weight-derived relationship between two entities (King↔Queen) is a Glicko-2 MATCHUP
  observation, not a value.** The weight is the match *outcome*; the model's source-trust is
  the *opponent* strength; `glicko2_update` runs the match. Across every source that plays
  King-vs-Queen, a **consensus rating** emerges (rating/RD/volatility), arena/trust-weighted.
  Truths cluster (agreement → low RD), lies scatter (outlier → high RD, discounted).
- **The substrate stores the consensus, never the raw weight.** `0.00098267354` is one
  match result, meaningless alone. Stamping a scaled weight straight into the rating column
  (`WeightTensorETL.ScaleToRating(weight)` / per-cell magnitude) IS the category error —
  it skips the matchup entirely. The Glicko-2 engine already exists
  (`glicko2_accumulate` aggregate, `glicko2_update`); ingest must FEED it matchup
  observations.
- **Synthesis = regenerate fresh weights FROM the consensus per recipe.** Never a copy.
  Sparse-by-construction. The bytes never live in the substrate at any point.

## Everything maps to tokens — embedding settled, interior tensors OPEN

- An embedding is an address book of token positions = **geometry** → a per-`(entity,
  source=model)` **PROJECTION physicality** (Procrustes-aligned onto the entity's canonical
  frame); `lm_head` → ProjectionOutput. Candidate access. `EMBEDS`/`OUTPUT_PROJECTS` are
  NOT attestation kinds. (`embed_tokens`/`lm_head` are directly token-anchored — cheap,
  real — per docs/SUBSTRATE-FOUNDATION.md.)
- **There are no separately-stored hidden-dim or intermediate-dim entities** — there is no
  blob of opaque dimension rows.
- **OPEN per docs/SUBSTRATE-FOUNDATION.md:** how the interior `d×d` tensors (`Q/K/V/O`,
  `GATES/UP/DOWN`) resolve their cells to token entities *without* re-running the GEMM
  (which is the vocab² blow-up the anchor forbids) is **genuinely unsolved** and must be
  pinned with Anthony. The earlier claim here — that every interior tensor "uniformly"
  maps to token↔token attestations exactly like the embedding and that "nothing is
  unsettled" — overstated an OPEN question as settled. The anchor lists three open items:
  (1) interior `d×d` tensor axis → token-entity resolution, (2) the exact arena/kind
  assignment per interior tensor role, (3) the synthesis "pour facts into the mold"
  algorithm at frontier scale. Do not substitute a confident answer; flag and pin.
- What IS settled: the embedding grounds the model's token-anchored surface, and whatever
  interior resolution is pinned, weight cells become Glicko-2 matchup observations (never
  stored weights). The family table's "object axis = hidden_dim" was an error, but the
  *resolution mechanism* that replaces it is OPEN, not decided.

## Cross-model / dim / vocab consensus = the moat **[ratified]**

Weight-merging needs identical architecture/dim/vocab and averages basis-entangled weights;
Qwen3 and Llama4 can't be merged that way. Entity-space Glicko-2 consensus can — King is
King regardless of any model's dim, tokenizer, or layout — so any models co-consense onto
shared entity anchors via matchups.

## Data-class axis (the thing I kept collapsing) **[ratified]**

Source/data-class decides handling, not the value's surface:
- **substrate / seed** → content entities + attestations.
- **AI model source** → matchup observations (knowledge), no content; weights discarded.
- **user / prompt** → content, prompt-local, no global attestations by default.

## Repo state these imply (NOT yet fixed — do not trust any "done")

- `WeightTensorETL`: `ScaleToRating` + EMBEDS/per-cell-magnitude + flat `RetainRatio` = the
  category error. Must feed weight-relationships as Glicko-2 matchups to `glicko2_update`,
  not stamp scaled weights as ratings. NOT done.
- ADR 0056 family table (per-cell magnitude) → killed; ADR 0040/0044 `EMBEDS`/
  `OUTPUT_PROJECTS`-as-kind → vestigial.
- The 7-agent audit's "Universal-T0 conflation" findings are INVALID — I poisoned the
  yardstick with my wrong "T0 = bits" reading; the docs' codepoint-T0 is correct.

## Done criterion

Ratified against this by Anthony — not against "compiles / CI green / checkboxes."
