# Entity contract: facets and strands

Status: draft v1. Owners: UI (`Laplace/web`), backend reads (laplace-e9). TypeScript types:
`web/src/data/contract.ts`. Changes to field names or meanings need both owners to agree.

## Model

- **Entity**: the handle. It has a content address (BLAKE3-128 id), a 4-D coordinate, a Hilbert index, banks/kind,
  and its Merkle structure. The same content is the same entity everywhere.
- **Physicality**: how building blocks are used. It is an ordered path of child ids with run lengths and positions.
- **Attestation**: one witness's claim. Witness = source trunk id. It carries trust class, lineage, games, score and position.
- **Consensus**: every attestation of a claim folded into one Glicko-2 standing (rating, deviation, volatility, matches).

Retrieval is a web, not nearest neighbour. Every web (source) that touches the same content attaches to the same
entity. A query is a **tug**: pick an entity and return the strands that pull back, how hard they pull, and which
spiders (witnesses) spun them.

## Strand: the one shape every facet returns

```
Strand {
  from:        EntityRef          // the entity being tugged
  to:          EntityRef          // what pulls back
  kind:        StrandKind
  strength:    number | null      // see "Strength" below; null = not measured
  uncertainty: number | null      // consensus deviation for attested; null for structural counts
  state:       'confirmed' | 'contested' | 'refuted' | 'thin' | 'prior' | 'structural'
  spiders:     Spider[]           // who spun it; never empty for attested strands
  via?:        EntityRef          // the container for precedes/follows/co-occurs
  evidence?:   { total: number, sample: EntityRef[] }   // e.g. the sentences behind a co-occurrence
}
StrandKind = 'contains' | 'contained-in' | 'precedes' | 'follows' | 'co-occurs'
           | `attested:${relation}` | 'shape-similar'
Spider { witness: EntityRef, trust: string, count: number, lineage?: string, score?: number, position?: number }
EntityRef { id: string /* 32 hex */, label: string, tier: number, kind?: string }
```

### Strength

| kind | strength | uncertainty | state |
|---|---|---|---|
| `attested:*` | consensus rating (display scale) | consensus deviation | `confirmed` / `contested` / `refuted` / `thin` (folded, not enough independent evidence) from standing; `prior` only when matches = 0 |
| `contains`, `contained-in` | occurrence count (run length × containers) | null | `structural` |
| `precedes`, `follows`, `co-occurs` | co-occurrence count across containers | null | `structural` |
| `shape-similar` | 1 / Fréchet distance on **realized curves** | null | `structural` |

Rules:
- Shape is computed on realized curves (child coordinates via `laplace_couple` / `laplace_project_path`), never on
  `physicality.path` carrier doubles.
- Shape strands are always labelled as shape and never mixed into meaning.
- Banks (upos, lexfile, deprel, …) are filters, never strand targets.
- Packaging metadata (WordNet pointer symbols, sense keys, offsets, row ids) never appears as a strand.

## Envelope: every facet response

```
Facet<T> {
  entity:  EntityRef
  facet:   FacetName
  items:   T[]
  total:   number | null        // null only when the read cannot count cheaply; then `truncated` is true
  cursor:  string | null        // opaque; pass back as ?cursor= for the next page
  truncated: boolean
  sources: Spider[]             // distinct spiders contributing to this page (for the filter UI)
  receipt?: Record<string, unknown>   // diagnostics only; the UI shows it only in Diagnostics mode
}
```

## Facets

`GET /v1/entities/{id}` returns `Identity`.
`GET /v1/entities/{id}/{facet}?cursor=&limit=&kind=&witness=&min_strength=` returns `Facet<Strand>`.

| Facet | Direction | Strand kinds | Source of truth |
|---|---|---|---|
| `made-of` | down | `contains` (ordered; `position`, `run` on the strand) | the entity's own physicality path |
| `appears-in` | up | `contained-in` (`evidence` = the lowest common ancestor surface) | GIN containment over physicality |
| `next-to` | along | `precedes`, `follows` (`via` = container) | vertex positions inside container paths |
| `occurs-with` | with | `co-occurs` (`evidence.total` = shared containers) | shared containers |
| `means` | meaning | `attested:<relation>` | attestation + consensus |
| `shape` | shape | `shape-similar` | realized curves |

`Identity { ref: EntityRef, coord: [x,y,z,m] | null, hilbert: string, banks: Record<string,string>, size: number, exists: boolean }`

Filters (all facets): `kind` (repeatable), `witness` (repeatable spider ids), `min_strength`, and `state`
(repeatable). Ordering: strength descending, then id.

## Answer states (chat / forward pass)

```
Turn =
  | { state: 'answered',  text: string, citations: Strand[], trace_id: string }
  | { state: 'abstained', reason: string, near_misses: { ref: EntityRef, strength: number|null, uncertainty: number|null }[], trace_id: string }
  | { state: 'failed',    stage: 'resolve'|'couple'|'orient'|'route'|'scan'|'compose'|'propose'|'steer'|'select'|'realize'|'witness', message: string, trace_id: string }
```

The UI shows `answered` text with citations as entity chips. It shows `abstained` with the reason and the near
misses, and `failed` with the stage. The full lifecycle trace is available behind `trace_id`, in a trace view.

## Migration

The UI builds lenses against this contract with fixtures. Backend facets replace the bespoke explore endpoints
(`mesh`, `roster`, `taxonomy`, `record`, `matchup`, `lexicon`, `response-field`, `verdict`, `preview`,
`neighbors`, `members`, `peers`, `containers`, `graph`) as they land. Each old endpoint is removed once nothing
calls it.
