# Substrate-Native Algorithms — Research Scratchpad

**Status:** scaffold. Not user-authored canon. Lives under `docs/research/` so it is clearly working material, not invariant spec. Authoritative invariants remain in `RULES.md`, `DESIGN.md`, `GLOSSARY.md`, `STANDARDS.md`, `OPERATIONS.md`.

**Purpose:** enumerate the algorithm families that fall out of treating Laplace's substrate (typed, source-rated, content-addressed evidence sheaf) as the mathematical object of study, and give each one a stable home for incremental deepening.

**Reading order:** Section 0 (shared notation) → any family. Families are independent after Section 0.

---

## How to use this document

- Each family below is a stub with a fixed section template (see §0.3).
- We fill stubs one at a time. Each pass on a family should leave every template subsection at least lightly populated before moving on.
- When a family reaches "workshop-paper-grade" (formal object + update rule + 3-6 citations + novelty delta + open problems), promote to its own file under `docs/research/algorithms/<id>.md` and replace the section here with a one-paragraph abstract + link.
- Citations live inline as `[Author Year]` and are collected per-family in the **References** subsection. A consolidated bibliography is deferred until at least 4 families are paper-grade.
- No claim in this document is canon until reflected (with user authorization) in `RULES.md` / `DESIGN.md` / an ADR.

---

## Table of contents

### Part I — Foundations

