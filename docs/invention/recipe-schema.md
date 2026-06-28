# `laplace.recipe` schema (Mold-A-Model)

A **recipe** is the architecture-agnostic, content-addressed spec that drives model *export*
("Mold-A-Model"): the substrate pours its consensus knowledge into the operator slots this document
declares. It is deposited as a `Model_Recipe` entity (`recipeId = Blake3(canonical JSON)`) and fetched
at export via `laplace.model_recipes()` / `--recipe-from` — never read from disk at synthesis time.

Parsed read-side by `RecipeDescriptor.Parse` (`app/Laplace.Decomposers.Model/RecipeDescriptor.cs`);
deposited by `RecipeExtractor` / `RecipeDecomposer`; synthesized on ingest from a model manifest by
`RecipeSynthesizer` (closing the ingest↔export loop).

## Top-level fields

| field | type | default | meaning |
|---|---|---|---|
| `kind` | string | — | must be `"laplace.recipe"` |
| `name` | string | — | model name |
| `structure` | string | `"dense"` | `"dense"` or `"moe"` |
| `model_type` | string | — | architecture family (`llama`, `qwen2`, …) |
| `hidden_size` | int \| `"auto"` | `"auto"` | residual width; `"auto"` = spectral rank chosen at synthesis |
| `intermediate_size` | int | ≈2.67·hidden (SwiGLU, rounded to 64) | MLP width |
| `num_layers` | int | = `layers.length` | block count (cross-checked against `layers`) |
| `rope` | bool | true (false for bert) | rotary position |
| `tie_embeddings` | bool | false | share embed/unembed |
| `norm` | string | `"rmsnorm"` | `"rmsnorm"` or `"layernorm"` |
| `embed` | OperatorSpec | `{op:"coord"}` | how tokens enter the residual |
| `lm_head` | OperatorSpec | `{op:"trajectory"}` | how the residual reads out to tokens |
| `layers[]` | LayerSpec[] | — | per-block operators (≥1) |
| `vocab` | VocabSpec | — | token set to materialize |
| `attributes[]` | string[] | — | word→category relation types folded into embed (e.g. `HAS_POS`) |
| `compile` | string | auto: `"continuation"` if lm_head is `trajectory`, else `"full"` | `"continuation"` (prefix→next readout) vs `"full"` (all heads active) |

## Nested types

**OperatorSpec** `{ op, type?, metric? }` — `op` ∈ `relation` (with `type`, e.g. `IS_A`/`ATTENDS`),
`metric` (with `metric`), `attribute`, `syntax`, `trajectory`, `unary`, `coord`, `spectral`. The
`Key` (e.g. `relation:IS_A`, `metric:frechet`) selects the consensus plane the operator reads.

**LayerSpec** `{ kv_heads:int, heads: OperatorSpec[], ffn: OperatorSpec }` — one operator per head
(each head is its own relation/metric operator — the per-head synthesis path), plus the FFN operator
(`relation:COMPLETES_TO`, `experts` for MoE).

**VocabSpec** `{ source, seeds[], hops, fanout, size, tokenizer? }` — `source` ∈ `crawl` (graph walk
from `seeds`, default hops 2 / fanout 30 / size 1500), `grapheme`, or `tokenizer`.

## Example

```json
{
  "kind": "laplace.recipe",
  "name": "tiny-demo",
  "structure": "dense",
  "model_type": "llama",
  "hidden_size": 2048,
  "num_layers": 2,
  "rope": true,
  "tie_embeddings": false,
  "norm": "rmsnorm",
  "embed":   { "op": "coord" },
  "lm_head": { "op": "trajectory" },
  "layers": [
    { "kv_heads": 16, "heads": [ { "op": "relation", "type": "ATTENDS" } ], "ffn": { "op": "relation", "type": "COMPLETES_TO" } },
    { "kv_heads": 16, "heads": [ { "op": "relation", "type": "IS_A" } ],    "ffn": { "op": "relation", "type": "COMPLETES_TO" } }
  ],
  "vocab": { "source": "crawl", "seeds": ["king","dog","water"], "hops": 1, "fanout": 40, "size": 2000 },
  "compile": "continuation"
}
```
