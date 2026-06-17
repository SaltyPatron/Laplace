# Laplace recipe schema (build-a-bear spec)

A **recipe** is a modality (JSON). It is the *build-a-bear parts list*: it names the architecture
structure, the content slice, and — the core of it — the **operator array**: per layer, per head,
exactly which substrate operator that head *is*. Recipes are deposited into the substrate as
content-addressed `Model_Recipe` entities (the JSON is the entity's canonical name; hparams are
also emitted as queryable scalar attestations). Export reads the stored recipe via
`model_recipes()` / `--recipe-from` — never a disk file. A hand-written recipe is a **dev fixture**
that simulates an ingest or user-create event; it goes through deposit like any other.

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
| `vocab` | content/build-a-bear stuffing — see below |
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
| `{"op":"trajectory"}` | `trajectory_pairs_plane` (continuation) | lm_head log-odds, or a sequential head/ffn |
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

## Validation by ablation (per-operator signatures)

A single-operator model has a predictable output signature; that signature is the correctness gate:
- `IS_A` only → hypernym climb (`king→monarch→ruler→person`) then stall
- `IS_SYNONYM_OF` only → synonym clusters, no progression
- `trajectory` only → n-gram continuation (fluent, driftless)
- `metric:angular` only → category-mates regardless of relation
- `HAS_DEFINITION` only → tier-3 sentence fragments
