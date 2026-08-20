# Model ingestion — the design

Status: design. Every constraint below traces to a measurement in
`docs/archive/reports/MODEL_LANE_AUDIT_2026-08-11.md`; nothing here is argued from a code comment.

## The one-line statement

**A checkpoint is a witness, not a corpus.** Its declared structure is an AST to
scrape, its tokenizer is an encoding to decode, and its weights are the graded
significance of votes on edges between entities that already exist. Nothing in a
checkpoint is content. The ingest writes attestations and retains no floats.

The current lane inverts all three: it resolves tokenizer surfaces as identity,
mints entities for BPE fragments, and stores weights as geometry payload. Measured
result: 87.4 GB and 7,110 minted entities from two checkpoints, projecting to
23.1 TB for the local library and 110 TB for the frontier models.

---

## Why the weights are worth reading at all

Curated sources give typed facts: `king IS_A monarch`. They do not give **graded
pull** — that `king` tugs harder on `queen` than on `magistrate`. That gradation
is corpus-derived, it is the only thing in a checkpoint obtainable nowhere else,
and it is exactly a Glicko magnitude.

Two caveats that set the trust class:

- A weight is **frequency-weighted co-occurrence over an unattributed corpus.**
  There is no field in a float saying which document contributed. Curated sources
  carry provenance; weights do not.
- A frequency model **cannot represent refutation.** The web mentions flat earth
  constantly, overwhelmingly to debunk it; co-occurrence reads every one of those
  as support. `laplace.attestations.outcome` and the `feedback` surface
  ("Glicko win/loss on an edge") read the same corpus with the opposite sign.

So the model enters as `AIModelProbe` — testimony that must be adjudicated, never
authority. `kind_rank × source_trust → φ` is the mechanism: a probe moves a rating
less than a curated witness does.

---

## Preconditions — none of this is optional, and one is currently violated

**P1. Tier 0 complete.** ✓ Verified: 1,114,240 entities against Unicode's
1,114,112 plus sentinels. Consequence: *no tokenizer can introduce a novel atom.*
Measured across 25 tokenizers, the codepoint union saturates at **1,145** and is
flat from model 4 onward.

**P2. Decomposition correct. ✗ CURRENTLY VIOLATED.** Measured on the TinyLlama
ingest:

```
tier                pre-existing   minted by this ingest
0  codepoint            2,186              0     <- correct
2  word                17,438          6,141     <- 6,141 BPE fragments as WORDS
3  sentence                26            936
4  document                 0             33
```

