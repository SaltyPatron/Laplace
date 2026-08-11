# Language highway — master/detail surfaces

Status: **direction captured, not yet built.** Recorded 2026-08-10 during the UI
session on `feat/ui-user-surfaces`.

## The ask

Master/detail pages for each layer of the language highway — one page per piece,
each with a standardized, known, optimized way to query it:

- highway mask
- ISO 639 (the language axis)
- ILI (Collaborative Interlingual Index — the concept anchors)
- synsets
- frames (FrameNet)
- POS
- sense
- deprel
- (and the rest of the mesh: VerbNet class, PropBank roleset, roles)

## Why — the actual requirement

Not a browser for its own sake. The purpose is **to prove that each
instruction/step of the "firmware" really moves toward inference / generation /
prediction.** Each layer is a step in the pipeline; the surface has to make it
checkable that the step does work — that it contributes signal — rather than
merely existing in the database.

So each detail page has to answer, per layer:

1. **What is in it** — counts, coverage, residency, what seeded it.
2. **How it is queried** — the one standardized, optimized read for this layer,
   named and stable, not an ad-hoc query per page.
3. **What it contributes** — the evidence that this step advances
   inference/generation/prediction, not just structure.

Point 3 is the one that distinguishes this from the existing warehouse/mesh
browsing, which shows structure but does not demonstrate contribution.

## The shape it should take: a league site, not a lab

Clarified in the same session. The warehouse, walk, glome and constellation are
"cool tools and visuals" — but they are not the thing. The model is an
**MLB/NBA/NFL website**:

    league → division → team → position → player → roster → schedule

Plain resource hierarchy, master/detail at every level, a URL for every
resource — **what MVC was made for.** Navigation by drilling down a known
structure, not by flying a camera through a graph.

The mesh landing already speaks this language ("a concept is a hub whose roster
is its members; a word is a player whose teams are the hubs it plays for") — but
it hands you a 3-D web instead of a standings table. The highway layers are the
league structure:

| League site | Language highway |
| --- | --- |
| league | the highway itself |
| division / conference | layer (ISO, ILI, synset, frame, POS, sense, deprel) |
| team | a hub — synset / frame / class / roleset |
| position | relation type / role |
| player | a surface, sense, or lemma |
| roster | the hub's members |
| schedule / results | the witnessed edges and their ratings |
| standings | ranked by consensus μ and witness count |

Implication for the build: **tables, rosters, standings and record pages first.**
The existing visualizations stay — they are not being removed — but they become
a tab on a detail page rather than the primary way in.

## How this relates to what exists today

Already built and working (verified live this session):

- `/explore/mesh` — the mesh landing already names the layer chain
  `surface → lemma → sense → concept → frame / class / roleset → roles` and has
  per-layer cards with residency. This is the closest existing surface and the
  natural host for the master list.
- `/explore/entity/:idHex` — the detail view for a single entity, with
  overview / graph / glome / structure / links / provenance / export tabs.
- `/topic/:ref` — the best "everything at once" read: definition, IS_A ladder,
  translations, strongest facts by band, mesh position.
- `/v1/query` with `shape` — the standardized read vocabulary already exists
  (`define`, `what_is`, `related`, `is_a`, `band_facts`, `beam`, `path`,
  `neighbors`, `languages`, `translate`, …), catalogued at `/v1/query/shapes`.
- `/v1/explore/entities/{idHex}/mesh`, `/taxonomy`, `/members`, `/containers`,
  `/peers`, `/neighbors` — per-entity structural reads.

What is missing is the **per-layer master page** — there is no
`/explore/highway/ili` or `/explore/highway/deprel` that treats a *layer* as the
subject, with its own standardized query and its own contribution evidence.

## Open questions to settle before building

- Is there a per-layer catalog endpoint, or does this need new API surface?
  `/v1/explore/catalog` returns stages and sources, not highway layers.
- Where does "highway mask" live in the substrate, and what is its read?
  (`docs/decisions/0001-highway-bit-order.md` is the existing reference.)
- What is the concrete contribution metric per layer? Candidates: edges
  contributed to consensus, effect on a beam/walk with the layer's band masked
  out, coverage of a probe set.
- Deprel and POS are lexical-glue/structural bands; their contribution is likely
  measured differently from ILI/synset concept anchors.

## Note on data state

Chess modality is empty (`modalities.chess: 0`) and the DB is being repeatedly
reseeded, so any per-layer counts must read live and degrade honestly when a
layer has not been seeded yet — the existing surfaces already do this
("I hold X but no translation consensus yet").
