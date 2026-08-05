# W16 — The operation algebra: every calculation the substrate performs

**Related:** spec 37 OP4 (one weight, four modes) · W15 (fan-out axes) · G1/G6
gates · #860 · #866 · **Phase:** 1/6

> **Evidence discipline.** **[M]** measured, **[S]** structural — true from the
> schema, manifest or a function body, **[C]** conjecture. The `[C]` rows are
> the ones that will bite; `docs/plan/README.md` records that every claim in
> this directory that turned out false was a plausible one nobody had measured.

---

## 0. Why audit by form and not by name

Spec 37 found six ways to weight an edge and folded them to one function with
four modes. That is the right move made one level too high: **the four modes
are not four algebras.** They are one log-linear form with different
coefficients, plus two different — and mutually inconsistent — treatments of
uncertainty.

Auditing by function name finds duplicate *bodies*. Auditing by algebraic form
finds duplicate *mathematics*, which is where the disagreements live.

---

## 1. The domain — every value an operation may read

| group | values | type |
|---|---|---|
| fold | `rating`, `rd`, `volatility` | int64, fixed point 1e9 |
| evidence | `witness_count`, observation counts, source count | int64 |
| relation | `rank` ∈ [0.05, 1.0], `band` ∈ [0,12], highway bit ∈ [0,255] | double / int |
| geometry | `coord` ∈ S³ ⊂ ℝ⁴, `radius_origin`, `hilbert_index`, `trajectory`, `n_constituents` | double / bytea / geometry |
| identity | `id`, `type_id` (128-bit), `tier` | bytea / smallint |
| structure | container degree, constituent count, node degree | int64 |

Constants, single-sourced in C (`glicko2.h`): `κ` via `foundry_rd_kappa()`,
witness half-max via `foundry_witness_sat()`, `glicko2_neutral_mu()`,
`glicko2_initial_rd()`, `glicko2_initial_volatility()`, `glicko2_tau()`. [S]

---

## 2. The eight forms

| # | form | expression | instances |
|---|---|---|---|
| F1 | fixed-point ↔ real | `x / 1e9` | ubiquitous |
| F2 | lower confidence bound | `μ − k·σ`, k = 2 | `eff_mu`, `effective_mu`, 25 open-coded sites [M, G1 baseline] |
| F3 | exponential reliability | `e^(−κ·rd)` | OP4 `COMPLETE` |
| F4 | saturation (Hill, n = 1) | `x / (x + h)` | `foundry_witness_sat` |
| F5 | logistic + clamp | `σ(z)`, clamped [0.05, 1.0] | OP4 `STRENGTH` |
| F6 | multiplicative prior | `rank · w` | `edge_rank`, salience, `prompt_coherence` |
| F7 | self-normalizing ratio | `a / Σa`, PPMI, `1/log₂(2+n)` | `specificity`, `ASSOCIATION`, `entity_container_degree` |
| F8 | weighted mean on a manifold | `Σ(pᵢwᵢ) / Σwᵢ` | `laplace_attention_centroid` |
| — | Glicko-2 update (stateful) | `g(φ)`, `E`, `v`, `Δ`, Illinois for `σ′` | `laplace_glicko2_accumulate` |

### F1–F6 are one model — **on the positive orthant only**

`COMPLETE = rank × (rating − neutral) × e^(−κ·rd) × wc/(wc+h)` is a product of
independent factors, so **where every factor is strictly positive** it is
linear in log space:

```
log w = log(rank) + log(rating − neutral) − κ·rd + log(wc/(wc+h))
```

`SALIENCE` drops terms 3 and 4. `CONSERVATIVE` is F2 alone. `STRENGTH` is F5
over F2. There are not four weights — there is one kernel and three truncations
of it. [S]