`undert`, `Ev`, `Sa`, `str`, `}(\`, `CGFloat` are now tier-2 word entities with
the same standing as `understand`. This is worse than waste: the next model will
surface-match them, *find* them, and consensus will accumulate witnesses on
fragments that were never words. `rd` falls, the junk gains confidence, and the
noise floor manufactures itself. Fix before any further model ingest (#1014).

**P3. Curated sources ingested first. ✓ SATISFIED — and my earlier claim that it
was not was wrong.** An audit pass read `ops.source_status()`, saw no `ConceptNet`
row, and concluded the graph lacked world knowledge. That is the same
consult-the-description-instead-of-the-system error this document warns about in
its header. What is actually ingested:

```
ChessAnalysis    15,560,776      WordNet       2,594,670      CILI      1,430,900
OMWDecomposer     7,028,182      Unicode       1,644,189      FrameNet    724,768
ChessPgn          1,520,978      PropBank/VerbNet/SemLink/MapNet/ISO639/PredicateMatrix
```

Plus the 208 MB reference corpus in `/vault/Data/test-data/text` — Britannica 1911
in ~150 volumes, Webster's Unabridged 1913, Roget's Thesaurus, Boyle, Bateson,
Babbage, Conan Doyle — which `ops.source_status()` does **not** list because
document sources are content-hash keyed, not name-keyed (#1019). Verified present:

```sql
SELECT tier, count(*) FROM structural.containers_of(laplace.word_id('Sherlock'), 2, 20000)
GROUP BY 1;   -- tier 3: 119 sentences | tier 4: 10 documents
```

returning exact Conan Doyle and Britannica text. So a model votes into a graph that
already holds definitions, synonym classes, and densely cross-referencing world
facts. Disagreement is adjudicable from the first checkpoint.

---

## This is an ISA program, not a bespoke emitter

`docs/specs/37_Substrate_Operation_ISA.md` names **model-analysis** in its contract:
"Every read, generation, game, code, model-analysis, and export path is a typed
program over the operation families." The current lane is not one. It does not
RESOLVE, ROUTE, COMPOSE, STEER, SELECT or WITNESS — it computes floats and writes
payload, bypassing the instruction set entirely. That is why its output cannot be
adjudicated: it never entered the machine.

The phases below are therefore opcodes, not a private pipeline, and the
implementation law applies: "one canonical implementation per operation fact...
endpoint-specific helpers delegate to the same program."

| phase | opcode | contract |
|---|---|---|
| tokenizer decode → codepoints → compose | **OP0 RESOLVE** | surface/content/scope → canonical ids |
| AST scrape, band/relation choice | **OP2 ROUTE** | context → typed relations, bands, constraints |
| existing edges among nameable entities | **OP3 SCAN** | typed query → bounded indexed candidates |
| contract the tensor tile → couplings | **OP4 COMPOSE** | candidates/evidence → typed frontier |
| candidate couplings | **OP5 PROPOSE** | frontier → valid next entities |
| magnitude → significance × `AIModelProbe` trust | **OP6 STEER** | proposals + evidence → ranked |
| admission (corroborated or matching) | **OP7 SELECT** | ranked + policy → selected |
| Glicko observation | **OP9 WITNESS** | outcome → append-only testimony |

Two consequences that change the work:

- **OP8 REALIZE is absent on the ingest path, and should be.** Ingest produces no
  external surface. OP9 follows "an actual outcome; it is never an implicit side
  effect of a read" — so a contraction that admits nothing writes nothing.
- **The loop is OP2–OP8**, per the ordering rule, which is where multi-hop and
  multi-constituent work lives. Corroboration (phase 5b) is a loop iteration, not a
  post-filter: each pass re-routes and re-scans with the evidence the previous pass
  admitted. That is the same OODA shape `docs/specs/15_Godel_Engine_OODA_Loop.txt`
  governs, and it is why the admission threshold is discovered rather than configured.

Receipts are mandatory and currently absent: "every completed program can report its
operation sequence, inputs, source/context scope, candidate reductions, evidence
cells, scoring policy, selected identity, realization, and writes." A model ingest
should emit one. The `factors` pass emits 87.4 GB and no receipt.

## Placement: identity, geometry, and order are three different organs

Verified on the substrate, not argued:

```
        Blake3-Merkle id                    coord (md5)      hilbert          trajectory (md5)
