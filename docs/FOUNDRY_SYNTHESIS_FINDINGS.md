# Foundry Model Synthesis — Findings (why generation is incoherent, and the fix surface)

Source-verified investigation of the `synthesize substrate` GGUF foundry: what the generated model
actually contains, what the substrate feeds it, why it generates like a 1-hop walk, and the concrete
change surface. Grounded in: full reads of `FoundryCommands.cs` / `FoundryExport.cs` /
`engine/synthesis/src/arch_template.cpp`, the substrate SQL (`26_generation.sql.in`), the rank table
(`engine/core/src/generated/relation_law.c`), the llama.cpp loader contract
(`D:\LlamaCPP\llama_cpp\src\llama-model.cpp`), and an **empirical probe of a live `kfix.gguf`**
(`scripts/foundry-probe.py`). All claims carry file:line.

> Framing: this is a **constructed, GPU-free, fully-provenanced** model — no training, no gradient
> jitter. "Coherence" is therefore a **recipe/construction** problem (does the pour compose the
> substrate's structure into a forward pass that emits dense, grounded tokens), NOT a "mimic SGD"
> problem. The transformer-mechanics checklist below is just the math any autoregressive GGUF must
> satisfy to be more than a bigram — it tells us *what the pour must produce*, not that we should train.

---

## 0. TL;DR — the four root causes

1. **The rank recalibration starved the foundry (THE killer).** Synthesis builds the embedding +
   attention/FFN operators from `consensus_layer_plane` rank **bands [0.30–0.86]**. After we
   recalibrated ranks, **IS_A=0.90 and HAS_DEFINITION=0.97 sit ABOVE the 0.86 ceiling → excluded**,
   and **PRECEDES=0.18 sits BELOW the 0.30 floor → excluded**. Taxonomy's strongest edges and the
   sequential edge are both dropped. Empirically the embedding geometry is only **+2.34σ ("NO
   consensus geometry")**. The bands were never reconciled with the new ranks.
2. **The lm_head is a single-stride sequential bigram.** `lm_head[y] = Σ_x w(x→y)·E[x]` from `traj`
   (= `word_order` PPMI co-occurrence at **one** stride). Generation follows corpus continuation, not
   meaning (" king" → `Elizabeth, passed, don't, gone…`). No multi-hop, no context conditioning.
3. **The default path's depth/heads barely compose.** The main "foundry" path uses **4 coarse rank
   bands** rotated across layers (`layerIdx % 4`) with **undifferentiated heads** (fills ignore head
   boundaries). Layers tile a 4-cycle; all H heads read the same operator factor.
4. **The good path exists but isn't the default, and its richest input is dead.** The "build-a-bear"
   path *does* give per-relation heads + frame-correct per-layer differentiation, but it only runs on
   a `laplace.recipe` JSON. Multi-stride context (`trajectory_pairs_plane`, 1/gap decay) and the
   per-gap ladder (`entity_trajectory_plane`) are unused / **undefined in SQL**.

---

## 1. Two synthesis paths

`synthesize substrate` routes on whether the recipe text contains `"laplace.recipe"`
(`FoundryCommands.cs:439-444`):

- **Main "foundry" path** — `SynthesizeFromSubstrateAsync` (`FoundryCommands.cs:429`) → `WriteCast`.
  HF/Llama tensors filled from **4 rank bands**, layers rotate the bands, **heads undifferentiated**.
  *This is what a plain `synthesize substrate … --layers 2 --heads 16` uses.*
- **Build-a-bear path** — `SynthesizeBuildABearAsync` (`FoundryCommands.cs:955`). One operator **per
  head**, descriptor-driven, operators re-projected against the **advancing residual frame R** at each
  depth (genuinely layer-differentiated, `FoundryCommands.cs:1135-1306`). The designed-for-coherence
  path.

**RETIRED:** `LAPLACE_FOUNDRY_FAITHFUL` / `_KNOWLEDGE` (the `embed=I`, `lm_head=log-odds(A)` 1-hop
lookup) are rejected (`FoundryCommands.cs:1348-1365`); setting them hard-fails. The "king→monarch then
loops" behavior was *that* path — it no longer exists. The current incoherence has different causes.

---

## 2. What the GGUF actually contains (current main path)

### Vocab (`FoundryCommands.cs:159-355`)
`<unk>,<s>,</s>` (3 specials) + 256 byte tokens (`<0xNN>`, score −20) + N word entities from a
relation-closed crawl `foundry_vocab_crawl(seeds,N,hops,fanout)` (`:228`), each as `▁word` plus a
bare-word alias for sentence-initial match (`:287`). Token score = `log(weight+1)+1` (frequency).
**It grows its own BPE** from the corpus (916 words → 1,559 merges in ~200ms) — confirmed in the run
log; the BPE merge-pair capability is real.

### token_embd (`E`) — `FoundryCommands.cs:619-653`
NOT identity in any live path. Three modes:
- **AFFINITY-SVD** (vocab ≤ 3000, the default for our test): `BuildBasisAffinity` (`FoundryExport.cs:875`)
  symmetrizes the **union of the 4 rank bands**, SVDs it, `E[i,c]=U[i,c]·√S_c`, L2-normalized, last
  dim pinned to bias 1.0.
- **Laplacian-eigenmaps** (large vocab): `BuildBasis` (`FoundryExport.cs:1143`).
- **`LAPLACE_FOUNDRY_COORD_ONLY=1`**: first 4 dims = verbatim S³ coord ×20, rest zero.
The embedding is **pure semantic geometry from the rank bands** — and the bands exclude the strongest
relations (§3), so the geometry is weak.

### output / lm_head (`FoundryCommands.cs:709-779`)
`lm_head[y] = Σ_x w(x→y)·E[x]`, IDF-divided by in-degree, byte/alias rows zeroed, RMS-normed.
Planes: **generative (default) = `{traj}` only**; non-generative = `{traj, adjacency}`
(`:721-723`). `traj` = `word_order` co-occurrence, **PPMI**-weighted (`FoundryExport.cs:781`). So the
readout is a **next-token continuation projector** (a bigram transition expressed in embedding space),
not a semantic-relation projector. This is the structural reason generation is sequential/bigram.

### Per-layer tensors (`FoundryCommands.cs:853-898`)
norms all `1.0`. Operator selection rotates 4 bands: layer 0 = assoc/taxo, last = completion, middle
`layerIdx % 4` (`:867-872`). q/k = left/right SVD factors of a band's `dModel×dModel` Gram operator;
v/o = the `(M−I)` residual factor; ffn up/down = FFN-rank factor; **ffn_gate = a single constant
column** (no signal, `FoundryExport.cs:1518`). Rows beyond the factor rank are **zero**. So each layer
is a shallow low-rank nudge from one rank band; **beyond 4 layers they repeat tensor-for-tensor.**

### Per-head — **undifferentiated** (main path)
`FillRows`/`FillRowsRight`/`FillCols` (`FoundryExport.cs:1407-1429`) write factor rows across the whole
`dModel` width, **ignoring head boundaries** — all H heads compute the same relation. (Build-a-bear is
the opposite: `FoundryCommands.cs:1271-1302` fills each head's band from its own operator.)

### RoPE / norms
No positional tensors written; RoPE is metadata-only (`rope.freq_base=10000`, `FoundryCommands.cs:1494`),
applied at runtime by llama.cpp (defaults: `n_rot=head_dim`, freq_base 10000 — RoPE is ON even with no
KV). norms = 1.0 everywhere.

---

## 3. What the substrate feeds it — and the band-misalignment bug

The main path calls (call-site authority: `FoundryCommands.cs`):

| reader | SQL | becomes | weight |
|---|---|---|---|
| `consensus_layer_plane` ×4 bands | `26_generation.sql.in:286` | embed union + per-band QK/OV/FFN operators | `eff_mu × relation_rank` |
| `word_order` (via `ReadTrajectoryStrideAsync`) | `26_generation.sql.in:583` | **lm_head** (`traj`), single stride, PPMI | raw co-occ count |
| `consensus_adjacency` | `26_generation.sql.in:378` | lm_head only in non-generative mode (dormant) | `Σ rank·eff_mu` |
| `metric_edges` | `26_generation.sql.in:652` | optional geometric attn head (off by default) | `exp(−dist)` |

**The four bands** (`FoundryCommands.cs:521-527`): `sim`[0.50–0.60], `att`[0.30–0.50],
`pre`[0.60–0.68], `rel`[0.68–0.86]. Cross-referenced to the **recalibrated** rank table
(`engine/core/src/generated/relation_law.c`):

```
HAS_DEFINITION 0.97  → ABOVE 0.86 ceiling → EXCLUDED
IS_A           0.90  → ABOVE 0.86 ceiling → EXCLUDED   ← taxonomy's strongest relation, dropped
IS_SYNONYM_OF  0.82  → rel band ✓
HAS_PART/MEMBER 0.73 → rel band ✓
ENTAILS/IS_BEFORE 0.64 → pre band ✓
(nothing)      0.50–0.60 → sim band is EMPTY in practice
RELATED_TO     0.36  → att band ✓
PRECEDES       0.18  → BELOW 0.30 floor → EXCLUDED from bands (only enters via word_order)
```

So the embedding + operators are built from **mid-rank relations only**. The two most semantically
load-bearing relations (IS_A, HAS_DEFINITION) and the sequential one (PRECEDES) are all outside the
bands. **This is a direct, unintended consequence of the rank recalibration** — the bands are stale.

**Also unused / dead:** per-relation-type planes `consensus_type_plane` (used only in build-a-bear,
`FoundryCommands.cs:1053`); multi-stride — `word_order` uses `lead(wid, p_gap)` = **one** offset
(`26_generation.sql.in:601`), the `trajectory_pairs_plane` 1/gap ladder is never called, and
`entity_trajectory_plane` (the true per-gap reader) **has no SQL definition** (only a DROP at
`26_generation.sql.in:45`; dead reader at `FoundryExport.cs:601`).

---

## 4. Empirical confirmation (`scripts/foundry-probe.py` on `kfix.gguf`)

```
basis probe:  king~queen +0.24  man~woman +0.32  dog~cat +0.31  two~three +0.49
              related mean +0.309  vs random +0.025 (std 0.12)
              separation = +2.34σ  → "NO consensus geometry"            ← weak embedding (band bug)
readout " king" top-15: Elizabeth, passed, don't, gone, it's, hardly,   ← sequential corpus continuation,
              Queequeg, neither, feel, come, indeed, sometimes, became      NOT monarch/ruler (lm_head=word_order)
              (▁king itself ranks 317)
depth probe:  k=0 std 2.19 → k=1 3.15 → k=2 2.99                        ← layers perturb, don't converge
```

Every prediction of the code-analysis is borne out: weak semantic embedding (bands exclude IS_A/
HAS_DEFINITION), sequential bigram lm_head (word_order), shallow non-composing layers.

### 4b. Tensor-value confirmation (`gguf` reader on `kfix.gguf`, 21 tensors, F32)
```
token_embd [2048,2091] std=0.0312        output(lm_head) [2048,2091] std=0.0221
blk.0 vs blk.1 attn_q/k/v/o: NOT identical (L2 0.46–0.62)   → layers ARE distinct
   ...but blk.0 attn std = 0.0004  (≈75× smaller than embed) → layers barely perturb the residual
blk.0 attn_q: eff_rank(99%) = 134/2048 (6.5%), s1/s0=0.096   → heavily low-rank / content-flat
blk.0 ffn_gate: 1/2048 nonzero columns                       → constant gate, SiLU stuck linear (FFN off)
```
**Mechanism, exact:** `embed(0.031) → [layers std 0.0004, rank 134, FFN gated off] → bigram lm_head`.
The residual reaches the readout ~unchanged, so generation is a 1-hop walk **despite two real, distinct
layers** — they contribute ~0.04% of the signal. The fix is therefore not "add layers"; it's **give the
layers signal**: more gain (`layerScale=gain·nLayers^-0.25` is too small), higher operator rank, a real
FFN gate, plus the band realignment that feeds them stronger relations (§3).

---

## 5. The transformer-mechanics checklist (what the pour must satisfy)

From the llama.cpp loader contract (`llama-model.cpp:2766-2806`, `:508-660`) and the reference forward
(`scripts/model-forward-oracle.py:195-230`). To be more than a bigram, prediction must be a function of
the **trajectory through the residual stream**, not the last token. That requires, non-degenerately:

1. **`attn_q`/`attn_k` content-dependent** (not constant-tiled/identity) → `q_t·k_s` varies with `s`.
   *Current:* low-rank band-Gram factors, heads undifferentiated → near-content-flat.
2. **RoPE effective** (present by default, but needs non-constant q/k to matter).
3. **Heads differentiated** → different subspaces track different relations. *Current main path: no.*
4. **Layers differentiated** → layer ℓ+1 reads what ℓ wrote (induction). *Current: 4-cycle, repeats.*
5. **FFN a real nonlinearity over the mixed residual** — not the constant `ffn_gate` / zero fallback
   (`arch_template.cpp:351` zeros any unhandled tensor).
6. **lm_head conditioned on the residual `x`**, not an `x`-independent continuation table.
   *Current: `lm_head·RMSNorm(E[x])` ≈ pure bigram readout.*

The default main path violates 1,3,4,5,6. Build-a-bear satisfies 3 and 4 by construction.

---

## 6. The fix surface (recipe/construction changes — no training)

Ordered by leverage; all are pours/recipe edits, preserving provenance + density:

1. **Realign the rank bands to the recalibrated table** (`FoundryCommands.cs:521-527`). Raise the top
   ceiling to ≥0.97 (or rebucket) so **IS_A (0.90) and HAS_DEFINITION (0.97)** enter the embedding +
   operators; give **PRECEDES (0.18)** an explicit sequential band/plane. This single change should
   move the embedding past the "+2.34σ / NO consensus geometry" line. *(It's our own rank fix's
   downstream debt.)*
2. **Default to the build-a-bear recipe** (or port its per-head/per-relation + frame-correct
   per-layer differentiation into the main path): heads = distinct relation operators
   (`consensus_type_plane`), layers re-factored against the advancing residual frame `R`.
3. **Condition the lm_head on context.** Read continuation against the **final residual frame** (as
   build-a-bear does, `FoundryCommands.cs:1187`) instead of `E`, so logits depend on the prefix, not
   just the last token.
4. **Wire multi-stride context.** Define `entity_trajectory_plane` (or use `trajectory_pairs_plane`'s
   1/gap ladder) and assign **strides to heads** so the residual carries an n-gram window — the
   structural cure for the bigram 2-cycle.
5. **De-degenerate the interior materializers** (`arch_template.cpp` `materialize_interior_uniform`):
   non-constant q/k, per-layer/per-head variation, a real FFN; never hit the zero fallback.

Probe after each with `foundry-probe.py` (basis σ should rise; depth-probe rank should *move* at k=2;
readout for " king" should surface `monarch/ruler` not function words).

---

## 7. The paradigm note

None of the above is "train it." Every fix is a **construction choice** — which witnessed planes pour
into which tensors, how heads/layers specialize, how the readout reads the residual. The model stays
deterministic, GPU-free, and fully traceable to attestations. The incoherence isn't a missing-training
problem; it's a **band boundary stale by one rank-recalibration, a sequential-only lm_head, and the
coherent path (build-a-bear) not being the default.** Fix those three and the foundry pours a model
whose every weight still traces to who witnessed it.

---

## 8. GPU-free (the invention's claim)

The claim is about **producing** intelligence without a GPU, not about forbidding GPUs on the output:

- **No-GPU REQUIREMENT (the invention):** the substrate + the model *synthesis* run GPU-free — no GPU
  training, no gradient descent. Every fix in §6 is CPU-side **construction** (band realignment,
  build-a-bear recipe, residual-conditioned lm_head, multi-stride wiring, layer-signal). None introduces
  a GPU requirement.
- **GPU ACCEPTED for the heavy math** — the foundry's SVD / Laplacian-eigenmaps / operator factoring
  (~8–45s on CPU; a CUDA-via-Postgres-SPI path is feasible and has been done before). Optional accelerator.
- **GPU ACCEPTED for running/testing the exported model** — the GGUF is a **conventional** model; the test
  is precisely that it works in standard tooling. Run it with GPU offload (`llama-completion -ngl 99`,
  `D:\llamacpp\ggml-cuda.dll`); **CPU stalls testing.** Using a GPU here does not weaken the claim. No gradient
descent, no training artifacts, full provenance — that is the claim: AI reinvented, the graphics-card
requirement thrown away, the black box turned into a crystal ball.
