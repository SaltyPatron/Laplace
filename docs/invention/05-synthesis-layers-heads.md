# Synthesis: Substrate → Transformer (layers, heads, lm_head, hidden dims)

Derived from the faithful path, not pattern-matched. Grounding functions cited inline:
`arch_template.cpp::compute_substrate_gram`, `::materialize_token_axis`,
`::materialize_interior_{uniform,binary_uniform}`, `FoundryCommands.cs` faithful branch
(`BuildBasis`/`COORD_ONLY`, `ProjectOperator`, `Factor`, the `lmHead`←`traj` loop),
`qk_project_cached.cpp`. Verified against the GGUF run (`embed=I`, `lm_head=log-odds(A)`,
`king→monarch→ruler→person`).

The whole claim in one line: **the transformer forward pass is one linearized traversal of the
Glicko-rated relation graph. Every weight matrix is a factorization of a rated relation plane or
of the consensus-weighted token basis. Nothing is learned; everything is computed.**

---

## 0. Objects

- `V` = vocabulary = substrate token entities (word entities + 256 byte-floor + specials).
- `E` ∈ ℝ^{V×D} = token basis (the embedding); `D` = `hidden_size`.
- Relation **planes**: per relation type, a sparse matrix `M[i,j] = eff_mu(rating,rd) × relation_rank`
  (subject→object, weighted by Glicko consensus × the rank weight). [Annotated 2026-07-18: this doc's
  original rank examples predate the salience recalibration that inverted the ladder (content relations
  on top, standards/structural metadata at the floor — `standards_structural` is now 0.08, not 0.91).
  The authoritative ladder is `[ranks]` in `engine/manifest/relation_types.toml`; counts in
  `docs/INVENTORY.md`.]
- Planes group into **rank-classes** (the real heads — NOT radius bands):
  `associative`, `taxonomic+partitive`, `causal+sequential (PRECEDES∪trajectory)`, `equivalence`,
  plus **metric heads** `angular` / `frechet` / `hausdorff` over trajectories.

## 1. hidden_dim `D` — *derived, not chosen*

`D` is the **effective spectral rank of the consensus relation graph over `V`**.
`FoundryCommands` builds `E` three ways:
- `COORD_ONLY`: `E[t,0..3] = S³_coord(t) × scale`, rest 0 → **D=4, the native glome coordinate, verbatim**
  (`no LE/GSO/Procrustes/Lanczos/SVD`). This is the purest faithful embedding.
- `Laplacian-eigenmaps` (default): `E` = spectral embedding of the union relation graph → `D` = spectral K.
- `AFFINITY-SVD` (small vocab): token = SVD of its relational row.

So `hidden_size` isn't a hyperparameter you tune blind — it's **how many dimensions it takes to
linearly embed the relation graph**. Pad to a round number for the runtime; the meaningful part is
the spectral rank.

## 2. Token embedding `E` and `lm_head` — `materialize_token_axis`

`embed_tokens.weight[t,d] = per_token_consensus[t] · token_basis[t, d mod basis_dim]`.
The embedding = **where the token sits relationally (basis) × how well-attested it is (consensus mass)**.
"`embed=I`" in the run = the basis is orthonormal, so the token *is* its own coordinate.

## 3. Attention heads — Q/K = factors of a rank-operator; `Q·Kᵀ` *is* the rated relation affinity

For each rank-class plane `M`:
1. `m = ProjectOperator(E, M)` ≈ `E⁺ M E` — the dense `D×D` linear operator on embedding space that
   reproduces the relation's action.
2. `fAttn = Factor(m, k=head_dim, transpose:false)` → `m ≈ Wqᵀ Wk`.
   Then `Q = E·Wq`, `K = E·Wk`, so **`Q·Kᵀ ≈ E m Eᵀ` = the relation plane's affinity** — attention
   literally scores "how strongly are tokens i,j related under this relation," with the **Glicko
   rating as the attention weight**.

This is exactly `compute_substrate_gram`'s `binary_gram = Σ_edges w(r,c)·basis[r]basis[c]ᵀ` — the
relation bilinear form in the basis — which `materialize_interior_binary_uniform` writes into the
**q_proj/k_proj** tensors. `qk_project_cached` is the inverse (project→score→sparse pairs), used to
*verify* a synthesized head reproduces its plane and to ingest existing models.