cat     baa7b0f6b4ba7abc947f62751f00b496    2365caa0...      9ffc39c8...      e39d74d4...
tac     8565ebfd40052b4135cdeff25a652382    f4c1317b...      9ffc39c8...      c505c333...
act     02258af704a436e3470c1a66570837d6    2365caa0...      9ffc39c8...      ffb27915...
```

Three anagrams over identical tier-0 constituents. **Geometry collides on all
three** — same Hilbert bucket, and `cat`/`act` are bit-identical in `coord`.
**Identity separates all three** — ordered `childIds` in the Merkle hash.
**Trajectory separates all three** — order is carried on the curve.

This is the ISA's "exact identity from spatial candidate discovery" made concrete,
and it dictates three things the model lane must get right:

1. **A slice coordinate is a bucket, not an identity.** Placing two circuits at the
   same coordinate is *correct behaviour*, not a collision to engineer around. The
   `coord` GiST index (`physicalities_coord_gist`, `gist_geometry_ops_nd`) is the
   OP3 SCAN organ — narrow spatially, then resolve exactly by hash.
2. **Order-bearing structure belongs on the trajectory, not in the coordinate.** A
   circuit's token ordering is recoverable from a trajectory and unrecoverable from
   a centroid.
3. **Distance choice follows from that.** Metrics split by whether they respect order:

   | metric | on cat/tac/act | use |
   |---|---|---|
   | angular on `coord`, Hausdorff, **Karcher mean** | collide | bucket-level candidate discovery only |
   | **Fréchet**, DTW | separate | any comparison of ordered circuit trajectories |

**Open defect in placement.** `‖cat‖ = 0.2416` — the placement is a *Euclidean*
centroid of constituent coordinates, which falls inside the 4-ball rather than on
S³, and `radius_origin` exists to carry the shortfall. A **Karcher (Riemannian)
mean** is the intrinsic barycenter and would land on the surface at `‖·‖ = 1`.
Whether the ball interior is intended (radius carrying salience) or is an artifact
of using the wrong mean is unresolved and should be decided before the model lane
places thousands more circuits.

Also measured: the Euclidean centroid is **summation-order dependent** — `tac`
differs from `cat`/`act` below 1e-12 purely from float accumulation order. Absorbed
by Hilbert quantization today, but it means `coord` is not reproducible under
constituent reordering. A Karcher mean computed to a fixed tolerance would be.

For context on the frame the model lane is placing into, verified this session:
21,330,410 placements, **nothing outside the glome** (worst excess 8.88e-16, 4 ULPs),
Super-Fibonacci/Hopf minimum separation a stable 0.78× theoretical across
N = 10⁴..4×10⁶, and float64 headroom to N ≈ 2×10⁴⁷.

## Composition is not testimony — the lane inflates on both sides

Measured. `CONTAINS`/`PRECEDES` attestations by source:

```
TinyLlama    8,891
MiniLM       1,641
FrameNet        89
two others      19
```

**10,532 of ~10,640 (99%) come from the model lane.** WordNet, OMW, CILI, Unicode,
Britannica, Webster's, Roget's and all of chess combined emit 108.

The contrast is `alice.txt`: **4,202 trajectory points, 4,202 constituents, 2
attestations** on the document entity. A 151 KB document carries its entire
composition on the curve — indexed by `physicalities_constituents_gin` over
`laplace_trajectory_constituent_ids(trajectory)`, which is what makes
`structural.containers_of` work — and asserts almost nothing. The two are
author/title-class facts, which are genuine claims.

If documents followed the model lane's pattern, `alice.txt` alone would emit 4,201
`PRECEDES` rows and the 208 MB corpus hundreds of millions.

So the lane is inverted on both halves at once:

| | current | correct |
|---|---|---|
| weights (claims) | payload — 87.4 GB, asserts nothing | attestations |
| structure (composition) | attestations — 10,532/model | trajectory constituents |

**The epistemic cost outweighs the bytes.** You cannot refute that layer 3 precedes
layer 4. It is a structural given, not testimony. Encoding it as an attestation
gives Glicko a rating, an `rd` and a witness count for a proposition that can never
lose — unfalsifiable rows inside the mechanism whose entire value is falsifiability.
`UserPrompt` sitting at 0 evidence is the same law applied correctly; document
ingest asserting nothing is that law, not an omission.

Corollary for OP9 WITNESS: a model ingest should emit attestations **only** for
graded couplings and genuinely claimed facts (architecture family, author,
provenance). Tensor order, layer order, and containment are composition and belong
on the curve.

## Where the only real loss is

| layer | exact? |
|---|---|
| identity `Blake3-Merkle(tier, childIds)` | exact |
| trajectory | exact |
| attestations, per source | exact |
| `coord` / `hilbert_index` | deliberately approximate — an index, not a representation |
| consensus cell | aggregate; the per-source attestations underneath are intact |
| **factor trajectories** | **lossy** — `(float)factors[t*dim+j]` truncates float64→float32 |

The single genuinely lossy operation in the system is in the lane this document
argues should not exist. Everything the substrate keeps is exact or derived from
something exact.

## The pipeline

### Phase 1 — AST scrape (works today, keep)

A tensor name is already a parse tree: `model . layers . 0 . self_attn . q_proj .
weight`. The checkpoint hands you its own AST in the safetensors header. Emit
nodes and `CONTAINS`/`PRECEDES` productions, plus tensor byte-ranges.

Measured: 201 roles, `coverage=Full`, 252 s, ~megabytes for TinyLlama.

Known ceiling: `ArchitectureProfile.For(modelType)` is a three-arm switch —
`"llama"`, `"bert"`, `_ => Llama`. Anything else is silently profiled as llama.
The substrate can express any architecture (39 entity types, 210 declared
relations plus dynamically minted families, arbitrary composition); the intake
recognizes two.

### Phase 2 — Tokenizer decomposition (broken, must be built)

```
token → decode → codepoints → grapheme (UAX #29) → word → usages
```

Decode strips wire format: `▁` (U+2581) and `Ġ` (U+0120) are U+0020, `##` is a
continuation relation, `<0xNN>` is a byte. `▁cat`, `Ġcat`, `cat` and `' cat'` are
the same content and must reach the same node.

Emit **usages, not nodes**. Expected new tier-0: exactly zero. Expected new
tier-2 for a covered language: near zero.

**Merges are free corpus evidence.** TinyLlama alone emits 61,249. A merge is a
corpus-derived claim that a character sequence coheres, and across 25 tokenizers
it is multi-witness: a merge seen by twelve independent tokenizers is
high-confidence morphology; a merge unique to one is noise that never
accumulates. Kilobytes per model, no weights read.

Measured saturation across the whole local catalog (25 tokenizers, ~2.7M raw
tokens): casefolded decoded surfaces union to **151,016** — an 18× collapse — and
twelve consecutive Qwen-family models added **exactly zero**.

### Phase 3 — Canonicalization (missing; required by the identity law)

`same content = same hash` means two functionally identical models with permuted
neurons must produce the same nodes. Gauge freedom is not an inefficiency to
optimize away later; if the ETL cannot produce a canonical form it cannot
content-address, so it cannot ingest correctly.

- **Permutation** (FFN neuron order, head order): canonical order by a
  content-derived key computed at stable precision. Tractable.
- **Rotation** (within a head's subspace, and any orthogonal reparameterization):
  needs a canonical basis with fixed sign and ordering conventions. Harder, and
  the open design question.

### Phase 4 — Contraction (streaming, retains nothing)

**Query the weights; never prompt.** The couplings are in the weights, not the
outputs:

| component | relation | contraction |
|---|---|---|
| QK | which tokens couple | `(E·Wq)(E·Wk)ᵀ` |
| FFN | `[token] => [token]` memory | `E·Wupᵀ · Wdownᵀ·Eᵀ` |
| lm_head | unembed | `[state] => [token]` directly |

All three are bilinear forms; `bilinear_edges_tile`
(`engine/dynamics/src/bilinear_edges.cpp`) is the compiled contraction and is
still tested. Verified feasible in this session: TinyLlama layer 0 head 0,
4,000 tokens, 16M pairs, seconds, in numpy, on a **CPU-only box**.

Prompting loses on every axis: it samples a vanishing fraction of the pair space,
measures the decoding stack (temperature, template, RoPE) convolved with the
couplings, gives no circuit provenance, and isn't reproducible as evidence.
Contraction measures structure; prompting measures behaviour. Structure is what
gets witnessed.

Shape: `mmap tensor → contract tile → emit observations → free`. Peak memory is
one tile. Terabytes of checkpoints become a read-once streaming scrape. Carry the
`(plane, layer, head/expert)` coordinate as `context_id` — that is free from
contraction and unobtainable from prompting.

### Phase 5 — Admission: what is worth recording

The hard part, and where a threshold alone fails. Measured on TinyLlama L0 q/k
head 0 (arena `RMS|m| = 0.0045`): 23.1% of pairs survive at 1×RMS, 0.48% at
4×RMS — 236M and 4.9M edges *per circuit*. At 118.9 B/row that is 28 GB and
0.6 GB per circuit, against ~14,000 circuits for a 160-expert MoE. **Enumerating
`V²` and thresholding reintroduces the materialization the design rejected.**

Two passes instead:

**5a — Vote on what exists.** Iterate the edges the graph already holds among
entities the model can name; compute the model's tension on each; emit one
observation. Bounded by `|existing edges|`, not `V²`. Measured at the token tier:
26,622 entities, 55,971 edges between them, 336,795 touching — needs recomputing
after P2 lands, since the token tier is an encoding rather than a tier. Always
meaningful: it either confirms or contradicts, and both move Glicko.

**5b — Admit novelty only on corroboration.** A coupling with no matching edge
becomes a *candidate*, not an edge. Admit only when corroborated — by ≥k circuits
within the model, or by a second model, or by a curated source.

This is consensus-as-discovery rather than a configured floor. Gradient jitter is
uncorrelated between models and between circuits, so it never accumulates; real
structure recurs. **The noise floor is discovered, not chosen** — which is what
makes it not top-k and not a threshold.

It also implements *truths cluster, lies scatter* at intake: a coupling asserted
by one head of one model, connecting to nothing, does not get in.

### Phase 6 — Fold

`NativeAttestation.CategoricalResolved(subject, typeId, obj, sourceId, contextId,
witnessWeight, …)` is the sink and exists in main — the model lane currently
passes the literal `1.0`. `witnessWeight` takes `½(1 + tanh(m/M))`.

Missing: `KindRegistry.AttestWeighted` (`kind_rank × source_trust → φ`) and
`AttestationFactory.CreateWeighted` (`magnitude` + `floor` → score). Both deleted
in `7022bbca`, both recoverable from `7022bbca^` (#1015).

Existing edges take an `observation_count` increment and a rating move. **Zero new
consensus rows.**

### Phase 7 — Discard

No floats retained. No trajectories. Payload contribution: **0 bytes.** Weights
are transient input, like source to a compiler — you emit the binary, you don't
ship the AST.

---

## Cost model

Measured constant: `laplace.attestations` = 31,066,506 rows in 3,522 MB with
indexes = **118.9 bytes/row all-in**.

| | current lane | this design |
|---|---|---|
| TinyLlama | 81.8 GB payload, 775 attestations | ~10–40 MB, ~0 new consensus rows |
| local library (15) | 23.1 TB projected | tens of MB |
| frontier (Qwen3-480B, Llama-4, Mistral-Large) | 110 TB projected | tens of MB |
| growth per additional model | **linear, forever** | **saturating** |

Saturation is measured, not assumed: 2.7M tokens → 151,016 surfaces, codepoints
flat from model 4, twelve consecutive models adding zero. Growth saturates *per
domain* and new domains extend it — the first Arabic model adds a lot, the sixth
English one adds nothing. That is the property conventional AI cannot have, where
every checkpoint is a full copy regardless of agreement.

For scale: the entire witnessed graph — WordNet, FrameNet, PropBank, VerbNet,
CILI, Unicode, ISO-639, chess — is 31M attestations in **3.5 GB**. TinyLlama's
factors pass alone deposited **23× that**, and contributed 775 attestations.

---

## Order of work

1. ~~**Decomposition** (#1014).~~ **RE-SCOPED — this is Phase 5, not a
   separate first stage.** Decomposition is correct. Verified at HEAD:
   `LlamaTokenizerParser.Canonicalize:257-261` strips `▁`/`Ġ`, `:262-268` maps `##`
   to a continuation role, `:233-239` decodes `<0xNN>`; `▁cat` and `cat` canonicalize
   to identical bytes. The 6,141 tier-2 fragments arrive through the *successful*
   path: `StageVocabToken:302-306` → `TextDecomposer.Run` → `TextEntityBuilder.cs:74-75`,
   which writes every tree node as an entity at the tier the native decomposer
   assigned, and `undert` segments under UAX #29 exactly as `understand` does. **A BPE
   fragment is lexically indistinguishable from a word**, so no decomposer change
   fixes it.

   What is actually owed is Phase 2's *"emit usages, not nodes"*, which is an
   **admission** decision — Phase 5 below — and it has nowhere to live today:
   decomposers may not probe (Rule #8, pure content→`SubstrateChange`); the spine's
   probe is contractually present→skip / novel→write and is hardened against
   declining (`NpgsqlWorkingSetApply.cs:102-104` — *"a false positive would treat a
   genuinely novel row as present and DROP it"*), inside the subsystem
   `.scratchpad/38` §12 marks *"do not touch"*; and attestation-only is unavailable
   because attestation presence is always verified (`:37-38`), so `TOKEN_MAPS_TO`
   cannot reference an entity the change did not also supply.

   The fork — spine-side admission policy, exposing `ContentLadderLedger` as a
   decomposer-side present-set, or a two-pass stage-then-`evict_source` — is recorded
   on #1014 and is a decision about which law bends, not a coding task. Acceptance is
   already measured: TinyLlama 26,622 resolved / **8,603** minted; the 8,603 is what
   admission must decline.
2. **Ingest the five curated sources** that have decomposers and no rows. Makes
   model votes adjudicable and shrinks what models can add.
3. **Restore magnitude→Glicko** (#1015). Two functions, recoverable.
4. **Canonicalization.** New work; permutation first, rotation is the open problem.
5. **Streaming contraction emitter**, replacing `EmitFactorTrajectories`.
6. ~~**Wire the traversal engine.**~~ **RETRACTED — the premise was
   false.** This item read *"`astar.cpp` has zero callers and
   `laplace_walk_edge_weight` is unused as a path cost, so the coherence half —
   truth is cheap to traverse, falsehood dead-ends — cannot currently be computed
   at all."* Both clauses are wrong, and the engine is wired end to end:

   - `NpgsqlSubstrateReads.cs:2205,2213,2225` calls `converse.astar_path(...)`
     (`label: "astar_path"`);
   - `converse.astar_path` (`cascade/astar_path.sql.in:2`) →
     `astar_path_raw.sql.in:10` `AS 'MODULE_PATHNAME', 'pg_laplace_astar_path'`;
   - `extension/laplace_substrate/src/astar_path.c:280,289,298` calls
     `astar_open` / `astar_next` / `astar_close`;
   - `astar_path.c` derives additive path cost as negative-log Glicko expected
     score; exact accumulated cost leads and geometry only orders equal-cost ties.

   Source: `MODEL_LANE_AUDIT_2026-08-11.md` §6, which additionally asserted that
   `generate_walk.c` and `astar_path.c` "neither file exists." Both were added two
   months before that audit — git holds the commits — and running §6's own
   published reproduction grep returns them in its first eight lines. The command
   was printed as a receipt, not executed.

   Nothing is owed here. The remaining question is performance and edge-case
   behaviour on the existing C path, not construction.
7. **Delete the 87.4 GB** and re-baseline `scripts/model-payload-gate-check.py`
   downward.

## Open questions

- Canonical form for a head's rotational freedom (permutation is easy).
- The corroboration count `k` in 5b, and whether it differs per band.
- Whether "would never fire" is decidable analytically. Reachability and
  always-closed gates are; arbitrary couplings likely are not.
- An admissible A\* heuristic over Glicko edge costs — without one it is
  best-first search, not A\*. `astar_open` already takes an
  `astar_heuristic_fn`; nobody has supplied one.
- Whether the couplings are recoverable in the neuron basis at all, or only in a
  learned sparse dictionary. Measured: per token, 2,799 of 5,632 neurons carry
  90% of the mass, and **0 of 5,632 are dead** — so there is no neuron-basis knee
  to cut at. Superposition predicts the sparsity lives in a rotated basis, which
  would make 5b's corroboration test the practical substitute for finding it.
