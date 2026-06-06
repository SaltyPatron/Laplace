# laplace_substrate SQL modules

**The extension is the deployment unit** (the postgis model). It ships the
complete substrate schema, every function, and its readback seed. The DbUp
chain (`db/migrations/`) is Layer-1 orchestration ONLY — extensions, roles,
grants — and NEVER carries copies of extension objects (the retired pattern
produced 42P13 signature collisions and ~2,300 lines of drift). Greenfield
changes: edit the module, rebuild, nuke+up. Post-greenfield: versioned upgrade
scripts (`laplace_substrate--A--B.sql`, `ALTER EXTENSION … UPDATE`).

Numbering == load order (concatenated by CMake through cpp with
`sqldefines.h.in`; forward references tolerated via check_function_bodies=off,
but keep references flowing backward). **cpp hazard:** never write `*/` inside
a comment (e.g. `DEP_*/FEAT_*` — write `DEP_* / FEAT_*`).

| Module | Concern |
|---|---|
| 01_schema | extension version fn |
| 02–04 | entities / physicalities / attestations tables (EVIDENCE = provenance only) |
| 05_indexes | core table indexes |
| 06_glicko2 | the C kernel surface (aggregate + accumulate_games) |
| 07–09 | coordinate cascade, trajectory ops, BRIN tier ops |
| 10_bootstrap | canonical kinds/types/trust seeding |
| 11_entities_exist_bitmap | engine-backed existence SRF (writer hot path) |
| 12_consensus_schema | `consensus` table + the eff-μ expression indexes + `consensus_id` |
| 13_mu_law | `eff_mu` / `eff_mu_display` / `refuted` + the §10 constants (`glicko2_*`). **The inlining law lives here**: LANGUAGE sql, single expression, IMMUTABLE, NO SET clause — any proconfig de-indexes every ranked read (regress catalog-pins this). Body and index expressions change together or not at all. |
| 14_period_fold | K-partition UNLOGGED staging + the one fold (accumulate-at-ingest; no batch rebuild — by design, do not reintroduce) |
| 15_readback | canonical_id / canonical_names / codepoint_render / constituents / render_text / render / register_canonical(s) — id→content, the substrate's own voice |
| 16_inspect | glass-box SRFs (facets, physicalities, evidence in/out, readable consensus, attestation_response primitives) |
| 17_consensus_reads | ranked-μ inference reads (top_relations, completions, consensus_in/out, generate_tree/greedy, consensus_stats) |
| 18_ops_surface | the operating + GATE surface: relation_type_id (deprecated kind_id wrapper)/source_id, evidence/consensus/content counts, arena_counts, source_counts, entity_type_counts, consensus_tier_distribution, render_gaps, period_staging_status. **Every verification query becomes a function here — never hand-written at a call site.** |
| 19_geometry_consensus | the geometric consensus axis |
| 20_converse | the conversational read surface + loop |
| 21_seed | readback seed shipped WITH the extension: build_codepoint_render() + the static canonical vocabulary. Dynamic families register at ingest (`register_canonicals`). |

New relation kinds: `KindRegistry.Canon` (C#) is the single source of truth for
rank/symmetry/roll-up; the SQL side needs no per-kind DDL (kinds are entities).
New verification/gate queries: add to 18_ops_surface + a regress pin.