- [§0.1 Mathematical object: the evidence sheaf](#01-mathematical-object-the-evidence-sheaf)
- [§0.2 Shared notation](#02-shared-notation)
- [§0.3 Per-family section template](#03-per-family-section-template)
- [§0.4 Honesty boundary: what we do NOT claim](#04-honesty-boundary-what-we-do-not-claim)

### Part II — Inference and consensus

- [§1 ARI — Attestation-Response Inference](#1-ari--attestation-response-inference)
- [§2 TAS — Typed A* with arena admissibility](#2-tas--typed-a-with-arena-admissibility)
- [§3 GSC — Geometry-Sound Cascade pruning](#3-gsc--geometry-sound-cascade-pruning)
- [§4 DCB — Drift / Creativity Budgeting](#4-dcb--drift--creativity-budgeting)

### Part III — Rating, trust, conflict

- [§5 ACG — Arena-Coupled Glicko field](#5-acg--arena-coupled-glicko-field)
- [§6 SCD — Sheaf-Cohomology Conflict Detection](#6-scd--sheaf-cohomology-conflict-detection)
- [§7 PLT — Prompt-Local Tug (non-truth-granting)](#7-plt--prompt-local-tug-non-truth-granting)

### Part IV — Generation, synthesis, decoding

- [§8 SED — Suffix-Extension Decoding](#8-sed--suffix-extension-decoding)
- [§9 RIM — Reverse-Index Motif Reuse](#9-rim--reverse-index-motif-reuse)
- [§10 DSS — Deterministic Substrate Synthesis](#10-dss--deterministic-substrate-synthesis)

### Part V — Compression, lineage, lifecycle

- [§11 MDS — Merkle-Dedup Substrate description length](#11-mds--merkle-dedup-substrate-description-length)
- [§12 LTU — Lineage-Tracing Unlearning](#12-ltu--lineage-tracing-unlearning)

### Part VI — Cross-cutting and meta

- [§13 Open mathematical questions](#13-open-mathematical-questions)
- [§14 Empirical calibration plan](#14-empirical-calibration-plan)
- [§15 Publication targets and venues](#15-publication-targets-and-venues)
- [§16 Glossary delta vs `GLOSSARY.md`](#16-glossary-delta-vs-glossarymd)
- [§17 Roadmap: order of filling](#17-roadmap-order-of-filling)

---

## Part I — Foundations

### §0.1 Mathematical object: the evidence sheaf

Define the substrate as the tuple

$$
\mathcal{S} \;=\; (E,\; \Sigma,\; K,\; A,\; \pi,\; \rho,\; \mathcal{C},\; \Phi)
$$

with the axioms below. The object combines features of several known structures — a typed property graph, a probabilistic database, an evidence/belief assignment, and a rating system — but is not exactly any of them.

**Axiom A1 (content-addressing).** Entities $e \in E$ are identified by a 128-bit BLAKE3 hash of their canonical-bytes representation. Equality of entities is equality of hashes; no entity can be renamed without rehashing. This is the standard content-addressable storage discipline used by IPFS, Git, and BitTorrent; in our case the hashes form a Merkle DAG bottoming at Unicode codepoints (T0). Relevance: every $e \in E$ has a unique reproducible identity across machines and across sources.

**Axiom A2 (T0 termination).** Every $e$ decomposes through a finite chain of structural attestations into elements of $E_{T0}$, the set of Unicode codepoint atoms. This gives a well-founded base for recursion and a single hash space across all modalities.

**Axiom A3 (typed attestations).** $A$ is a set of typed edges $a = (s, k, o, \sigma, c)$ with $s, o, c \in E$, $k \in K$, $\sigma \in \Sigma$. Kinds carry policy: compatibility, cardinality, context scope, conflict, observation update scope, source-trust policy, lineage policy, structural-support policy (ADR 0036, ADR 0044). This is closer to RDF reification + the named-graph idea than to a plain labeled property graph [Hogan et al. 2021], but with explicit per-kind arena policy attached at the type level.

**Axiom A4 (Glicko-2 triple).** $\pi(a) = (\mu, \phi, \sigma_{g2})$ is a Glicko-2 state: rating, rating deviation, volatility [Glickman 2013]. Stored as int64 fixed-point at scale $10^9$ per STANDARDS.md. Unlike conventional Glicko (per-player), $\pi$ is **per-attestation**: each typed claim has its own observation-rating dynamics. The semantics of "a game" is replaced by "an observation update" — see §5 ACG.

**Axiom A5 (source-trust mapping).** $\rho: \Sigma \times K \to \mathbb{R}$ assigns per-source, per-kind credibility (ADR 0044 source-trust classes 1–10). This is analogous to source-reliability scores in truth-discovery [Li et al. 2016, Yin et al. 2008] and to copying-aware reliability [Dong/Berti-Equille/Srivastava 2009], but kind-conditioned: a source can be highly credible for one kind (e.g. WordNet for IS_A) and weak for another (e.g. WordNet for cardinal numbers).

**Axiom A6 (context layer).** $\mathcal{C} \subseteq E$ is a designated subset of entities used to scope attestations. Contexts are themselves content-addressed. They function as the unit over which the sheaf-of-evidence (§6 SCD) restricts.

**Axiom A7 (projection layer).** $\Phi: E \to (\mathrm{Geom}_{4D} \times \mathrm{Traj} \times \mathrm{Proj})$ assigns each entity per-source physicalities: 4D geometry (PostGIS standard `geometry` with Z+M, extended per ADR 0001/0025/0029), a child-reference trajectory, and projection state. **Physicalities are an access/index lens, not a knowledge layer.** Semantic responses come from $A$, not from $\Phi$ (RULES.md).

**Key contrasts** (used in every family's *Novelty delta*):

- vs **probabilistic databases** [Cavallo/Pittarelli 1987; Dalvi/Suciu 2007]: those represent uncertainty as a distribution over possible worlds; we represent it as per-attestation Glicko-2 with explicit per-source provenance and arena policy. Our query answer is not $\Pr[\text{tuple}]$ but $M(\cdot)$, effective support under traversal mode.
- vs **Dempster–Shafer belief functions** [Dempster 1967; Shafer 1976]: DST assigns mass to subsets of a fixed frame of discernment and combines via Dempster's rule. We do not have a fixed frame; the candidate set is discovered by traversal. We borrow the *upper/lower bound* framing implicitly (mode thresholds bracket admissible answers) but combination is arena-policy-typed rather than rule-typed.
- vs **knowledge graphs / RDF** [Hogan et al. 2021]: KGs are typically untimed, untrust-rated, and reasoned over via OWL/SPARQL. We attach per-edge rating dynamics, per-source provenance, per-kind arena policy, and treat traversal as a compiled cascade.
- vs **transformer parameters** $\theta$: those are the result of a differentiable optimization. $\mathcal{S}$ is a state that ingestion updates and synthesis (§10) reads to compile parametric models. There is no $\theta$ hidden anywhere.

**References (foundations).** Cavallo & Pittarelli 1987; Dalvi & Suciu, *Efficient query evaluation on probabilistic databases*, VLDB J. 2007; Dempster, *Upper and lower probabilities induced by a multivalued mapping*, Ann. Math. Stat. 1967; Shafer, *A Mathematical Theory of Evidence*, Princeton, 1976; Glickman, *Example of the Glicko-2 system*, 2013; Hogan et al., *Knowledge Graphs*, ACM Comput. Surv. 2021 (arXiv:2003.02320); Li, Gao, Meng, Li, Su, Zhao, Fan, Han, *A Survey on Truth Discovery*, ACM SIGKDD Explorations 2016.

### §0.2 Shared notation

Single notation table reused by every family. New families append to it, they do not redefine.

| Symbol | Meaning |
|---|---|
| $e \in E$ | entity (content-addressed, BLAKE3-128) |
| $E_{T0} \subseteq E$ | T0 codepoint atoms (Unicode) |
| $\sigma \in \Sigma$ | source |
| $k \in K$ | attestation kind (carries arena policy) |
| $c \in \mathcal{C} \subseteq E$ | context entity |
| $a = (s, k, o, \sigma, c) \in A$ | attestation |
| $\pi(a) = (\mu, \phi, \sigma_{g2})$ | per-attestation Glicko-2 state (int64 fixed-point, scale $10^9$) |
| $\rho(\sigma, k) \in \mathbb{R}$ | source-kind credibility (ADR 0044) |
| $L(\sigma, A') \in [0,1]$ | lineage-independence of $\sigma$ vs evidence set $A'$ (§5) |
| $C(c_a, c_q) \in [0,1]$ | context compatibility under kind policy |
| $R(\phi, \sigma_{g2}) \in [0,1]$ | certainty factor from Glicko RD/volatility |
| $\Phi(e)$ | physicality lens (geometry + trajectory + projection) |
| $G(q) \subseteq E$ | geometry-sound candidate set for query $q$ (§3) |
| $w(a) \in \mathbb{R}_{\ge 0}$ | per-attestation effective weight (§1) |
| $M(s, k, o, c \mid \text{mode}, \text{scope})$ | effective support (§1) |
| $\tau_{\mathrm{mode}}$ | per-mode weight floor (strict / standard / speculative / creative) |
| $\tau_{\mathrm{lt}}(i,j)$ | per-row lottery-ticket-aware sparsity threshold (§10) |
| $\mathrm{mode} \in \{\text{strict}, \text{standard}, \text{speculative}, \text{creative}\}$ | traversal mode (GLOSSARY.md) |
| $\mathrm{scope}$ | observation/arena scope (prompt / session / source / global) |
| $T(\mathrm{mode})$ | per-mode policy clamp / multiplier |

### §0.3 Per-family section template

Every family below uses this template. Treat it as a checklist when filling.

```
### §N <ID> — <Name>

#### Object
What mathematical object is at play (graph / sheaf / policy / dynamical system / operator)?

#### Inputs / state / outputs
Typed signature. What goes in, what is the persistent state, what comes out.

#### Update rule or query equation
Plain math. No implementation. Should be small enough to fit in one screen.

#### Algorithm sketch
~10 lines of pseudocode. Engine-side, not SQL. Reference compiled cascade boundaries.

#### Complexity character
Big-O for time/space; compare to conventional analogue (transformer forward pass, HNSW query, etc.).

#### Novelty delta
What is genuinely new vs prior art. Be conservative — name overlaps explicitly.

#### Substrate invariants relied on
Which RULES.md rules / ADRs are load-bearing.

#### Open problems
3-7 bullets. Theorems to prove, calibrations needed, adversarial cases.

#### Publication framing
Workshop / journal / which community. One-sentence pitch.

#### References
Inline-cited works for this family.
```

### §0.4 Honesty boundary: what we do NOT claim

**Stub.** Recurring honesty list to prevent drift in any family:

- We do not change linear algebra or probability theory.
- We do not beat well-tuned transformers on tasks whose answer is not representable as substrate-supported traversal/reconstruction/synthesis.
- "Microseconds for long answers" applies only to reconstruction/expansion-dominated queries with low drift budget over already-ingested structure.
- Constants and stability of arena/source-trust dynamics require empirical calibration; the math frames, it does not validate.
- Sheaf / cohomology framings are real but need formal restriction-map definitions before they are publishable rather than evocative.

---

## Part II — Inference and consensus

### §1 ARI — Attestation-Response Inference

#### Object

A functional $M$ that, given a typed query $(s, k, ?, c)$ and a traversal mode, scores candidate object entities $o$ by **effective support**: the weighted sum of per-attestation Glicko-2 ratings, where the weights compose source-kind credibility, lineage independence, context compatibility, certainty, and a policy clamp.

Formally, define the per-attestation weight

$$
w(a) \;=\; \rho(\sigma_a, k_a) \cdot L(\sigma_a, A_{-a}) \cdot C(c_a, c_q) \cdot R(\phi_a, \sigma_{g2,a}) \cdot T(\mathrm{mode})
$$

and the effective support

$$
M(s, k, o, c \mid \mathrm{mode}, \mathrm{scope}) \;=\; \sum_{a \in A(s, k, o, c, \mathrm{scope})} w(a) \cdot \mu(a)
$$

where $A(\cdot, \mathrm{scope})$ restricts to attestations admissible under the requested observation/arena scope (prompt-local, session, source, global; RULES.md R19/R20).

#### Inputs / state / outputs

- **Inputs.** Query tuple $(s, k, ?, c)$; traversal mode; observation scope; optional candidate-set hint $G(q) \subseteq E$ from §3 GSC.
- **State.** $\mathcal{S}$ (read-only for ARI; ACG §5 mutates $\pi$ and $\rho$).
- **Outputs.** Either a ranked list $[(o_1, M_1), (o_2, M_2), \dots]$ with $M_i \ge \tau_{\mathrm{mode}}$, or $\bot$ (abstain) if no $o$ achieves the mode threshold.

#### Update rule or query equation

ARI does not update; it queries. The query equation is the definition of $M$ above. Two derived quantities matter:

1. **Truth-cluster index.** Independent high-trust convergence: pick the top-$k$ attestations by $w(a)$ for a given $o$ and compute their lineage-independence sum $\sum L(\sigma_a, A_{-a})$. High value with high $\mu$ means cross-source convergence; high value with high count but low $L$ means a lineage echo, not real convergence. This addresses the *copying* problem flagged in truth-discovery literature [Dong/Berti-Equille/Srivastava 2009].
2. **Conflict index.** $K = \sum_{o' \neq o^*} M(\cdot, o', \cdot) / \sum_{o} M(\cdot, o, \cdot)$, the Dempster-style mass-on-disagreement for the contender pool, repurposed as an arena-aware conflict score rather than a normalization factor.

#### Algorithm sketch

Engine-side (`liblaplace_substrate`), called from the compiled cascade SRF per ADR 0035:

```
ari(s, k, c_q, mode, scope, G_hint):
  C ← candidates(s, k, c_q, G_hint)            # optionally pruned by GSC
  results ← empty min-heap of size N
  for each o in C:
      A_o ← attestations(s, k, o, c_q) filtered by scope
      M  ← 0
      for each a in A_o:
          w ← rho(σ_a, k) * L(σ_a, A_o - {a})
                       * C(c_a, c_q) * R(φ_a, σg2_a) * T(mode)
          M += w * μ(a)
      if M ≥ τ_mode: results.push((o, M))
  if results empty: return ABSTAIN
  return results.sorted_desc()
```

All arithmetic is fixed-point int64; no GEMM, no GPU, no recursive CTE, no SQL frontier. This is a single-pass scan of typed attestations within the candidate set.

#### Complexity character

Let $|C|$ be the candidate set, $\bar{d}$ the average per-candidate attestation count under scope. ARI is $O(|C| \cdot \bar{d})$ time, $O(N + \bar{d})$ working memory for the result heap and per-candidate evidence list. With §3 GSC pruning, $|C| \ll |E|$. Compare to:

- Transformer forward pass: $O(L \cdot d^2)$ for $L$ tokens, $d$ hidden dim, regardless of query specificity, and yields no provenance.
- HNSW $k$-NN: $O(\log |E|)$ per query but returns approximate spatial neighbors, not typed support.
- SQL recursive CTE over an attestation graph: blows up with depth; not the design point here.

#### Novelty delta

What is *not* new:

- Weighted source aggregation is core to truth-discovery [Yin/Han/Yu 2008 TruthFinder; Galland et al. 2010 *Corroborating information from disagreeing views*; Zhao et al. 2012 Bayesian truth discovery]. Iterative source-trust + value-trust loops are standard.
- Source-reliability scores tied to evidence are central to Dempster–Shafer combination and to TrueSkill / Glicko families.
- Abstention thresholds appear throughout uncertain-DB query answering and in conformal prediction.

What *is* new in ARI:

1. **Per-attestation Glicko-2 rather than per-source or per-value.** Conventional truth-discovery rates sources; Glicko rates players. ARI rates each typed *edge* with full RD/volatility state.
2. **Kind-conditioned arena policy as a first-class weight component.** The factor $T(\mathrm{mode})$ × kind-defined cardinality / conflict / structural-support semantics is not present in DST or truth-discovery.
3. **Lineage-independence shrinkage built into the weight, not the score.** Copying-aware truth-discovery [Dong et al. 2009] discounts dependent sources; ARI puts the same idea inside the per-evidence weight so the same machinery serves single answers, ranked answers, drift budgeting (§4), and conflict detection.
4. **Mode-budgeted abstention.** $\bot$ is a first-class return value with a defined optimality condition, not a downstream confidence filter.
5. **Scope-typed observation filter.** Prompt-local content can tug without being treated as global truth (RULES.md R19/R20).

#### Substrate invariants relied on

- ADR 0035 (compiled cascade is the only hot-path traversal surface).
- ADR 0036 (arena policy; observation-update vs raw vote).
- ADR 0044 (kind-value tiers T1–T11 and source-trust classes 1–10; user-prompt content is kind-tier T11 "Probationary / User" and source-trust Class 9 "User Prompt / User Content").
- STANDARDS.md (int64 fixed-point ratings; no floats in hot path).
- RULES.md R19/R20 (prompt-local content vs claim distinction).

#### Open problems

1. **Calibrating $T(\mathrm{mode})$ across kinds.** A single scalar per mode is the first cut; per-kind multipliers may be needed.
2. **Defining $L(\sigma, A_{-a})$ operationally.** Lineage is tracked by source metadata, but turning it into a $[0,1]$ shrinkage scalar requires a measurement (§11 MDS may help: $\Delta\mathrm{SDL}$ on the source’s contribution).
3. **Stability vs adversarial mass.** What guarantees against an adversary flooding low-trust attestations? Likely follows from $\rho$ saturation + RD floors, but needs a theorem.
4. **Relation to Dempster’s rule conflict $K$.** Is our normalization-free $M$ a strict generalization, or does it lose the upper-bound interpretation?
5. **Cardinality kinds.** Multi-truth (set-valued) kinds [Wang et al. 2015] need a generalized $M$ over sets, not single $o$.
6. **Anytime behavior.** Can ARI return a provably-monotone best-so-far if the cascade is interrupted? Likely yes given heap-based ranking; needs spec.

#### Publication framing

One-sentence pitch: *Effective support is a substrate-native query operator that subsumes truth-discovery, evidence combination, and rated-evidence ranking under one arena-policy-typed weight*.

- Workshop: NeurIPS Workshop on Knowledge Representation for Foundation Models; ACL Workshop on Truth and Trust Online.
- Conference: SIGMOD, VLDB (database angle), or AAAI (uncertain reasoning angle).
- Comparator baselines: TruthFinder, Latent-Truth-Model (Zhao et al. 2012), Dempster combination, majority vote, RAG-with-citation-scoring.

#### References

- Cavallo & Pittarelli, *The Theory of Probabilistic Databases*, VLDB 1987.
- Dalvi & Suciu, *Efficient query evaluation on probabilistic databases*, VLDB J. 2007.
- Dempster, *Upper and lower probabilities induced by a multivalued mapping*, Ann. Math. Stat. 1967.
- Shafer, *A Mathematical Theory of Evidence*, Princeton, 1976.
- Dong, Berti-Equille & Srivastava, *Integrating conflicting data: the role of source dependence*, VLDB 2009.
- Galland, Abiteboul, Marian & Senellart, *Corroborating information from disagreeing views*, WSDM 2010.
- Glickman, *Example of the Glicko-2 system*, 2013.
- Jøsang & Simon, *Dempster's Rule as Seen by Little Colored Balls*, Comp. Intel. 2012 (cumulative vs constraint fusion).
- Li, Gao, Meng, Li, Su, Zhao, Fan & Han, *A Survey on Truth Discovery*, SIGKDD Explorations 2016.
- Pearl, *Reasoning with Belief Functions: An Analysis of Compatibility*, Int. J. Approx. Reason. 1990 (DST critique — honesty boundary).
- Wang, Sheng, Fang, Yao, Xu & Li, *An Integrated Bayesian Approach for Effective Multi-Truth Discovery*, CIKM 2015.
- Yin, Han & Yu, *Truth Discovery with Multiple Conflicting Information Providers on the Web*, IEEE TKDE 2008 (TruthFinder).
- Zhao, Rubinstein, Gemmell & Han, *A Bayesian approach to discovering truth from conflicting sources for data integration*, VLDB 2012.

---

### §2 TAS — Typed A* with arena admissibility

#### Object

A heuristic best-first search over the *typed* attestation DAG that owns the cascade frontier (ADR 0035). Where classical A* [Hart, Nilsson & Raphael 1968] searches a single-cost weighted graph with $f(n) = g(n) + h(n)$, TAS searches a graph whose edges carry kind-typed weights derived from ARI (§1) effective support, with admissibility defined relative to *arena policy* rather than path cost.

A traversal path is a sequence of attestations $a_1, a_2, \dots, a_\ell$ forming a connected chain in the kind-typed DAG from query node to candidate object. Path cost is the sum of negative-log effective weights plus a mode-tuned support discount:

$$
g(a_1 \cdots a_\ell) \;=\; \sum_{i=1}^{\ell} \big(-\log w(a_i)\big) \;-\; \lambda_{\mathrm{mode}} \sum_{i=1}^{\ell} \mu(a_i),
$$

with $w(a_i)$ the ARI weight (§1) and $\lambda_{\mathrm{mode}} > 0$ a mode-dependent rating-reward.

#### Inputs / state / outputs

- **Inputs.** Query $(s, k_{\mathrm{target}}, ?, c, \mathrm{mode}, \mathrm{scope})$; optional kind sequence policy (which kinds may appear along the chain); branching cap $B$; beam width $\beta$ (\u00a73 GSC supplies $G(q)$ which seeds the initial open set); abstention threshold $\tau_{\mathrm{mode}}$.
- **State.** Per-cascade-call open set (binary min-heap), closed set (hash), `came_from` predecessor map, `g_score` map. All in C/C++ arena memory; no SQL frontier (RULES.md R6, ADR 0035).
- **Outputs.** Ranked list of $(o, M, \mathrm{path})$ triples with $M \geq \tau_{\mathrm{mode}}$ and provenance chain, OR $\bot$ (abstention) when no admissible chain reaches the mode threshold.

#### Update rule or query equation

The cost-algebraic A* extension [Edelkamp, Jabbar & Lluch-Lafuente 2005] applies: as long as the edge-cost domain forms a *monotone, isotone semiring*, $f = g \oplus h$ defines a sound best-first search. We use the negative-log-weight + rating-discount algebra above, which is monotone (kinds may not make a path *more* certain by adding more steps under arena policy) and isotone (extending a path cannot reduce its accumulated incompatibility).

**Heuristic $h(n)$ for a frontier node $n = (e_{\mathrm{cur}}, k_{\mathrm{seq}})$.** Lower bound on remaining path cost to any admissible terminal:

$$
h(n) \;=\; -\log\Big(\max_{a \in \text{admissible}(n)} w(a)\Big) \;-\; \lambda_{\mathrm{mode}} \cdot \mu^{\sup}_k(e_{\mathrm{cur}}),
$$

where $\mu^{\sup}_k(e_{\mathrm{cur}})$ is the *kind-conditional Glicko ceiling* \u2014 the maximum $\mu(a)$ over any single admissible extension of kind $k$. This is admissible because no single extension can exceed its own kind's observed ceiling; it is consistent in the Pearl sense when the kind policy is *monotone-compatible* (composition of compatible kinds remains compatible).

**Arena admissibility.** Define $h$ as *arena-admissible* iff for every compatible terminal $o^*$ reachable from $n$ under the kind policy, $h(n) \le g(n \to o^*)$ holds in the cost algebra above. We prove this from ADR 0036 kind compatibility: incompatible extensions are excluded from the max, and the $\mu^{\sup}$ ceiling is by construction an upper bound on any single step. Hence TAS-with-arena-$h$ inherits classical A* admissibility and optimal efficiency [Dechter & Pearl 1985] over the admissible-A*-like family.

**Bounded relaxation.** For *speculative* and *creative* modes we use weighted A* [Pohl 1970] with $f = g + \varepsilon_{\mathrm{mode}} h$, $\varepsilon_{\mathrm{strict}} = 1$, $\varepsilon_{\mathrm{standard}} = 1$, $\varepsilon_{\mathrm{speculative}} \in (1, 2]$, $\varepsilon_{\mathrm{creative}} > 2$. This yields $\varepsilon$-admissible chains with cost $\le \varepsilon$ times optimal. For very wide candidate sets, beam-stack search [Zhou & Hansen 2005] is the fallback: a memory-bounded anytime variant that returns provably-monotone best-so-far.

#### Algorithm sketch

```
tas(start_query, mode, scope, G_seed):
  open  \u2190 min-heap of (f, node), seeded from G_seed
  closed \u2190 hashset
  g_score \u2190 map, default \u221e
  for s in G_seed: g_score[s] = 0; push(open, (h(s), s))
  best_results \u2190 fixed-size top-N heap

  while open not empty:
      f, n \u2190 pop(open)
      if f > f_bound(mode):                   # mode-clamped \u03b5-bound
          continue
      if is_terminal(n, k_target):
          M \u2190 ari_support(path_to(n))         # \u00a71
          if M \u2265 \u03c4_mode: best_results.push((n, M, path_to(n)))
          continue
      closed.insert(n)
      for a in admissible_extensions(n, kind_policy):
          n' \u2190 extend(n, a)
          tentative \u2190 g_score[n] + edge_cost(a, mode)
          if tentative < g_score[n']:
              came_from[n'] = n
              g_score[n'] = tentative
              push(open, (tentative + \u03b5_mode * h(n'), n'))

  if best_results empty: return ABSTAIN
  return best_results.sorted_desc()
```

Implemented inside `liblaplace_substrate` and invoked from the cascade SRF (ADR 0035). Open-set is a pairing-heap or quaternary binary heap; closed-set is BLAKE3-keyed hash; both use Postgres memory context allocations for clean transaction-bounded teardown.

#### Complexity character

Time bounded by classical A*: $O(|N_\le|)$ node expansions where $N_\le = \{n : f(n) \le C^*\}$, with $C^*$ the cost of the optimal admissible terminal. Space $O(|N_\le|)$ for open+closed; SMA*-style pruning [Russell 1992] applies if $|N_\le|$ exceeds the arena budget.

With \u00a73 GSC seeding (candidate set from physicality lens) and arena-policy pruning of inadmissible extensions, $|N_\le|$ in practice is small: typical cascade query expands $10^2$\u2013$10^4$ attestations even when $|A| \gg 10^9$. Compare:

- Recursive CTE (Postgres): the explicit-counterexample for why this is in the engine: a 3-hop recursive CTE over the full attestation DAG is intractable; ADR 0035 forbids it.
- Beam search [Lowerre 1976; Tillmann & Ney 2003]: TAS subsumes beam as the special case $\varepsilon = \infty$ and `best_results` size = $\beta$; we keep beam as a degenerate fallback mode.
- Monte Carlo tree search: TAS uses Glicko-derived $\mu$ in place of UCB1; no rollouts.
- Anytime A* [Hansen & Zhou 2007]: TAS is naturally anytime via `best_results` heap; first feasible terminal returns immediately with monotone improvement on continued expansion.

#### Novelty delta

Not new:

- A* with admissible heuristic [Hart, Nilsson & Raphael 1968; Dechter & Pearl 1985] over weighted DAGs.
- Cost-algebraic generalization to semiring-valued edge costs [Edelkamp, Jabbar & Lluch-Lafuente 2005].
- Weighted A* and bounded suboptimality [Pohl 1970; Pearl 1984].
- Beam search [Lowerre 1976] and beam-stack search [Zhou & Hansen 2005] for memory bounds.
- A* parsing in NLP [Klein & Manning 2003].

What *is* new in TAS:

1. **Edge weights are arena-policy-typed ARI weights, not scalar distances.** The cost algebra is derived from kind compatibility, source trust, lineage, Glicko certainty, and traversal mode \u2014 not from any metric ambient space. There is no geometric distance on the hot path; physicality is only a candidate-seeding lens (\u00a73 GSC).
2. **Arena-admissibility.** The admissibility proof is conditioned on ADR 0036 kind policy, not on a static cost function. Incompatible kinds simply do not appear in the open set; the heuristic ceiling $\mu^{\sup}_k$ is bounded by observed Glicko state, not by metric assumptions.
3. **Abstention as optimal terminal.** $\bot$ is a first-class result whose optimality follows from "no extension achieves $\tau_{\mathrm{mode}}$"; this is not a downstream confidence filter but a built-in property of TAS termination.
4. **Mode-clamped $\varepsilon$.** Traversal mode (strict/standard/speculative/creative) directly chooses the weighted-A* relaxation, giving a *single* search algorithm that spans exact-optimal through hallucination-budgeted creative inference. No conventional system unifies these regimes under one algorithm.
5. **Compiled cascade ownership of the frontier.** ADR 0035 forbids SQL-side frontier management; TAS is the *only* legitimate cascade hot path. This is an architectural constraint, but it is also what makes the per-call open-set/closed-set arena-bounded and deterministic.

#### Substrate invariants relied on

- ADR 0035 \u2014 compiled cascade SRF owns frontier management; no recursive CTE, RBAR, cursor, or app-layer loop on the hot path.
- ADR 0036 \u2014 arena policy supplies kind compatibility, cardinality, conflict semantics; defines what `admissible_extensions(n, kind_policy)` returns.
- RULES.md R6 — only the Glicko-2 update aggregate runs SQL-side; everything else (including TAS) is C/C++.
- STANDARDS.md \u2014 int64 fixed-point ratings; deterministic across machines.
- \u00a71 ARI \u2014 supplies $w(a)$ and the final $M$ score.
- \u00a73 GSC \u2014 supplies $G(q)$ seed set.

#### Open problems

1. **Consistency proof for the kind-conditional $\mu^{\sup}$ heuristic.** Admissibility is shown above; consistency in Pearl's sense requires the monotone-compatibility property of the kind closure, which we conjecture but have not proven for all ADR 0036 kind classes.
2. **Optimal $\lambda_{\mathrm{mode}}$ schedule.** Trade-off between cost and rating contribution is currently a per-mode constant; learning it from outcome telemetry would be useful but circular with ACG (\u00a75).
3. **Inconsistent-but-admissible heuristics.** Recent work [Felner & Zahavi 2011; Zhang & Sturtevant 2009] shows inconsistent heuristics can reduce expansions in some cases. Whether arena-policy heuristics fall into the helpful or pathological category is open.
4. **Bidirectional TAS.** Starting cascade from both query and candidate-target frontier, meeting in the middle (cf. [Goldberg et al. 2007; Pijls & Post 2009]). Nontrivial because kind directionality is arena-typed (IS_A is asymmetric; HAS_PROPERTY may be).
5. **Incremental / lifelong TAS** in the spirit of LPA* / D* for streaming ingestion: when ACG updates $\pi(a)$, can the open-set be re-prioritized without full restart?
6. **Determinism under parallel expansion.** TAS may need parallel frontier expansion (oneTBB) for very wide candidate sets; preserving deterministic tie-break across thread counts is non-trivial.

#### Publication framing

One-sentence pitch: *TAS is arena-admissible A* over a typed, content-addressed, rated-evidence DAG, with traversal mode collapsing to a single $\varepsilon$-bounded relaxation parameter and abstention as a first-class optimal terminal.*

- Workshop: ICAPS Workshop on Heuristics & Search; NeurIPS Workshop on Neuro-Symbolic AI.
- Conference: ICAPS, AAAI (heuristic search track), VLDB / SIGMOD (cascade-in-DB angle).
- Comparator baselines: Dijkstra over edge-rated graphs; weighted A* on knowledge graphs; beam search; Recursive-CTE traversal baselines; Monte Carlo tree search with UCB1.

#### References

- Dechter & Pearl, *Generalized best-first search strategies and the optimality of A*, J. ACM 1985.
- Edelkamp, Jabbar & Lluch-Lafuente, *Cost-Algebraic Heuristic Search*, AAAI 2005.
- Felner & Zahavi, *Inconsistent heuristics in theory and practice*, Artif. Intell. 2011.
- Goldberg, Harrelson, Kaplan & Werneck, *Efficient Point-to-Point Shortest Path Algorithms*, 2007.
- Hansen & Zhou, *Anytime Heuristic Search*, J. Artif. Intell. Res. 28, 2007.
- Hart, Nilsson & Raphael, *A Formal Basis for the Heuristic Determination of Minimum Cost Paths*, IEEE Trans. Syst. Sci. Cybern. 1968.
- Klein & Manning, *A* parsing: fast exact Viterbi parse selection*, NAACL 2003.
- Lowerre, *The Harpy Speech Recognition System*, PhD thesis, CMU 1976 (origin of beam search).
- Pearl, *Heuristics: Intelligent Search Strategies for Computer Problem Solving*, Addison-Wesley 1984.
- Pohl, *First results on the effect of error in heuristic search*, Machine Intelligence 5, 1970 (weighted A*).
- Russell, *Efficient memory-bounded search methods*, ECAI 1992 (SMA*).
- Russell & Norvig, *Artificial Intelligence: A Modern Approach*, 4th ed., Pearson 2018 (canonical A* / heuristic-search reference).
- Tillmann & Ney, *Word reordering and a dynamic programming beam search algorithm for statistical machine translation*, Comput. Linguistics 2003.
- Zhou & Hansen, *Beam-Stack Search: Integrating Backtracking with Beam Search*, ICAPS 2005.

---

### §3 GSC — Geometry-Sound Cascade pruning

#### Object
_Stub:_ physicality-derived candidate set $G(q)$ as soundness oracle for the cascade frontier.

#### Inputs / state / outputs
_Stub._

#### Update rule or query equation
_Stub._

#### Algorithm sketch
_Stub._

#### Complexity character
_Stub:_ $O(|G(q)| \cdot \log |A|)$ vs $O(|A|)$.

#### Novelty delta
_Stub:_ geometry as soundness oracle, not similarity proxy.

#### Substrate invariants relied on
_Stub:_ ADR 0029 (custom opclasses), ADR 0001/0025 (extend PostGIS).

#### Open problems
_Stub._

#### Publication framing
_Stub._

#### References
_Stub._

---

### §4 DCB — Drift / Creativity Budgeting

#### Object

A per-traversal *drift functional* and matching *budget policy* that quantify and bound how far a TAS (§2) path may depart from the high-support corroborated core of the substrate, with the traversal mode setting the budget cap. DCB is what makes "hallucination" a first-class measured quantity in the substrate rather than an emergent property of decoder temperature.

Let a candidate path $P = (a_1, \dots, a_\ell)$ traverse attestations with ARI weights $w(a_i)$ (§1), Glicko ratings $\mu(a_i)$, and rating-deviation $\phi(a_i)$ (§5). Define the *drift functional*:

$$
\mathrm{Drift}(P) \;=\; \sum_{i=1}^{\ell} \big(1 - \min(w(a_i), \tau_{\mathrm{mode}})\big) \;+\; \beta \sum_{i=1}^{\ell} \phi(a_i),
$$

with $\beta \ge 0$ the rating-uncertainty weighting. Mode supplies a budget cap $B_{\mathrm{mode}}$:

| Mode | $\tau_{\mathrm{mode}}$ | $B_{\mathrm{mode}}$ | Behavior |
|---|---|---|---|
| strict | 0.95 | 0 | only fully-corroborated paths; otherwise $\bot$ |
| standard | 0.7 | small | low-drift inference; rare abstention |
| speculative | 0.4 | moderate | accepts low-rated attestations with bounded count |
| creative | 0.1 | large | actively prefers high-drift paths up to cap |

A path is *admissible under mode* iff $\mathrm{Drift}(P) \le B_{\mathrm{mode}}$.

#### Inputs / state / outputs

- **Inputs.** Path under construction $P$; mode parameters $(\tau_{\mathrm{mode}}, B_{\mathrm{mode}}, \beta)$; per-attestation $(w, \mu, \phi)$ from §1/§5.
- **State.** Running drift accumulator on each frontier node in TAS (one `int64` fixed-point at scale $10^9$ per STANDARDS.md); per-mode performance-profile telemetry (Zilberstein 1996) for anytime monitoring.
- **Outputs.** Boolean admissibility per path; quantitative drift report attached to every emitted result triple; mode-conditional credibility score $\mathrm{Cred}(P) = \exp(-\mathrm{Drift}(P) / B_{\mathrm{mode}})$.

#### Update rule or query equation

Drift is monotone non-decreasing along path extension: $\mathrm{Drift}(P \cdot a) \ge \mathrm{Drift}(P)$ with equality only when $w(a) \ge \tau_{\mathrm{mode}}$ and $\phi(a) = 0$. This monotonicity is required for DCB to compose with TAS as a pruning predicate (a path that exceeds budget cannot be rescued by extension).

DCB couples to TAS by adding a drift-feasibility check to `admissible_extensions` and a drift-aware heuristic correction:

$$
f_{\mathrm{DCB}}(n) \;=\; g(n) + \varepsilon_{\mathrm{mode}} \cdot h(n) \;+\; \gamma_{\mathrm{mode}} \cdot \max(0, \mathrm{Drift}(P_n) - B_{\mathrm{mode}}/2),
$$

with $\gamma_{\mathrm{mode}}$ a soft-penalty multiplier. The penalty kicks in only past the half-budget point, preserving optimality of low-drift solutions while gradually discouraging excursion.

Coverage guarantee (split-conformal style, cf. Vovk/Gammerman/Shafer 2005 [Algorithmic Learning in a Random World]; Angelopoulos & Bates 2023 [Foundations and Trends in ML]): if drift is calibrated on a held-out source-arena split, choosing $B_{\mathrm{mode}}$ at the $(1-\alpha)$ quantile of historical drift on correct retrievals guarantees that strict-mode retrievals are correct with marginal probability $\ge 1 - \alpha$ under exchangeability of evaluations within an arena. This is the substrate analogue of conformal prediction's distribution-free coverage, with non-conformity score $=$ drift.

#### Algorithm sketch

```
extend_with_dcb(P, a, mode):
  d_new \u2190 drift(P) + (1 - min(w(a), \u03c4_mode)) + \u03b2 * \u03c6(a)
  if d_new > B_mode:
      return INFEASIBLE                 # hard cap; prune subtree
  return Path(P + a, drift = d_new)

emit_result(P_terminal, mode):
  M \u2190 ari_support(P_terminal)            # \u00a71
  d \u2190 drift(P_terminal)
  cred \u2190 exp(-d / B_mode)
  return (object_of(P_terminal), M, d, cred, path = P_terminal)

monitor_anytime(results_so_far, mode, t_elapsed):
  # Zilberstein performance profile
  if results_so_far.empty and t_elapsed > t_min(mode):
      return ABSTAIN_NO_FEASIBLE
  if best_result.M \u2265 \u03c4_mode and result.drift \u2264 B_mode/4:
      return EARLY_RETURN                # high quality early
```

#### Complexity character

Drift accumulation is $O(1)$ per frontier extension (one fixed-point add). No DCB-specific search complexity overhead; it acts as a pruning predicate on TAS that *reduces* the effective branching factor in strict and standard modes by aborting subtrees once $\mathrm{Drift}(P) > B_{\mathrm{mode}}$ regardless of $g, h$. Calibration of $B_{\mathrm{mode}}$ via conformal split is $O(N \log N)$ for sorted quantile lookup over a calibration set of size $N$ \u2014 offline, not on the hot path.

#### Novelty delta

Not new:

- Anytime algorithms with performance profiles [Dean & Boddy 1988; Boddy & Dean 1989; Zilberstein 1996 AI Magazine; Hansen & Zhou 2007 JAIR].
- Conformal prediction for distribution-free coverage [Vovk, Gammerman & Shafer 2005; Saunders, Gammerman & Vovk 1999; Papadopoulos et al. 2002 ECML (inductive CP); Angelopoulos & Bates 2023].
- Mondrian conformal prediction for class-conditional / group-conditional calibration [Vovk et al. 2003; Toccaceli & Gammerman 2019 Machine Learning].
- Bounded-rationality decision theory [Horvitz 1986/1988; Russell & Wefald 1991].
- Selective classification / classification with reject [Chow 1957/1970; El-Yaniv & Wiener 2010 JMLR].
- Hallucination measurement in LLMs as a *downstream* metric (TruthfulQA, HELM, etc.).

What *is* new in DCB:

1. **Drift is intrinsic to the cascade, not a post-hoc score.** Every TAS expansion knows its accumulated drift; abstention is a search termination, not a confidence filter on outputs.
2. **Hallucination has units.** Drift is a sum of bounded contributions in $[0, 1+\beta\phi_{\max}]$ per edge, in deterministic int64 fixed-point at scale $10^9$ per STANDARDS.md. It is comparable across queries, modes, sources, and arenas.
3. **Mode is the budget knob, not temperature.** The strict\u2192creative axis directly sets $(\tau_{\mathrm{mode}}, B_{\mathrm{mode}}, \beta, \gamma_{\mathrm{mode}})$ \u2014 no sampling stochasticity, no nucleus / top-$k$ / top-$p$ heuristics. Two creative-mode calls with identical query and identical substrate state produce identical results (STANDARDS.md determinism).
4. **Mondrian-by-arena conformal calibration.** Drift quantiles are calibrated *per arena* (ADR 0036), not globally; the coverage guarantee localizes to the arena in which the query is asked.
5. **Drift composes with arena policy.** A path that is arena-incompatible cannot reduce its drift cost; DCB does not let a creative-mode caller silently relax kind-compatibility (those are TAS-level inadmissible, drift only governs the *graded* dimension).
6. **Monotonicity-by-construction.** $\mathrm{Drift}(P \cdot a) \ge \mathrm{Drift}(P)$ holds by definition, so DCB-pruning composes with admissible heuristics in TAS without breaking optimality of low-drift solutions.

#### Substrate invariants relied on

- ADR 0035 \u2014 compiled cascade SRF owns the frontier; DCB lives in the same C/C++ extension as TAS, not in app-layer post-processing.
- ADR 0036 \u2014 arena policy supplies hard kind compatibility; DCB governs *graded* admissibility only.
- STANDARDS.md \u2014 int64 fixed-point at scale $10^9$ for drift accumulator; deterministic across machines (no float in hot path).
- §1 ARI \u2014 supplies $w(a)$; §5 ACG \u2014 supplies $\mu(a), \phi(a)$.
- GLOSSARY.md `traversal mode` entry \u2014 strict / standard / speculative / creative semantics.

#### Open problems

1. **Calibration target.** Should $B_{\mathrm{mode}}$ be calibrated for marginal coverage over all queries, or *conditional* on query shape (subject popularity, arena depth)? Conditional coverage is famously hard [Vovk 2012; Barber et al. 2021 *Limits of distribution-free conditional predictive inference*].
2. **Drift-rate vs drift-total.** A long path with low per-edge drift can equal a short path with high per-edge drift; per-edge max constraint vs cumulative budget may behave differently. Empirical question.
3. **Drift discounting.** Should later attestations on the path count less (geometric discount)? Argues both ways: structurally distant claims have less individual support but more compositional risk.
4. **Adversarial drift.** A source that floods the substrate with high-$w$ but inconsistent claims could artificially deflate drift; couples with source-trust (ADR 0044) and ACG (§5) credibility erosion.
5. **Online conformal recalibration.** When ACG (§5) updates ratings mid-session, the quantile of historical drift moves; how often to re-calibrate $B_{\mathrm{mode}}$?
6. **Reject region geometry.** Selective-classification theory [Geifman & El-Yaniv 2017 NeurIPS; Cortes/DeSalvo/Mohri 2016 ALT] gives risk-coverage curves; computing the substrate analogue (drift-coverage curves per arena) would let users pick mode by SLA, not by name.

#### Publication framing

One-sentence pitch: *DCB makes hallucination a measured, mode-budgeted, deterministically-computed traversal quantity with split-conformal coverage guarantees per arena, replacing decoder-temperature heuristics.*

- Workshop: NeurIPS Workshop on Distribution-Free Uncertainty Quantification; COPA (Conformal & Probabilistic Prediction with Applications).
- Conference: AAAI / IJCAI (selective classification + heuristic search); UAI; ICAPS (mode-budgeted anytime planning).
- Comparator baselines: classification-with-reject baselines (Chow's rule, El-Yaniv-Wiener selective classifier); conformal prediction sets; nucleus / top-$p$ LLM decoding; temperature scaling.

#### References

- Angelopoulos & Bates, *Conformal Prediction: A Gentle Introduction*, Foundations & Trends in ML 16(4), 2023; preprint arXiv:2107.07511, 2021.
- Barber, Cand\u00e8s, Ramdas & Tibshirani, *The limits of distribution-free conditional predictive inference*, Information & Inference 10(2), 2021.
- Boddy & Dean, *Solving time-dependent planning problems*, IJCAI 1989.
- Chow, *On optimum recognition error and reject tradeoff*, IEEE Trans. IT 16(1), 1970.
- Cortes, DeSalvo & Mohri, *Learning with Rejection*, ALT 2016.
- Dean & Boddy, *An analysis of time-dependent planning*, AAAI 1988.
- El-Yaniv & Wiener, *On the foundations of noise-free selective classification*, JMLR 11, 2010.
- Geifman & El-Yaniv, *Selective Classification for Deep Neural Networks*, NeurIPS 2017.
- Hansen & Zhou, *Anytime Heuristic Search*, JAIR 28, 2007.
- Horvitz, *Reasoning about beliefs and actions under computational resource constraints*, UAI 1987/1988.
- Papadopoulos, Proedrou, Vovk & Gammerman, *Inductive Confidence Machines for Regression*, ECML 2002 (inductive / split conformal).
- Russell & Wefald, *Do the Right Thing: Studies in Limited Rationality*, MIT Press 1991.
- Saunders, Gammerman & Vovk, *Transduction with Confidence and Credibility*, IJCAI 1999.
- Toccaceli & Gammerman, *Combination of inductive Mondrian conformal predictors*, Machine Learning 108(3), 2019.
- Vovk, Gammerman & Shafer, *Algorithmic Learning in a Random World*, Springer 2005 (2nd ed. 2022).
- Zilberstein, *Using Anytime Algorithms in Intelligent Systems*, AI Magazine 17(3), 1996.

---

## Part III — Rating, trust, conflict

### §5 ACG — Arena-Coupled Glicko field

#### Object

A coupled dynamical system over two state surfaces:

- per-attestation Glicko-2 state $\pi(a) = (\mu, \phi, \sigma_{g2})$ for every $a \in A$,
- per-source-kind credibility $\rho(\sigma, k) \in \mathbb{R}$ for every $(\sigma, k) \in \Sigma \times K$,

related by mutual update under typed arena policy. The classical Glicko / Glicko-2 system [Glickman 1995; Glickman 2013] is the special case where $|K|=1$, all attestations belong to a single arena, and $\rho$ is constant.

#### Inputs / state / outputs

- **Inputs.** A new observation event $\mathcal{O} = (a, r, \sigma_{obs}, c_{obs})$: an attestation $a$ being observed by source $\sigma_{obs}$ in context $c_{obs}$ with raw signal $r \in [0,1]$ (full assertion = 1, contradiction = 0, partial / hedged = intermediate; per kind policy).
- **State.** $\pi : A \to \mathbb{R}^3$ and $\rho : \Sigma \times K \to \mathbb{R}$.
- **Outputs.** Updated $\pi'(a)$ and updated $\rho'(\sigma_{obs}, k_a)$. Implemented as the substrate's only SQL-side aggregate (RULES.md R6).

#### Update rule or query equation

Translate the observation into a Glicko-2-style "virtual match" against the current effective answer for the kind/context/scope. Let $\bar\mu = \mu\big(M(s_a, k_a, o_a, c_a)\big)$ be the substrate's pre-observation effective answer (\u00a71 ARI). Define the score $s$ for the virtual match as

$$
s \;=\; r \cdot W(c_{obs}, c_a, k_a)
$$

where $W$ is the kind-policy compatibility weight (a contradicting observation from an incompatible context contributes a fractional, signed score, not a full vote).

Run the Glicko-2 update [Glickman 2013] against the virtual opponent of mean $\bar\mu$ and RD set to the structural-support RD floor of $k_a$:

$$
v^{-1} = g(\bar\phi)^2\, E\,(1-E), \qquad
\Delta = v \cdot g(\bar\phi)\,(s - E),
$$

with $g(\phi) = (1 + 3\phi^2/\pi^2)^{-1/2}$ and $E = \sigma\big(g(\bar\phi)(\mu - \bar\mu)\big)$. New volatility $\sigma_{g2}'$ is obtained by the Illinois-method root of $f(x)$ as in Glickman's spec; new RD and rating are

$$
\phi' = \big(\tfrac{1}{\phi^2 + \sigma_{g2}'^2} + \tfrac{1}{v}\big)^{-1/2}, \qquad
\mu' = \mu + \phi'^2 \, g(\bar\phi)\,(s - E).
$$

Then the **source-credibility coupling**:

$$
\rho'(\sigma_{obs}, k_a) \;=\; \mathrm{clip}\!\Big[\,\rho(\sigma_{obs}, k_a) + \eta_k \cdot L(\sigma_{obs}, A_{-a}) \cdot (\mu' - \bar\mu),\; \rho_{\min}(\text{class}), \rho_{\max}(\text{class})\Big]
$$

where $\eta_k$ is a per-kind learning rate, $L$ is lineage-independence (\u00a71), and the clip respects the ADR 0044 source-trust-class envelope (a Class 9 "User Prompt" source cannot escape the Class 9 effective-μ multiplier band). This is the "the substrate learns who is reliable from the evidence the same evidence is settling" loop \u2014 dual to the truth-discovery iteration of [Yin/Han/Yu 2008] and [Pasternack & Roth 2010 *Knowing what to believe*], but as a Bayesian-online single-pass update rather than a batch fixed-point iteration.

#### Algorithm sketch

Per observation event (executed inside the Postgres custom aggregate `acg_update`, the only piece of substrate math allowed SQL-side per RULES.md R6):

```
acg_update(\u03c0(a), \u03c1(\u03c3_obs,k_a), observation):
  bar_mu, bar_phi \u2190 effective_answer(s_a,k_a,o_a,c_a)     # \u00a71
  s, W \u2190 score_from_observation(r, c_obs, c_a, k_a)
  (\u03bc', \u03c6', \u03c3_g2') \u2190 glicko2_step(\u03c0(a), bar_mu, bar_phi, s)
  L \u2190 lineage_independence(\u03c3_obs, A_minus_a)
  \u03c1' \u2190 clip(\u03c1 + \u03b7_k \u00b7 L \u00b7 (\u03bc' - bar_mu),
              \u03c1_min(trust_class), \u03c1_max(trust_class))
  return (\u03c0', \u03c1')
```

No iteration to fixed point per event; ACG is the *online* trajectory whose attractor SCD (\u00a76) and DCB (\u00a74) can probe.

#### Complexity character

$O(1)$ per observation in the per-attestation half; $O(1)$ per observation in the per-source half. Across an ingestion of $N$ observations, total work is $O(N)$. The ARI lookup `effective_answer(...)` inside the update is $O(|C| \cdot \bar d)$ from \u00a71 but bounded by the candidate cap; aggressive cache reuse is possible because consecutive observations on the same $(s,k,c)$ share the same $\bar\mu$.

Compare:

- Glicko-2 per-player: $O(m)$ per rating period over $m$ games \u2014 same shape, smaller surface.
- TrueSkill EP: $O(\text{iters} \cdot m)$ message passing per match.
- TrueSkill Through Time [Dangauthier et al. 2007]: $O(T \cdot m)$ smoothing over the full time series \u2014 ACG\u2019s arena-cohort offline mode is the analog and is genuinely optional.
- Whole-History Rating [Coulom 2008]: batch MAP over all history; ACG provides the online equivalent and falls back to a similar batch recompute when arena cohorts shift.

#### Novelty delta

What is *not* new:

- Glicko-2's RD/volatility machinery is taken intact from [Glickman 2013]; we do not re-derive it.
- Source-trust + claim-trust mutual updates are the standard truth-discovery loop [Yin et al. 2008; Pasternack & Roth 2010; Zhao et al. 2012].
- TrueSkill family [Herbrich/Minka/Graepel 2007] showed Bayesian online rating with uncertainty; TrueSkill Through Time [Dangauthier et al. 2007] showed smoothed retrospective updates.

What *is* new in ACG:

1. **Per-attestation Glicko-2 indexed by typed edges of a content-addressed evidence DAG.** Glicko, Glicko-2, Elo, TrueSkill, TrueSkill2 rate *players*; truth-discovery rates *(source, value)* pairs. ACG rates *(subject, kind, object, source, context)* tuples \u2014 a typed-edge object that does not exist in any rating-system or truth-discovery framework.
2. **Arena-typed virtual opponent.** The "match" is observation-vs-current-effective-answer, scored by kind-policy compatibility, not player-vs-player.
3. **Source-credibility coupling is online, lineage-shrunk, and trust-class clamped.** Truth-discovery couples source and claim trust iteratively to a batch fixed point; ACG does it online with explicit lineage shrinkage and ADR 0044 trust-class envelopes, making coordinated low-trust adversaries provably bounded by the envelope.
4. **Observation update scope is a first-class kind property.** Some kinds update globally; others stay scoped to source / session / prompt. No prior system encodes this at the rating-update level (RULES.md R19/R20, ADR 0036).
5. **SQL-side custom aggregate.** ACG is intentionally the *only* math that crosses the DB boundary, exploiting Postgres' aggregate machinery for parallel-safe combine and a single, auditable update surface.

#### Substrate invariants relied on

- ADR 0036 arena policy (compatibility, cardinality, conflict, observation update scope, structural support).
- ADR 0044 trust-class envelopes for $\rho$ clipping.
- RULES.md R6 (only Glicko update runs SQL-side; the aggregate is the surface).
- STANDARDS.md (int64 fixed-point at scale $10^9$; deterministic across machines).
- ADR 0035 (the cascade reads ACG state; it does not embed ACG steps in the hot path).

#### Open problems

1. **Existence/uniqueness of the coupled attractor.** Given a finite, sealed evidence stream, does $(\pi, \rho)$ converge? Truth-discovery convergence proofs exist for batch iteration [Pasternack & Roth 2010]; ACG's online dynamics need a Lyapunov argument.
2. **Adversarial robustness bound.** Quantify the worst-case shift in $\mu^*$ for $o^*$ achievable by an attacker constrained to trust class $T_j$. Likely $O(\eta_k \cdot (\rho_{\max}(T_j) - \rho_{\min}(T_j)))$ per attestation, but a real bound is open.
3. **Choice of $\eta_k$.** Per-kind learning rate; cross-validation against held-out high-trust observations is the obvious estimator, but circularity with $\rho$ is a concern.
4. **Lineage shrinkage $L$ operationalization.** Requires a measurable definition; potentially MDS $\Delta\mathrm{SDL}$ on source contribution.
5. **Through-time smoothing.** A batch recomputation in the spirit of TrueSkill Through Time / Whole-History Rating would help when arena cohorts shift; cost, schedule, and reconciliation with online state are open.
6. **Multi-truth kinds.** Kinds with set-valued targets need a Glicko update against a *set* of contenders; the right generalization (per-element parallel update vs joint update) is unsettled.

#### Publication framing

One-sentence pitch: *ACG is the first per-typed-edge Bayesian online rating system, coupling Glicko-2 dynamics on attestations to lineage-shrunk, trust-class-clamped source credibility under arena policy.*

- Workshop: ICML Workshop on Uncertainty & Robustness; NeurIPS Workshop on Bayesian Deep Learning.
- Conference: AAAI, UAI (uncertain reasoning), SIGMOD (online aggregation angle).
- Comparator baselines: Glicko-2 single-arena; TrueSkill / TrueSkill Through Time; TruthFinder; LTM; Whole-History Rating.

#### References

- Coulom, *Whole-history rating: A Bayesian rating system for players of time-varying strength*, Computers and Games 2008.
- Dangauthier, Herbrich, Minka & Graepel, *TrueSkill Through Time: Revisiting the History of Chess*, NeurIPS 2007.
- Glickman, *The Glicko System*, 1995.
- Glickman, *Example of the Glicko-2 system*, 2013.
- Herbrich, Minka & Graepel, *TrueSkill: A Bayesian Skill Rating System*, NeurIPS 2006/2007.
- Minka, Cleven & Zaykov, *TrueSkill 2: An improved Bayesian skill rating system*, MSR-TR-2018-8.
- Pasternack & Roth, *Knowing what to believe (when you already know something)*, COLING 2010.
- Yin, Han & Yu, *Truth Discovery with Multiple Conflicting Information Providers on the Web*, IEEE TKDE 2008.
- Zhao, Rubinstein, Gemmell & Han, *A Bayesian approach to discovering truth from conflicting sources*, VLDB 2012.

---

### §6 SCD — Sheaf-Cohomology Conflict Detection

#### Object
_Stub:_ sheaf over the context category; stalks = locally consistent attestations; restriction maps = arena compatibility policy.

#### Inputs / state / outputs
_Stub._

#### Update rule or query equation
_Stub:_ global section ↔ consensus; nontrivial $H^1$ ↔ disputed answer with localized obstruction.

#### Algorithm sketch
_Stub._

#### Complexity character
_Stub._

#### Novelty delta
_Stub:_ principled conflict localization vs ad-hoc disagreement scores.

#### Substrate invariants relied on
_Stub:_ ADR 0036 (arena policy).

#### Open problems
_Stub:_ formalize restriction maps; characterize sheaves with arena-consistent global sections.

#### Publication framing
_Stub._

#### References
_Stub._

---

### §7 PLT — Prompt-Local Tug (non-truth-granting)

#### Object
_Stub:_ operator that lets prompt-local content tug existing entity links during cascade without promoting prompt-local claims to broader-arena truths.

#### Inputs / state / outputs
_Stub._

#### Update rule or query equation
_Stub._

#### Algorithm sketch
_Stub._

#### Complexity character
_Stub._

#### Novelty delta
_Stub:_ distinguishes content (occurrence/order/composition) from claim; explicit promotion calculus.

#### Substrate invariants relied on
_Stub:_ ADR 0035 + R19/R20 (prompt-local scoping), ADR 0044 T11 trust class.

#### Open problems
_Stub:_ promotion thresholds; cross-session leakage tests; adversarial prompt-injection bounds.

#### Publication framing
_Stub._

#### References
_Stub._

---

## Part IV — Generation, synthesis, decoding

### §8 SED — Suffix-Extension Decoding

#### Object
_Stub:_ suffix-extension operator $\mathrm{Ext}(t, k) = \arg\max_e M(\mathrm{tail}(t), k, e \mid \mathrm{mode})$ over typed next-step attestations.

#### Inputs / state / outputs
_Stub._

#### Update rule or query equation
_Stub._

#### Algorithm sketch
_Stub._

#### Complexity character
_Stub:_ indexed lookup + ranked expansion per token vs forward pass per token.

#### Novelty delta
_Stub:_ substrate-native autoregression; no softmax over vocabulary.

#### Substrate invariants relied on
_Stub:_ trajectory physicalities; ADR 0035.

#### Open problems
_Stub:_ behavior on long-novel suffixes; calibration of mode thresholds; relation to n-gram and PPM coders.

#### Publication framing
_Stub._

#### References
_Stub._

---

### §9 RIM — Reverse-Index Motif Reuse

#### Object
_Stub:_ reverse index from shared subtrees (Merkle child hashes) to all trajectories containing them; reuses motifs across generation tasks.

#### Inputs / state / outputs
_Stub._

#### Update rule or query equation
_Stub._

#### Algorithm sketch
_Stub._

#### Complexity character
_Stub._

#### Novelty delta
_Stub:_ generation-time motif reuse without retrieval-time copy/paste hacks.

#### Substrate invariants relied on
_Stub:_ content-addressing; Merkle DAG.

#### Open problems
_Stub:_ motif granularity selection; attribution; quality vs novelty tradeoff.

#### Publication framing
_Stub._

#### References
_Stub._

---

### §10 DSS — Deterministic Substrate Synthesis

#### Object
_Stub:_ deterministic map from $(\mathcal{S}, \mathrm{recipe})$ to a sparse tensor package, with exact-zero structural sparsity where $M \le \tau_{\mathrm{lt}}$.

#### Inputs / state / outputs
_Stub._

#### Update rule or query equation
_Stub:_ $W_{ij} = \Psi(M(e_i, k_{ij}, e_j))$ if $M > \tau_{\mathrm{lt}}(i,j)$ else $0$.

#### Algorithm sketch
_Stub._

#### Complexity character
_Stub._

#### Novelty delta
_Stub:_ compilation, not training; reproducible from state; sparse by construction.

#### Substrate invariants relied on
_Stub:_ ADR 0037 (codec); lottery-ticket-aware sparsity (RULES.md R3); STANDARDS.md (fixed-point ratings, float64 coords).

#### Open problems
_Stub:_ recipe expressiveness; faithfulness metrics vs source model; quantization interaction; GGUF proof export edge cases.

#### Publication framing
_Stub._

#### References
_Stub._

---

## Part V — Compression, lineage, lifecycle

### §11 MDS — Merkle-Dedup Substrate description length

#### Object
_Stub:_ substrate description length $\mathrm{SDL}(X) = |\mathrm{shared}(X)| + |\mathrm{trajectories}(X)|$ as Merkle-DAG-relativized Kolmogorov-ish upper bound.

#### Inputs / state / outputs
_Stub._

#### Update rule or query equation
_Stub._

#### Algorithm sketch
_Stub._

#### Complexity character
_Stub._

#### Novelty delta
_Stub:_ ingestion-time novelty detection; correlated-source / lineage discovery via $\Delta\mathrm{SDL}$.

#### Substrate invariants relied on
_Stub:_ BLAKE3 content-addressing; T0 termination.

#### Open problems
_Stub:_ relation to MDL and Kolmogorov complexity; calibration for noisy near-duplicates; adversarial source crafting.

#### Publication framing
_Stub._

#### References
_Stub._

---

### §12 LTU — Lineage-Tracing Unlearning

#### Object
_Stub:_ unlearning operator $\mathcal{S}' = \mathcal{S} \setminus \{a : \sigma_a = \sigma_{\mathrm{remove}}\}$ with exact re-synthesis from $\mathcal{S}'$.

#### Inputs / state / outputs
_Stub._

#### Update rule or query equation
_Stub._

#### Algorithm sketch
_Stub._

#### Complexity character
_Stub._

#### Novelty delta
_Stub:_ provably reversible removal; contrasts approximate-unlearning literature.

#### Substrate invariants relied on
_Stub:_ source-lineage metadata; deterministic synthesis (§10).

#### Open problems
_Stub:_ partial-source removal semantics; downstream attestation cascades; legal/compliance framing (GDPR right-to-erasure).

#### Publication framing
_Stub._

#### References
_Stub._

---

## Part VI — Cross-cutting and meta

### §13 Open mathematical questions

_Stub:_ collect cross-family open problems here — e.g., joint convergence of ACG + ARI, sheaf-A* duality, drift-budget / cohomology relationships, sparsity-as-cohomology.

### §14 Empirical calibration plan

_Stub:_ which substrate snapshots, which sources, which probes, which baselines. Tie to OPERATIONS.md `just verify` and to integration CI.

### §15 Publication targets and venues

_Stub:_ candidate venues by family:

- ARI / DCB / SED / RIM → NeurIPS / ICML workshop on retrieval and generation
- TAS / GSC → SIGMOD / VLDB
- ACG / SCD → AAAI / UAI; applied category theory workshops for SCD
- DSS / LTU → MLSys; ML & Law (LTU)
- MDS → DCC / ITW
- PLT → safety/alignment workshops

### §16 Glossary delta vs `GLOSSARY.md`

_Stub:_ list every new term introduced here that is not yet in `GLOSSARY.md`. When a term stabilizes, propose its promotion (user authorization required per RULES.md R12).

### §17 Roadmap: order of filling

Proposed order (one family per pass, each pass fills the full template):

1. **§0.1, §0.2** — foundations (must precede any family).
2. **§1 ARI** — most load-bearing; everything downstream uses $M$ and $w$.
3. **§5 ACG** — defines how $\mu$ and $\rho$ evolve, which §1 depends on.
4. **§2 TAS** — formalizes the cascade.
5. **§4 DCB** — gives modes their teeth.
6. **§7 PLT** — closes the prompt-local loop with §1/§2/§4.
7. **§10 DSS** — emission side; needs §1 + §5 stable.
8. **§12 LTU** — falls out of §10 cleanly.
9. **§11 MDS** — independent; can slot earlier if calendar allows.
10. **§3 GSC** — performance/pruning layer; needs §2.
11. **§8 SED** — generation; needs §1 + §2.
12. **§9 RIM** — generation optimization; needs §8 + §11.
13. **§6 SCD** — most speculative; do last so we know what consensus actually looks like in practice.

Each pass ends with: (a) updated **References** subsection, (b) glossary delta noted in §16, (c) any cross-cutting open problems lifted into §13.
