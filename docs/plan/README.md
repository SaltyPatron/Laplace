# Product-design index

## Product finish line

[REAL_CONVERSATION_AND_MODEL_CONSENSUS_FINISH_LINE.md](REAL_CONVERSATION_AND_MODEL_CONSENSUS_FINISH_LINE.md)
defines the required MCP/OpenAI product: real stateful conversation, code generation
with toolchain feedback, heterogeneous-model pooled consensus, source inspection, and
deterministic export.

## Workstream designs

[WORKSTREAMS.md](WORKSTREAMS.md) contains the W1–W17 design decomposition. Earlier
analyses are under `docs/archive/plans/workstreams-v1/`.

## Ingest / decomposer authority

[INGEST_BOUNDARY_AND_RECIPE_LAW.md](INGEST_BOUNDARY_AND_RECIPE_LAW.md) is the current
P0 correction for world admission. It separates artifact, transport, parser/source-object,
canonical semantic, and persistence boundaries; source providers recover structure while
the shared machine owns canonical identity/composition/dedup and physical-plan invariance.

[DECOMPOSER_NORMALIZATION_STATUS.md](DECOMPOSER_NORMALIZATION_STATUS.md) records
the merged implementation inventory. The companion
[DECOMPOSER_NORMALIZATION_ISSUE_LEDGER.md](DECOMPOSER_NORMALIZATION_ISSUE_LEDGER.md)
maps the campaign to GitHub owners and falsifiable release gates. Where those older
campaign files use the phrase “vendor composition”, interpret it only as source-specific
recovery/mapping under the boundary law above; it does not grant a private canonical
content-composition or identity implementation.

## Substrate cohesion and SQL

[SUBSTRATE_COHESION_STATUS.md](SUBSTRATE_COHESION_STATUS.md) records the merged
SQL, identity, modality, media, perfcache, operation-surface, extension-install,
and reseed inventory. The companion
[SUBSTRATE_COHESION_ISSUE_LEDGER.md](SUBSTRATE_COHESION_ISSUE_LEDGER.md) maps
every expected outcome to a GitHub owner and falsifiable completion gate.
