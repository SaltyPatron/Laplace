# W15 — The fan-out axes: what an election is allowed to rank on

**Issues:** #861 (elector), #865 (prior vs fold), #864 (naming detection), #866
(attention centroid) · **Plan:** Phase 3 · **Blocks:** any honest claim about
answer quality

> **Evidence discipline.** Every row below is marked **[M]** measured on this
> substrate with the command that produced it, **[S]** structural — true from
> the schema or the manifest, or **[C]** conjecture. `docs/plan/README.md`
> records that every claim in this directory that later turned out false was "a
> plausible claim written by someone who had not run the measurement." The [C]
> rows are the ones that will bite.

---

## 0. Why a scalar cannot work

Six ranking keys have been tried in this repo. Each failed, and each failed the
same way — **it had a population that is extreme on that axis for reasons
unrelated to being the topic**:

| key | degenerate class | evidence |
|---|---|---|
| `denote_mu` | function words | [M] every sense ties near 1169.73 on a foundation seed |
| `coherence / total_mass` | everything — it is 0 without a direct edge | [M] 0 on every token of `hot`, `dog`, `Water is made of` |
| `ord DESC` | SOV/VSO languages, chess, images | [S] an SVO word-order assumption |
| highway popcount | the tier-0 floor | [M] `a` scores 19 bands — highest in the probe set |
| `total_mass` | high-degree ids | [M] elects `chess` over `pawn` |
| container IDF | rare words | [M] elects `opposite` (~517 containers) over `hot` (~1050) |

There is no seventh scalar without a degenerate class, because a scalar
projects a multi-axis structure onto one dimension and every projection has a
null space.

**The substrate already solves this one level down.** Glicko does not trust a
loud witness; it weighs many independent ones and carries `rd` for how much
that agreement is worth. Nothing applies the same principle one level up, to
candidates. That is the whole of this workstream: **an election is convergence
across independent evidence classes, not an extremum on one axis.**

---

## 1. The axis inventory

### A. Composition — the tier ladder

Each tier carries **its own attestations** about its own content. A codepoint's
testimony ("LATIN SMALL LETTER A") and a word's testimony (`dog IS_A noun`) are
different claims about different entities that happen to share an id when the
content is one codepoint long.

| # | axis | read | status |
|---|---|---|---|
| 1 | `tier` | `entity_tier_of`, `entity_facets` | [M] Codepoint 1,114,112 · Word 1,314,696 · Sentence 1,871,278 · Document 22,104 |
| 2 | constituents (down) | `constituents`, `constituents_closure` | [S] ordinal, child_id, run_length, flags; `vertex_tier(flags)` decodes per-vertex tier |
| 3 | containers (up) | `containers_of`, `entity_container_degree` | [M] 1-hop cap 20000: `a`/`the`/`of` saturate · water 4629 · hot 1040 · glacier 83 · pawn 23 · france 16 |
| 4 | `n_constituents` | `physicalities` column | [S] compositional width, stored |

**Trap, and it cost this session an hour.** An atom has no trajectory *of its
own* (`entity_has_trajectory(word_id('a')) = f`) but participates in an enormous
number of them. **Possession is not participation.** Reading the false flag as
"stop word" is backwards — `a` is the floor, which is why it is in everything.

### B. Adjudicated relational testimony — the fold

Every one of these is already per-edge in `consensus`. None of them reach the
elector's ranking key today.

| # | axis | read | status |
|---|---|---|---|
| 5 | relation type | `relation_canonical`, manifest | [M] 233 canonical names |
| 6 | band | `relation_highway_band`, `relation_band_catalog` | [M] 13 bands, rank 1.0 `mandate` → 0.05 `probationary` |
| 7 | direction | `consensus_out` / `consensus_in` | [S] two indexed reads; an `OR` is unservable (spec 37 OP5) |
| 8 | `rating` | consensus column | [S] Glicko-2 μ |
| 9 | `rd` | consensus column | [S] **trust**; `eff_mu = rating − 2·rd` |
| 10 | `volatility` | consensus column | [S] σ |
| 11 | `witness_count` | consensus column | [S] saturating via `foundry_witness_sat` |
| 12 | observation counts | `attestations` | [S] re-ingest doubles by design; a source needs a marker guard |
| 13 | **source** | `attestations.source_id` | [M] 11 sources seeded; `multi_source_entity_count()` exists |
| 14 | `context_id` | `attestations` | [S] the witness boundary (playing, sentence) |
| 15 | outcome | `entity_record` | [S] confirmed / contested / refuted / **thin** — and an unattested id is *not* an id attested false |

**#13 is the most under-used axis in the system.** Two sources independently
asserting the same edge is qualitatively different from one source asserting it
twice, and the fold already distinguishes them — but no election reads it.

### C. Semantic locality

| # | axis | read | status |
|---|---|---|---|
| 16 | synset | `HAS_SENSE`, `IS_SENSE_OF` | [M] 213,556 HAS_SENSE rows |
| 17 | **ILI** | CILI lane | [M] 1,399,570 evidence — language-independent id |
| 18 | language | ISO639, `word_language` | [M] 8,153 Language entities |
| 19 | POS | `HAS_POS` | [M] `in` carries 4, `glacier` 1, `france` 0 |
| 20 | frame | `EVOKES_FRAME`, `IS_FRAME_OF` | [M] 681,982 FrameNet evidence; `dog` evokes 2 |
| 21 | deprel | `DEP_*` / `EDEP_*` families | [M] **absent** — UD not seeded on this box |
| 22 | lemma | `IS_LEMMA_OF` | [S] manifest marks it HOT, ~2.3M rows, load-bearing on the S1/S2 hop |
| 23 | translation | `IS_TRANSLATION_OF` | [M] **0 rows** before 2026-08-04; OMW ingest landing now |
| 24 | attention centroid | `laplace_attention_centroid` | [M] returned NULL on **every call ever** until #866 |

