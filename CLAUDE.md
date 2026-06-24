# Laplace — codebase notes

## SQL substrate surface — READ THIS BEFORE WRITING OR CALLING SQL
The Postgres extension exposes **188 functions** (62 native C, the rest SQL/plpgsql). Do not
guess what exists — consult:
- **`extension/laplace_substrate/docs/SQL_SURFACE.md`** — high-level map: capability → function
  ("I want to do X → call Y"), file layout, namespace style.
- **`extension/laplace_substrate/docs/FUNCTION_CATALOG.md`** — per-function reference: exact
  return shape, how it's built, and **empirically-tested status** (✅ works / ⚠️ degraded /
  ❌ broken), plus the systemic findings (§0) and the fix/wire/harden checklist (§8).
- Live self-catalog at runtime: `SELECT * FROM laplace.api('<name-fragment>')`.

Key orientation facts (see the catalog for detail):
- The **`recall(prompt)` / `recall_session` router is the product surface** and works well — it
  dispatches NL prompts to the clean functions (`define`, `synonyms`, `isa_path`, `translate_to`).
- The **gold-standard read functions** are `isa_path` and `relate_path`. `synonyms`, `define`,
  `hypernyms` (names) are good.
- Known **systemic issues** (catalog §0): scaffolding relations (PRECEDES/HAS_POS/HAS_LANGUAGE/
  IS_TYPED_AS/Unicode-props) dominate consensus by witness volume, so any mu-ranked "facts about X"
  surface (`links`, `salient_facts`, `epistemic_status`, `walk_*`, `attention`) shows grammar before
  meaning unless it filters scaffolding. Plus: instance/proper-noun pollution in the synset surface,
  `collocates` empty, `structural_neighbors` semantically random, 5,858 tier-law violations.

## Conventions
- Ratings/rd/volatility are **fixed-point ×1e9**. `eff_mu = rating − 2*rd`.
- Entity ids are 16-byte content addresses; provenance lives in attestations, never the id.
- Ingest writes go through `laplace_apply_batch` (set-based COPY-staging merge); heavy compute is
  client-side in `laplace_core`. The DB does only the light merge + the Glicko-2 consensus fold.
- **Do not `git commit`/push unless asked** — commits are done manually / on request.

## Architecture direction (in progress)
Moving toward enterprise-grade: surfaces out of the monolithic `20_converse.sql.in` into modular
files in the **`public` namespace** (dropping the `@extschema@` / `SET search_path` ceremony);
collapsing pass-through duplicates; reconciling the staged `sql/inference/` AI-query copies with the
live `20_converse` originals into one wired home. Nothing is being deleted — "not wired" ≠ "discard".
