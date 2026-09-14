# Language highway — master/detail surfaces

Status: **UI/product navigation decision.** Originally recorded 2026-08-10 during the `feat/ui-user-surfaces` session.

This decision governs how the language-highway product surface should be navigated. It does **not** rank the importance of substrate mechanisms and does not redefine Laplace cognition. The glome/bounded physicality, trajectories, containment, walk/search operators, coupling/response field and constellation/web views are computational or inspection primitives; calling any of them “just a visual” is incorrect.

## The ask

Provide master/detail product pages for each major language-highway layer with standardized, named and optimized reads, including surfaces such as:

- highway mask / relation bands;
- ISO 639 / language axis;
- ILI concept anchors;
- synsets;
- frames;
- POS;
- senses;
- dependency relations;
- VerbNet classes;
- PropBank rolesets/roles;
- other governed mesh layers as they are admitted.

## Product purpose

The surface should make each layer inspectable as part of the complete inference/generation machine rather than presenting a database inventory with no indication of how the layer participates.

For each layer the product should answer:

1. **What is here?** Coverage/residency/sources/generations.
2. **How is it addressed?** The canonical optimized operation/index/read rather than page-specific ad-hoc SQL.
3. **How can it respond?** Which typed coupling/search/evidence operations this layer participates in.
4. **What did it contribute to this operation?** Query-relative trace/receipt evidence, not merely a global popularity score.

The fourth point matters because contribution is query-relative. A layer can be structurally rich yet irrelevant to one request, decisive to another, or contradictory to a third.

## Navigation shape: familiar master/detail first

The product-navigation analogy remains useful:

```text
league → division → team → position → player → roster → schedule/results
```

The point of the analogy is not that Laplace itself is a hierarchy. The substrate is an overlapping recursive/relational web. The product needs a familiar **entry/navigation grammar** so users can browse a known layer without first operating a 3D/4D visualization.

A rough language-highway mapping is:

| League site | Language highway |
|---|---|
| league | highway / knowledge family |
| division/conference | layer (ISO, ILI, synset, frame, POS, sense, deprel, …) |
| team | hub such as synset/frame/class/roleset |
| position | relation/role type |
| player | surface/sense/lemma/entity |
| roster | hub members / contained structures |
| schedule/results | witnessed occurrences/relations and their outcomes |
| standings | declared ranked arena using consensus/other typed measures |

Tables, rosters, standings and record pages are therefore a primary **browse surface**. They do not replace trajectories, glome/geometry, web/constellation views, traces or walk/coupling inspection. Those remain available as alternate views of the same canonical state.

## Relationship to cognition

This decision must not create one semantic pipeline stage per UI page.

The actual cognition law is governed by `docs/specs/36_Laplace_Forward_Pass.md`:

```text
RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE → PROPOSE → STEER → SELECT → REALIZE → WITNESS
```

A request may cause several highway layers to respond simultaneously. The query-relative coupling field preserves those typed responses before joint interpretation/routing. The product can then show which layers/relations/occurrences/evidence actually tugged back.

That is stronger than an ablation-only notion of “which pipeline stage mattered.”

## Product requirements

A conforming layer master/detail surface should reuse the common entity/world operation model:

- stable URLs/identities for resources;
- paginated/streamed complete selected sets rather than arbitrary UI top-K ceilings;
- canonical operation/API surfaces shared with CLI/MCP/other product fronts;
- declared ranking arena/measure/context/epoch;
- provenance and source coverage;
- relation/containment/trajectory navigation;
- per-operation coupling/trace contribution where available;
- glome/graph/trajectory/constellation visualizations as additional views, not disconnected tools.

## Historical implementation notes

The original decision recorded then-live routes such as `/explore/mesh`, entity/topic detail surfaces and query shapes. Those observations are historical evidence from 2026-08-10, not a perpetual statement that those exact routes remain the current product contract.

Current implementation status must be read from the repository/product tests and owning issues rather than inferred from this decision record.

## Open acceptance questions

- What current canonical operation enumerates each highway layer and its members?
- How does the UI expose a layer's indexed/provider participation in the coupling field?
- Which query-relative contribution/ablation metrics are meaningful for each typed layer without collapsing unlike evidence into one score?
- How are large layer sets paged/streamed while retaining deterministic ordering and complete-set semantics?
- Which current product route owns this surface, and which historical routes have been superseded?

This decision answers the **navigation/product shape**. It does not demote the physicality, trajectory, web, walk/search or coupling mechanisms that make the data useful.