**Sign is verdict; magnitude is strength — and the kernel is log-linear on the
magnitude.** `Δ = rating − neutral` is *signed by design*: position relative to
`glicko2_neutral_mu()` is the **verdict** — affirm above, reject below, neutral
at — and `attestations.outcome` carries the same semantics at the evidence
layer. A claim rated below neutral is testimony that it is **false**, which is
the distinction the read rules exist to protect ("an unattested id is not an id
attested false"). A strongly-refuted edge is *strong* evidence, not weak
evidence.

So the correct factorization separates the two channels:

```
w = sign(Δ) · exp( log rank + log|Δ| − κ·rd + log sat(wc) )
```

Log-linear on `|Δ|`, with sign carried alongside. **The magnitude calculation
is sign-agnostic**, and "how negative" is symmetric to "how positive".

Two earlier drafts of this section were wrong and are corrected here rather
than quietly rewritten:

- The first claimed the kernel was log-linear with no restriction. It is
  log-linear **on the magnitude**; `log Δ` itself is undefined on the reject
  half.
- The second claimed a signed factor creates a parity hazard — "two rejected
  factors multiply to an affirmation." **There is no such hazard**: exactly one
  factor in `COMPLETE` is signed. `rank`, `e^(−κ·rd)` and `wc/(wc+h)` are
  strictly positive, so there is a single sign channel and nothing to XOR.

**[M] the constants:** `neutral = 1500`, `initial_rd = 350`, `κ = 1.0`.

**[C] and this is the one to measure first:** a fresh edge *starts at* neutral,
so `(rating − neutral) ≈ 0` — and it is a **factor**. On a thin seed `COMPLETE`
would not misrank so much as **annihilate**, driving nearly every weight to
zero exactly where the substrate is sparse and the other factors most need to
speak. Every `eff_mu_display` measured 2026-08-04 sat below neutral (1169.73,
1082.27, 994.80, 1388.07), which is correct Glicko behaviour — uncertainty
pulls a conservative estimate down — but it means the distance from neutral is
small and signed across the whole foundation seed.
*Falsifying query:* distribution of `rating − glicko2_neutral_mu()` over
`consensus`, and the fraction within ±1 rd of neutral. **Run on a quiet box.**

**Consequence for OP4:** the centralization is a kernel with *declared
exponents per factor* — a mode becomes a coefficient vector, which is data, and
data can be attested, versioned and reproduced (spec 37 §5's demand of
`FoundryDefaults`). But the kernel must separate **magnitude** from **verdict**:
a product is the wrong combinator for a signed adjudication. Candidates: carry
the verdict as a separate signed term outside the product, or transform to a
strictly-positive confidence in [0,1] before multiplying. **Undecided — do not
implement either until the distribution above is measured.**

---

## 3. Findings

### 3.1 Two incompatible uncertainty discounts — **the important one**

`eff_mu` discounts uncertainty **linearly**: `rating − 2·rd`. [S]
`COMPLETE` discounts it **exponentially**: `e^(−κ·rd)`. [S]

Both are called "the weight." They disagree about how uncertainty propagates,
and they cannot both be right:

- `μ − 2σ` is a Gaussian lower confidence bound — a *quantile*. It is the right
  object for **ranking** under uncertainty (rank by worst plausible case).
- `e^(−κσ)` is a *reliability multiplier* — it shrinks magnitude toward zero. It
  is the right object for **mixing** (weighting one contribution among many).

They are not interchangeable, and the difference is observable: F2 can go
**negative**, F3 cannot. Anything that ranks should use F2; anything that sums
or averages should use F3. **The repo currently picks by which function the
author was in.**

`CONSERVATIVE` must remain inlinable so `consensus_*_eff_mu_btree` serves it
(the eff_mu inlining law) — so F2 stays the index-facing form regardless.

### 3.2 `total_mass` has never declared which quantity it is

`prompt_coherence` accumulates `total_mass += rank × eff` where
`eff = rating − 2·rd`, then computes `specificity = coherence / total_mass`. [S]

The defect is **not** that the term can be negative — sign is verdict (§2), and
a refuted edge is real evidence. The defect is that summing a *signed* quantity
answers a different question from summing its magnitude, and the body never
says which one it is asking:

- `Σ Δ` is a **net verdict**: affirmed and refuted edges **cancel**. An entity
  with 40 strongly-affirmed and 40 strongly-refuted edges scores the same as
  one with no edges at all.
- `Σ |Δ|` is **total evidence**: how much has been witnessed about this entity,
  regardless of which way it went.

`specificity` is "the share of a candidate's own witnessed mass that reaches
the rest of the prompt" — which reads as a claim about **evidence**, so the
denominator should be `Σ|Δ|` while the numerator's semantics are their own
question. Today both are `Σ Δ`.

Consequences that follow only from the *net* reading: the ratio explodes as the
denominator approaches zero through cancellation, and inverts below it. The
existing guard is `total_mass > 0.0`, which catches exact zero and neither of
those.

Note `eff_mu = rating − 2·rd` is a **quantile, not a symmetric magnitude** — it
is not centred on neutral, so `|eff_mu|` is not `|Δ|`. Whichever is chosen, the
mass term and the ranking term must not silently be different objects.

### MEASURED 2026-08-05 — and the answer is not what this section predicted

447,145 consensus rows (`TABLESAMPLE SYSTEM (2)`) on the OMW-seeded substrate:

| | |
|---|---|
| above neutral | 444,081 (99.3%) |
| at neutral | 3,064 |
| **below neutral** | **0** |
| within 1·rd of neutral | **355,893 (79.6%)** |
| `eff_mu` below neutral | **427,955 (95.7%)** |
| `eff_mu` negative | **0** |
| mean \|Δ\| | **192.85** |
| mean `rd` | **262.24** |
| mean witnesses | **2.51** |

**The signed-vs-absolute question is moot on this substrate.** Nothing is below
neutral, so `Σ Δ` and `Σ|Δ|` are the same number and the denominator cannot flip
sign or cancel. `eff_mu` never goes negative either. The concern above is
**latent, not live** — it becomes live the first time a source refutes anything.

**The live defect is different and larger: the fold is uncertainty-dominated.**
Mean `|Δ|` is 192.85 against mean `rd` of 262.24, so the `2·rd` term is **2.7×
the signal**. `eff_mu = rating − 2·rd` therefore places 95.7% of edges below
neutral even though 99.3% of *ratings* sit above it. At 2.51 witnesses per edge,
`rd` has had almost nothing to shrink against.

Consequence for every ranking key in the system: **on this seed `eff_mu` orders
predominantly by how much evidence exists, not by what the evidence says.** Two
edges whose ratings differ by 100 but whose `rd` differ by 50 are separated
mostly by the `rd` term. That is defensible as a conservative bound and it is
*not* what the callers ranking by `eff_mu` believe they are ranking by.

This also sharpens §3.1: on a substrate this thin, the choice between the linear
discount (`μ − 2σ`) and the exponential one (`e^{−κ·rd}`) is not a refinement —
it selects which of the two quantities dominates the order.

### 3.3 Euclidean mean on a sphere

`laplace_attention_centroid` computes `Σ(coordᵢ·wᵢ) / Σwᵢ` component-wise and
**never renormalizes**. [S]

The arithmetic mean of unit vectors is not a unit vector — it lies strictly
inside S³ (at the origin for an antipodal pair). The correct object is the
**Fréchet/Karcher mean** under geodesic distance; its standard cheap
approximation is exactly this weighted sum **normalized back to the sphere**.

Consequences: the centroid's radius is a *concentration* measure that is
currently being silently discarded, and any downstream angular distance to an
unnormalized centroid is not an angle. Note `radius_origin` already exists as a
generated column, so the norm is a first-class quantity elsewhere in the schema
but not here.

(This function separately returned NULL on every call until #866; the mean was
never exercised.)

### 3.4 Chord distance read as geodesic

PostGIS `<<->>` is Euclidean n-D. On the unit sphere the chord `d` and the
geodesic `θ` satisfy `d = 2·sin(θ/2)`, strictly monotone on `[0, π]`. [S]

So **KNN ordering is correct** — this is not a retrieval bug. But `d` is not
`θ`: any threshold, any averaged distance, any ratio of distances, and any
mixing of a "geodesic" with a Fréchet value is comparing different units.
`explore_anchor_neighbors` returns a column named `geodesic` alongside
`frechet`; if that column carries a chord it is mislabeled.

### 3.5 One quantity, three numeric types

`eff_mu` → `bigint`; `eff_mu_display` → `numeric`; `prompt_coherence` casts to
`double`. [S] Fixed-point preserves determinism and is what the index expression
uses; `double` is what the C accumulates in; `numeric` is what SQL renders.
Rounding differs at each boundary, and G6 (weight parity) can only be
meaningful once the conversion points are declared rather than incidental.

### 3.6 F2 is open-coded in 25 places

[M] `isa-gate-check.py` baseline: 19 production-path expressions plus 6 in
`scripts/sql/model-planes-audit.sql`. G1 ratchets this shrink-only. Every one is
a site where 3.1's choice was made implicitly.

---

## 3.7 Census — how many sites compute each form [M, 2026-08-04]

351 function files under `sql/functions/`.

| form | sites | via a shared implementation |
|---|---|---|
| F2 conservative estimate | **9 open-coded** in SQL + 25 tree-wide per the G1 baseline | 61 call `eff_mu()`, 11 call `edge_rank()` |
| Glicko-2 | 23 SQL files, 12 C files | the update itself is single-sourced |
| Fréchet | 31 files | — |
| `<<->>` KNN | 12 files | — |
| hilbert | 17 files | — |
| centroid / spherical mean | 6 files | — |
| highway mask ops | 25 files | `laplace_highway_match` — correctly shared |
| PPMI / log-ratio | 6 files | — |

### The ratio that indicts

| how a body reads edges | files |
|---|---|
| **`FROM consensus` directly** | **57** |
| via `edges_raw()` | 8 |
| via `consensus_out()` / `consensus_in()` | 2 |
| via `consensus_by_ids()` | 1 |

**57 bodies hand-roll the edge read; 11 go through a shared one.** Spec 37 OP5
specifies exactly one `SCAN`. There are 57, each with its own direction
handling, its own refuted policy, its own ordering and its own cap — which is
why `consensus_out` and `consensus_in` silently include refuted edges while
their siblings exclude them (spec 37 OP5). 87 files carry a `subject_id =`
predicate.

Family sizes, for scale: 18 `recall_*_response` adapters, 23 realize/readback,
46 converse, 45 ops/inspect.

This is the same defect W16 §3.1 finds in the arithmetic, expressed in the read
path: the operation exists, a canonical implementation exists, and most callers
do not use it.

## 4. What centralization means here

1. **One kernel, coefficients as data.** `w = Π fᵢ^βᵢ`, evaluated in log space.
   Modes become named coefficient vectors, attested and versioned — not an enum
   in C.
2. **Declare which discount a call site needs.** Ranking → F2 (quantile).
   Mixing/averaging → F3 (reliability). No site picks by accident.
3. **One numeric contract** per boundary, stated: fixed point at rest and in
   index expressions, double in native accumulation, numeric only at render.
4. **Manifold operations respect the manifold.** Spherical means normalize;
   distances declare chord or geodesic in the column name.
5. **Constants stay single-sourced in C** and are read, never restated —
   already true for `κ` and half-max (`glicko2.h:80–92`), and the model for the
   rest.

## 5. Acceptance

1. Every `[C]` measured before it changes code — 3.2 first, since `specificity`
   is a live ranking key.
2. G6 extended to all modes with the numeric contract pinned (currently only
   `COMPLETE` and the constants are pinned).
3. G1's 25 sites shrink, each replaced by the kernel with a **declared**
   discount.
4. No new form appears without a row in §2.
