# Conventional-model export/construction recipe schema

A recipe is a versioned consumer-artifact construction description (JSON). It names a target architecture structure, an explicitly authorized/export scope, and an operator array describing which substrate calculations populate target slots. Recipes are deposited into the substrate as
content-addressed `Model_Recipe` entities (the JSON is the entity's canonical name; hparams are
also emitted as queryable scalar attestations). Export reads the stored recipe via
`model_recipes()` / `--recipe-from` — never a disk file. A hand-written recipe is a **dev fixture**
that simulates an ingest or user-create event; it goes through deposit like any other.

This schema describes a target artifact projection. It is not Laplace's native cognition architecture, not a knowledge package, and not a product-tier definition.

## Authority and scope boundary

The live substrate remains the semantic authority. A model export recipe may select source/context/evidence scope only when the caller is authorized for that scope and the export capability allows materialization. Selecting a narrow export artifact does not make the live Laplace world forget anything.

Commercial "quick/standard/deep" levels are compute envelopes over the same authorized world; they are not recipe choices for progressively smaller intelligence.

## Top-level fields

| field | meaning |
|---|---|
| `kind` | must be `"laplace.recipe"` |
| `name` | human label (also the canonical handle) |
| `structure` | `"dense"` \| `"moe"` |
| `hidden_size` | int, or `"auto"` = produced spectral rank of the selected operators' graph |
| `num_layers` | int (must equal `layers.length`) |
| `rope` | bool (parameter-free; metadata only) |
| `tie_embeddings` | bool |
| `norm` | `"rmsnorm"` \| `"layernorm"` |
| `vocab` | target consumer vocabulary/materialization selection — see below; this is export state, not the live knowledge boundary |
| `embed` | operator for `embed_tokens` (default `{"op":"coord"}`) |
| `lm_head` | operator for `lm_head` (default `{"op":"trajectory"}`) |
| `layers` | array of layer specs — the operator array |

## `vocab` (content selection)

```json
{ "source": "tokenizer", "tokenizer": "<dir with tokenizer.json>" }      // real BPE/SP vocab
{ "source": "crawl", "seeds": [...], "hops": 2, "fanout": 30, "size": 1500 }  // topic crawl
{ "source": "grapheme", "size": 2000 }                                    // grapheme floor
```

## Operator catalog (the `op` values)

Each head/embed/lm_head/ffn is one operator. Operators map to the fixed ETL (see
`docs/invention/05-synthesis-layers-heads.md`):

| `op` | source | drives |
|---|---|---|
| `{"op":"relation","type":"IS_A"}` (or any attestation type) | `consensus_type_plane` for that `type_id` | a head's Q/K (rated affinity) + V/O (residual) |
| `{"op":"metric","metric":"angular"\|"frechet"\|"hausdorff"}` | `metric_edges` over trajectories/coords | a metric head |
| `{"op":"trajectory"}` | `generation.continuation_conditional_plane` (continuation) | lm_head log-odds, or a sequential head/ffn |
| `{"op":"coord"}` | native S³ coordinate | `embed_tokens` |
| `{"op":"spectral"}` | Laplacian eigenmap of the selected graph | `embed_tokens` (alt) |
| `{"op":"unary"}` | per-token consensus covariance (`unary_gram`) | ffn (per-token implications) |

## `layers[]` (the operator array — one entry per layer)

```json
{
  "kv_heads": 2,
  "heads": [ {"op":"relation","type":"IS_A"}, {"op":"metric","metric":"angular"}, ... ],
  "ffn": {"op":"unary"}        // or {"op":"trajectory"} for continuation layers
}
```

- `n_heads` for the layer = `heads.length`. `head_dim = hidden_size / n_heads`.
- **Each head fills its own rows** `[h·head_dim, (h+1)·head_dim)` from *its* operator — never top-k of one operator tiled across heads.
- Recommended schedule (neighborhood → structure → continuation): early layers = equivalence/associative (+ angular); middle = taxonomic/partitive (+ frechet); last = causal/sequential + `trajectory` ffn.

## Knobs vs derived vs fixed (for UI + descriptor refactor)

Three buckets — the boundary is *the user designs the vessel and chooses what fills it; the substrate
determines every weight value; the math turning knowledge into weights is fixed.*

**1. KNOBS (user chooses — no single correct value):**
- Topology: `hidden_size`\*, `num_layers`, `num_heads`/layer, `kv_heads`, `intermediate_size`\*
- Structure: `dense`/`moe`, `num_experts`, `experts_per_token` (routing), LoRA rank
- Operator array: which operator each head is (the build-a-bear multi-select); per-layer schedule\*
- Content/materialization: `vocab.source` (+ selected scope controls) — what the target artifact materializes from the already authorized substrate; this does not alter what Laplace knows
- Flags/output: `rope`(+theta), `tie_embeddings`, `norm`, `embed` op, `lm_head` op, format, dtype
  (\* = has a substrate-derived default; overridable.)

**2. DERIVED (computed from substrate + knobs; never set):**
- Every weight value (embed, q/k/v/o, gate/up/down, lm_head, norms) — they ARE the rated
  attestations, calculated. Nothing in the weights is chosen → this is why provenance is auditable.
- `head_dim` = hidden/heads; each operator's rank K; `hidden_size` when `"auto"` (= spectral rank);
  `intermediate_size` default; token S³ coords; consensus μ/RD; recipe id; weight→source provenance.

**3. FIXED (the laws — not configurable):**
- The ETL operator→tensor map; S³/glome geometry; DUCET→Super-Fibonacci seeding; content-addressing;
  the relation-rank hierarchy (`[ranks]` in `engine/manifest/relation_types.toml` — the authoritative
  ladder; numeric values here rotted once already); Glicko-2 mechanics; operator signatures.

UI states: a knob-with-derived-default shows the substrate's natural value and flags overrides as
"deviates from substrate-natural." Constraints link knobs (`head_dim` integer, `experts_per_token ≤
num_experts`, `kv_heads` divides `heads`) → validate/grey-out, not free fields.

The target model may be deliberately finite/narrow as a consumer artifact. That is different from routing a cheaper Laplace request to a deliberately knowledge-reduced model.

## Validation by ablation (per-operator signatures)

A single-operator model has a predictable output signature; that signature is the correctness gate:
- `IS_A` only → hypernym climb (`king→monarch→ruler→person`) then stall
- `IS_SYNONYM_OF` only → synonym clusters, no progression
- `trajectory` only → n-gram continuation (fluent, driftless)
- `metric:angular` only → category-mates regardless of relation
- `HAS_DEFINITION` only → tier-3 sentence fragments