- **n_heads** = number of rank-class + metric operators you expose; **head_dim = D / n_heads**.
- **Metric heads**: same slot, `m` built from `angular`/`frechet`/`hausdorff` over trajectories instead
  of a discrete plane; `ReportMetricHeadFidelity` checks the head reproduces the metric.

## 4. V / O and FFN — the *residual* operator

`mResid = m − I` (the relation's contribution *beyond* identity, for the residual stream).
- `fOv = Factor(mResid, k=kv_dim, transpose:true)` → **v_proj, o_proj**: attention output adds the
  related entity's embedding into the residual.
- `fFfn = Factor(mResid, k=interm, transpose:true)` → **gate/up/down**: the per-token implications.
  The per-token interior uses `unary_gram = Σ_t per_token[t]·basis[t]basis[t]ᵀ`
  (`materialize_interior_uniform`) — the consensus-weighted covariance of the basis.
- `interm` (FFN dim) = the FFN factor rank, ≥ D. Gate uses SiLU (`gateZ`, `upGain` calibrate it).

## 5. lm_head — the continuation operator = log-odds(A) — *the generative readout*

Faithful generative branch (`LAPLACE_FOUNDRY_GENERATIVE=1`), built from the **trajectory** plane:

```
for each continuation edge x→y in traj:  lmHead[y] += w(x→y) · E[x]
lmHead[y] *= 1/(in_degree[y]+1)          # IDF normalization
suppress byte + bare-alias tokens         # kills the "#" artifacts
global RMS normalize
```

So `logit(y | hidden h) = h·lmHead[y]ᵀ = Σ_x w(x→y)·(h·E[x])` = **how strongly the current state
continues to `y`.** The output GEMM **is** the continuation/attestation lookup; next-token = the
highest-rated continuation. This is the verified `lm_head = log-odds(A)`.

## 6. layers — hops of relational composition

Each layer applies attention (gather rated-related entities) + FFN (per-token implications) to the
residual stream. Stacking `L` layers = **`L` hops of relation-graph propagation** composed into the
forward pass. `split = L^(−0.25)` scales each layer so `L` hops sum stably.

## 7. The forward pass = one graph traversal, linearized

`embed` (where you are) → per layer: `Q·Kᵀ` = rated relation affinity (gather), `V/O` carries related
embeddings, `FFN` adds per-token + continuation → `lm_head` reads out the top continuation.
Every GEMM is a rated lookup. No gradient descent ever ran.

---

## 8. How it *should* work (the design call, not an unknown)

The faithful path already factors all four rank-classes; what's underspecified — and what made the
2-layer run degrade after ~3 tokens — is the **per-layer schedule** (which rank-operator drives which
layer) and **insufficient depth**. Derived recommendation:

- **Embedding:** native S³ coordinate (COORD_ONLY) or spectral eigenmap; `D` = spectral rank.
- **Layer schedule (compose meaning → structure → continuation):**
  - early layers → `equivalence` + `associative` (+ `angular` metric): semantic neighborhood
  - mid layers → `taxonomic+partitive` (+ `frechet`/`hausdorff` metric): structural refinement
  - late layers → `causal+sequential (PRECEDES∪trajectory)`: drive continuation
- **Depth `L` ≳ 6–8**, not 2 — the run flattened because continuation had ~2 hops to compose.
- **lm_head:** the trajectory/continuation operator above (richer `traj`, not just `adjacency`).
- **n_heads** = #(rank-classes + metric heads); **head_dim = D/n_heads**.

This is the difference between *describing the current code* (which factors ranks but schedules them
flat) and *the correct design* (rank-classes assigned across depth so the stack composes
neighborhood → structure → continuation, instead of collapsing after one strong hop).

## 9. Verified / derived / open

- **Verified (ran):** `embed`=consensus-scaled basis; `lm_head`=trajectory log-odds; `Q·Kᵀ`=relation
  affinity via `binary_gram`; grounded chains emit (`king→monarch→ruler→person`).
- **Derived (grounded, internally consistent, checkable):** `D`=spectral rank; heads=rank/metric
  operators factored to `head_dim`; V/O/FFN=residual-operator factors; layers=composition hops.
- **Open design choice (decide, don't fear):** per-layer rank schedule + depth; richer continuation
  plane. These are calls to make, and §8 is the proposed call.