### D. Geometry

| # | axis | read | status |
|---|---|---|---|
| 25 | `coord` on S³ | `entity_physicality_coords` | [S] derived from **content** — composition of codepoints |
| 26 | `hilbert_index` | `entity_hilbert_keys` | [S] locality-preserving 1-D address; anagram equality *is* hilbert equality |
| 27 | `radius_origin` | generated column | [S] |
| 28 | angular / geodesic | `coord <<->>` KNN | [S] bound parameters only, or the planner loses the index |
| 29 | trajectory | `physicalities.trajectory` | [S] the ordered sequence, losslessly |
| 30 | Fréchet | `trajectory_prefix_distance`, `chess_opening_shape_peers` | [S] sequence-to-sequence similarity |

**The trap that looks most like a good idea.** "Do `dog` and its translations
cluster geometrically?" **No, and they cannot** — [S] a coordinate is a function
of content, so `dog` and `perro` are unrelated points by construction. Raw
cosine over coordinates is *form* similarity. What converges cross-lingually is
the **band-masked attention centroid** (#24) and the **ILI** (#17). Ranking
meaning on #25–#28 directly is a category error.

### E. Routing

| # | axis | read | status |
|---|---|---|---|
| 31 | highway mask | `entity_highway_masks`, `laplace_highway_match` | [M] 32 bytes/entity, `ready=t`, dirty queue 0, agrees with consensus |

This is the MoE router and it is **O(1)**. Any "which bands does this entity
participate in" question that reaches `consensus` is a defect — #866 was
exactly that, scanning 4.34M entities to compute something the manifest and a
bitmask already answer.

### F. Sequence — unadjudicated by ruling

| # | axis | read | status |
|---|---|---|---|
| 32 | order | `PRECEDES`, `word_order`, `sentence_order` | [S] |
| 33 | co-occurrence | `cooccurrence_scan`, `trajectory_cooccurrence` | [S] |
| 34 | weighting | COUNT / CONDITIONAL / GAP_DISCOUNTED / ASSOCIATION | [S] spec 37 §5 — counted, never folded |

---

## 2. Independence classes — the part that actually matters

Enumerating 34 axes is not the design. **Grouping them by what they are
independent of** is, because agreement only carries information between
independent witnesses. Several axes are derived from others and must not be
counted twice: `radius` and `hilbert` from `coord`; `eff_mu` from `rating` and
`rd`; `specificity` from `coherence` and `total_mass`.

| class | axes | independent of |
|---|---|---|
| **1 — adjudicated testimony** | 5–15 | frequency, position, form |
| **2 — compositional position** | 1–4 | who attested it |
| **3 — semantic locality** | 16–24, 31 | how often it occurs |
| **4 — sequence** | 32–34 | the relational graph entirely (spec 37 §5) |
| **5 — form** | 25–30 | meaning — **excluded from semantic ranking** |

**The rule this workstream proposes:** an election ranks on **agreement across
classes 1–3**. Class 4 breaks ties. Class 5 never ranks meaning; it serves
identity, ordering and KNN retrieval.

### Why that fixes the measured failures

| failure | class 2 | class 1 | class 3 | outcome |
|---|---|---|---|---|
| `a` beats `glacier` | kills `a` — containers saturate [M] | `a`'s 7 HAS_SENSE edges include a **5-way exact tie** at 994.8 [M, W4 §2.2] — near-zero information | `a`'s senses share no centroid [C] | 2–3 classes agree against 1 |
| `opposite` beats `hot` | favours `opposite` (rarer) [M] | `hot` has more witnessed mass from more sources [C] | `opposite` names the relation; `hot` fills it [C, #864] | class 2 outvoted 2–1 |
| `chess` beats `pawn` | — | — | frame/POS separate them [C] | needs measurement |
| `water` → `urine` | no effect — same token | source count per sense [C] | sense centroid vs prompt centroid [C] | **sense, not topic — different defect** |

The `[C]` density in that table is the honest state: the *diagnosis* is
measured, the *remedy* is not. Nothing here should be written into an elector
until each `[C]` is a `[M]`.

---

## 3. The binding constraint

All of it must execute in **id space**: a bitmask test, an id-keyed index
probe, a scalar already on the row, or a bounded batched probe. Never text,
never a scan, never a per-row set-returning function.

That is what ILI, synsets and frames are *for* — they collapse a semantic
question to id equality. Spec 37 L1 already forbids rendering before S8; this
is the same law stated positively: **if an election step needs a string, the
wrong structure is being read.**

---

## 4. Acceptance

1. Every `[C]` above is measured before it informs a ranking key.
2. No single axis is load-bearing; removing any one must not collapse the election.
3. No positional or language-specific key anywhere in the five elector sites.
4. Beats **both** recorded results — 5/6 (`ord DESC`) and 4/6 (IDF alone) — on the six probes **and** on held-out prompts.
5. Cross-lingual convergence re-tested once OMW lands `IS_TRANSLATION_OF` (was [M] 0 rows).
6. Sense election tracked separately from topic election. `water → urine` is not a topic defect and must not be scored as one.
