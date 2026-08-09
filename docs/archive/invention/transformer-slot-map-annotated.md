> Archived annotated slot campaign. Status rows are historical.

# The transformer slot map — deterministic slots over the substrate

Binding. This is the component-level completion of INVENTION.md §10 and the
COMPLETION_PLAN §2 "Learning" frame (GH #823): every named tensor slot of a
transformer has exactly one substrate counterpart, readable live. Where this
document and the running system disagree, the system is right and this
document is the thing to fix.

## The governing principle: pour follows query

**A model cannot be poured from a substrate that cannot be queried.** The
GGUF artifact is a render of the read surface — every slot completed live
becomes a pourable tensor by transcription, and a slot that cannot be
queried live was never pourable at all, only forgeable. Layers and heads are
built by making the substrate answer layer-shaped and head-shaped questions;
the foundry transcribes. Consequence: the conversational campaign IS the
pour campaign, run in the only order that works.

## The slot table

| Transformer slot | Substrate counterpart | Plane |
|---|---|---|
| **Embed** | S³ placement + Hilbert key — anchored by law, never learned; eigenmap basis generated only at pour | geometry |
| **Hidden dim** | live: unbounded id space (hash space); rank-d exists only at export | — |
| **Q** (query formation) | the election — the turn's frontier construction | consensus reads |
| **K** | arena keys: (subject, type) indexes; positional keys are trajectory ordinals | consensus + trajectory |
| **V** | consensus objects with their full rating tuples — `eff_mu` is the value magnitude | consensus |
| **O** (output projection) | steer-merge + share normalization folding heads back into the stream | native walk |
| **Gate** | highway-mask bits — which relation families fire for this entity | highway plane |
| **Up** | expansion into the relation arenas — one head per band/family | consensus |
| **Down** | the weighted fold back: `walk_edge_weight`, RD decay, witness saturation | native |
| **Norms** | share normalization (÷ own total mass); RD as per-feature scale | fold |
| **LM Head** | `realize` / `realize_batch` over the final distribution; at pour, the conditional-table factorization | realization |
| **Positional (RoPE analog)** | **the physicality trajectory** — order lives in geometry, losslessly | trajectory |

## The trajectory law (restated as this table's rule)

CONTAINS / PRECEDES / FOLLOWS / co-occurrence / PART-OF are **extrapolations
over the physicality trajectory and containment, computed at read time** —
never materialized edges. The system paid for this lesson once: 785,637
redundant word-adjacency PRECEDES rows carrying 4.9M observations were
deleted because the geometry already held the same fact losslessly, and the
read path migrated (`collocates`, `usage_overlap`, `geometry_successors`,
`trajectory_continuations`) onto trajectory constituents. Any slice that
reaches for a new materialized sequence edge is wrong by construction.
(PRECEDES the RELATION remains a model/feedback-lane edge — token couplings
and witnessed turn chains — never text word-adjacency.)

## Slot status — the campaign checklist

A conversational slice names the slot it completes. Filled means queryable
live with its behavior pinned by the deploy-printed detectors.

| Slot | Status |
|---|---|
| Embed / Positional | **Filled** (geometry + trajectory; concept-level stream in flight makes the stream language-free — GH #751) |
| K / V / Up | **Filled** (consensus arenas, band-partitioned) |
| Gate | **Filled** (highway masks; gating used by walk_branches) |
| O / Norms | **Filled** (steer-merge + share normalization, seed-responsive since #884) |
| LM Head | **Filled** (realize family; nameable-only candidates since the hygiene fix) |
| Softmax-with-temperature | **Filled** (Gumbel over steered weights) |
| **Q — query formation** | **OPEN**: the election works but its mixing weights and query construction are hand-set constants. Completion = retrieval-as-player (Phase 5): the feedback lane folds operator configurations as rated players, so the fold tunes its own retrieval. |
| **Attention over context** | **OPEN**: discourse readback (R7 / #759) — the context window is biography; orientation must read the witnessed turn history, not one carried topic id. |
| **Routing (which arena)** | **OPEN**: questions routing themselves (#756) — attested relation vocabulary wired into the election's rel_type_id and infer's typed read. |
| **Depth (layer stack)** | **OPEN**: multi-step TRAVERSE with WITNESS between steps (#757's port payload) — step N's emission conditions step N+1's election. |

## Vocabulary is a ladder; similarity is three declared planes

**The tokenizer is tier 0 and only tier 0** — Unicode codepoints, universal
across modalities since the codepoint-floor ruling (media reduces to
digit-composed numbers). Everything above is a COMPOSED vocabulary that
stays first-class at every altitude: codepoint, word, sentence, document —
position, game — sample, window, track. There is no single token list;
there is a ladder, and a conventional transformer's word/subword vocabulary
is one *projection* of it, chosen at pour time by the vocab crawl exactly
the way language is chosen at realize time. A codepoint-granularity pour is
equally lawful (it is what byte-level models are); emission granularity in
the live engine is likewise a per-step choice — realize a word, a clause,
or a whole composed entity. Multi-granular attention and emission are
capabilities the flat-vocab paradigm cannot express; the Embed slot above
is therefore PER-TIER, and containment gives the cross-altitude edges.

**Any attention-shaped score must declare its comparison plane.** There are
three, and they answer different questions (the dual-engine law):

| Plane | Instrument | Answers | Bounded by |
|---|---|---|---|
| Relational | consensus `eff_mu` | what testimony binds (whale–ship) | sign from rating; magnitude from RD/witnesses |
| Angular | S³ coord / Hilbert key | same constituent multiset, any order (whale–while; anagrams) | NOT identity above tier 0 — candidate filter only |
| Curve | Fréchet / Hausdorff over REALIZED trajectories (entity_curve / word_curve) | same order and shape | must realize from live coords; stored trajectory is identity cargo, not positions |

A head that mixes planes without declaring it — or concludes identity from
an angular collision above tier 0 — is the cosine-as-meaning defect
reintroduced in this architecture's own vocabulary. Heads are typed by
plane: meaning heads read relational, form heads read angular/curve, and
their disagreement is generative (structurally near + relationally silent =
hypothesis candidate — the system's own reading list).

## Order

Concept streams (#751) → discourse readback (#759) → routing (#756) →
depth (#757) → retrieval-as-player. Each unlocks the next's measurability;
the eval harness gains a probe class before each wires. The usage-layer
seeds (Tatoeba / OpenSubtitles, post decomposer-rework) are the data lever
running in parallel — generation feeds on usage, and the substrate is
knowledge-heavy, usage-light today.
