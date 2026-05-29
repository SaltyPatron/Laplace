# Drift register — 2026-05-28

A concept-by-concept reconciliation of every doc/issue/ADR in the corpus, anchored to the current state of code + recent ADRs + memory. Each entry lists the concept, every place stale references live, and the coherent reconciliation that needs to land. **One PR per concept**, touching every file the concept lives in, so all references move together.

Ground truth anchor: ADRs through 0060; memory entries through 2026-05-28; latest commits b6a78b8 … 1141201; Stream A revert + Stream B-min landed; chunk sequence retired (ADR 0060); custom PG runtime is `/opt/laplace/pgsql-18` (ADR 0045 amendment); model ingest is static weight-tensor ETL (ADR 0056), not probe.

---

## C1. Chunk sequence retired — every "Chunk N" framing in active docs

**Ground truth.** ADR 0060 retires chunks-as-a-sequence; `chunk-N` labels are historical grouping only; forward work tracks against the `v0.1 — model round-trip` milestone. Foundation epics #1 + #2 closed; #3 keeps determinism residuals (#49 + #50); #4–#8 + framework epic #232 still open as work containers, but the *ordering obligation* is gone.

**Stale references.**
- `README.md:148-153` — Status table: "Current chunk Chunk 0 ✅ done → Chunk 1 in queue (#1)" — #1 is **closed**. Also "All chunks: Issues filter: chunk-*" header still implies sequential.
- `README.md:191` — `just roundtrip /vault/models/qwen3-0.6b` (memory pins TinyLlama-1.1B as the v0.1 model per project_model_decomposer_attestation_insight; Qwen3-0.6b is an aspirational future). Tangentially related but worth tightening on the same pass.
- `CONTRIBUTING.md:32-44` — "Chunk lifecycle" section built entirely around the retired sequential framing. Open chunk's issue → read scope → implement → close via commit. Needs to become "Issue lifecycle (under the v0.1 milestone)" — same hygiene, no chunk ordering claim.
- `CONTRIBUTING.md:48` — "A chunk is **Done** only when…" — same reframing.
- `CONTRIBUTING.md:50-83` — entire Definition of Done section keyed on "chunk acceptance criteria". Body is fine; vocabulary needs s/chunk/issue (or s/chunk/milestone deliverable).
- `CONTRIBUTING.md:108-120` — Conventional Commits scopes include `chunk-N` — keep as historical/optional, but no longer the recommended scope.
- `CLAUDE.md:148-159` — "Cadence — standing agent operating procedure" / "At the start of each chunk" / "During a chunk" / "At chunk completion" — needs to be reframed against issues + the v0.1 milestone per ADR 0060.
- `DESIGN.md:248` — "(lands Chunks 5/7)" comment on `Laplace.Cli` row.
- `DESIGN.md:506-507` — `liblaplace_synthesis` paragraph: "Real implementations land Chunks 7-8."
- `STANDARDS.md` — `Justfile` snippet references implicitly chunk-aligned recipes; no explicit chunk text to remove.
- `OPERATIONS.md` — clean. No "Chunk N" framing in operational text.
- `FLOWS.md` — has chunk-aware text (e.g., FLOW-BUILD-001 step 9 "Chunk 3+ residuals"); FLOWS.md already self-audits drift in §"Doc / code mismatches" so additional fixes can live in that table.
- All open `Chunk N` epic issues #3 / #4 / #5 / #6 / #7 / #8 — bodies still read as "Chunk N — does X" and reference dependencies on "Chunk N-1 must complete first." Per ADR 0060 these become unordered v0.1-milestone deliverables.

**Reconciliation.** One PR that:
1. Replaces every prose `Chunk N` reference in the four protected docs + AGENTS + CONTRIBUTING with the issue # / milestone framing (chunk-N labels stay on the issues for archaeology).
2. Updates the chunk epic issue bodies (#3–#8) to drop ordering language; cross-link the v0.1 milestone.
3. Updates README Status table to reflect actual milestone progress, not "Current chunk".
4. Updates CLAUDE.md cadence section.

---

## C2. AI-model ingest is static weight-tensor ETL (ADR 0056) — every "probe" reference is stale

**Ground truth.** ADR 0056 replaced probe-based ingest with `WeightTensorETL` — static computation of typed-arena-matchup observations from weight tensors. No model invocation. No GPU at ingest. The substrate doesn't load + doesn't execute + doesn't run probe forward passes. Per ADR 0056's own "Consequences" section, this invalidates probe framing in R3, R8, ADR 0007, ADR 0036 trust-class list item 6, ADR 0037, ADR 0043, ADR 0044 trust class tier 7 naming + T9 description, GLOSSARY "Probe (in ingestion context)", and DESIGN VII.

**Stale references.**
- `RULES.md:74-78` (R3) — Pass 3 "Probe-validated retention test (synthesize candidate sparse subgraph; verify behavior preserved on probe set)" → should be static-mathematical validation (spectral preservation / singular-value retention / matchup-distribution preservation between sparse + dense subgraphs).
- `RULES.md:122-126` (R8) — "**The one exception:** probe-time forward-pass of a model being ingested (running a 70B transformer to extract attestations may need GPU). After extraction, the substrate is CPU-only. GPU is the probe driver, NOT a runtime requirement." → The exception is stale; no GPU at ingest under ADR 0056. R8 collapses to: no GPU, period.
- `STANDARDS.md` Kind value tiers, T9 row: "Tensor-Calculation … single-probe trust; cluster across many models for higher confidence" → s/single-probe/single-model/. **Anchor flag (truth #5, docs/SUBSTRATE-FOUNDATION.md):** the word "tier" is reserved exclusively for the Merkle stratum (T0 = Unicode codepoints). A "Kind value tiers" table is itself a fixed-class ladder — corruption per truth #5; trust is a Glicko-2 value that self-tunes from cross-source agreement, never a tier or fixed class. This row needs more than an s/probe/model/ scrub; the tier framing itself is on the correction list. Surface with Anthony.
- `GLOSSARY.md:483-485` — "Probe (in ingestion context)" entry — either delete or move to the Anti-vocabulary section as a forbidden historical pattern.
- `GLOSSARY.md:147` — Trust class table row 7 "Single-model probe observations" → "Single-model static weight-tensor ETL observations". **Anchor flag (truth #5):** a "Trust class table" with numbered rows is a fixed-class trust ladder — corruption per truth #5. Trust is a self-tuning Glicko-2 value, not a class/row. The probe→static-ETL scrub is correct but does not address the deeper drift that the trust-class ladder itself contradicts the anchor. Surface with Anthony.
- `GLOSSARY.md:316-322` — Lottery-Ticket-Aware Sparsity entry pass (c) "probe-validated retention test" → static-mathematical validation.
- `GLOSSARY.md:486-489` — "ModelDecomposer" / round-trip framing is fine post-amendment, but the "probe" word should be scrubbed everywhere it appears in per-decomposer ecosystem rows. ModelDecomposer Layer-10 row uses "behavioral attestations" — fine; check that "probe observations" isn't smuggled in.
- `DESIGN.md` §VII Lottery-ticket-aware sparsity bullet "probe observations over prompts/tasks selected for the architecture" → remove or replace with the static-ETL framing.
- `DESIGN.md` §VII "lottery-ticket sparse edges that survive per-tensor, per-row, and probe validation gates" → "and static-mathematical validation gates".
- `ADR 0007` — full read pending, but per ADR 0056 the third pass needs the same reframing.
- `ADR 0036:33` — trust class list "AI model-derived probe observations" → "AI-model static-ETL observations". (Same anchor flag as above: the "trust class list" is itself a fixed-class ladder contradicting truth #5; the probe scrub does not resolve that. Surface with Anthony.)
- `ADR 0037:35` — "probe observations, architecture-specific attestation arenas" → drop "probe observations" or replace.
- `ADR 0043` — body uses "probe observations" + "ProbeObservation" — replace.
- `ADR 0044:60-61` Part B row 7 — "TrustClass_AIModelProbe" entity name + "TransformerModelDecomposer probe observations from a single model". **Anchor flag (truth #5):** the entire `TrustClass_*` taxonomy is a fixed-class trust ladder — corruption per truth #5 ("trust is a Glicko-2 value, self-tuning from cross-source agreement — never a tier or fixed class. Any … TrustClass_* ladder … is corruption"). The drift here is NOT "probe vs static-ETL naming" and NOT "rename vs keep canonical hash" — it is that a hardcoded `TrustClass_*` entity ladder should not exist at all; source trust must emerge as a Glicko-2 value. Rewording probe→static-ETL inside a banned ladder leaves the contradiction in place. This is a substrate-conceptual change, not a hash-churn tradeoff. Surface with Anthony — do not auto-resolve by picking a new ladder label.
- `ADR 0044:36` Part A T9 row "single-probe trust" → "single-model trust". (Anchor flag: if T9 lives in a "kind value tier" / trust-class table, the tier/class framing is itself corruption per truth #5 — see the STANDARDS and ADR 0044 Part B notes above.)
- Issue #221 ("ADR — Architecture-family vocabulary extensions") and #222 (memory-bounded streaming for matchup computation) — bodies likely use probe framing; check + update.

**Reconciliation.** One PR that:
1. Amends RULES R3 + R8 (R12-protected — diff first, you approve).
2. Updates GLOSSARY entries (R12-protected — diff first).
3. Updates DESIGN §VII (R12-protected — diff first).
4. Updates STANDARDS T9 row (R12-protected — diff first).
5. Files a small supersession note on ADR 0007 / 0036 / 0037 / 0043 / 0044 OR amends in place with explicit "Amended 2026-05-28: probe framing superseded by ADR 0056" headers (ADRs are not R12-protected, but amending in place is the project's standing pattern; see ADRs 0017 / 0023 / 0025 / 0027 / 0045 / 0047 / 0053 / 0056 for the convention).
6. **Anchor flag (truth #5)**: this PR must NOT silently rename or scrub `TrustClass_AIModelProbe` into another `TrustClass_*` label — the whole trust-class ladder contradicts truth #5 (trust is an emergent Glicko-2 value, never a fixed class/tier). The probe→static-ETL prose scrub is fine; the deeper trust-class drift is a substrate-conceptual question for Anthony, tracked under C8. Do not resolve it inside this scrub.

---

## C3. Custom-built PG at `/opt/laplace/pgsql-18` is the runtime — system PG is retired

**Ground truth.** ADR 0045's 2026-05-24 amendment + bootstrap commit `67c3808` establish that the substrate runs against the custom-built PG cluster at `/opt/laplace/pgsql-18`. The system `postgresql-18` apt package may be present but is NOT the runtime. The `extension_control_path` + `dynamic_library_path` GUC pattern + the "staged extensions under `/opt/laplace/{lib,share}/postgresql/18`" framing is interim and superseded.

**Stale references.**
- `OPERATIONS.md:107` — Build defaults: "`LAPLACE_PG_PREFIX=/usr/lib/postgresql/18` (stock PG; override via env var for the custom-built PG at /opt/laplace/pgsql-18)". Should flip: default is `/opt/laplace/pgsql-18`; the override case is the deprecated system-PG fallback (and may not need to be supported at all).
- `OPERATIONS.md:108` — Install: "sudo-free thanks to LAPLACE_INSTALL_STAGED=ON (extensions land in /opt/laplace/{lib,share}/postgresql/$PG_MAJOR; PG finds them via the conf paths set by bootstrap_pg_extension_paths)" — the staged-paths story is the system-PG framing. Under the custom-PG framing the extensions install directly into the custom PG's `lib/postgresql` + `share/postgresql/extension`. Different install paths; different rationale.
- `OPERATIONS.md:130-141` — Layer 1 / extension lifecycle — "`just launch-db` Start the system Postgres cluster" is the wrong cluster.
- `OPERATIONS.md:193` — `just regress` description: "smoke tests for laplace_geom + laplace_substrate against the running system PG with staged extensions installed" — wrong cluster.
- `OPERATIONS.md:210-212` — Launch internals: "The extensions install under that prefix's lib/postgresql/ + share/postgresql/extension/" — this is correct for custom PG but the surrounding text mixes the two framings.
- `OPERATIONS.md:336-343` — "Persistent state outside the repo" + "Data on the user's machine" — `/var/lib/postgresql/18/main/` is named as the PG data directory; that's the *system* PG. Custom PG's data directory is wherever the cluster initdb landed (typically `/opt/laplace/pgsql-18/data` or `/var/lib/laplace-pgsql`).
- `OPERATIONS.md:339` — "engine library + staged-extension install (per ADR 0045)" — "staged" is the interim framing; current is direct install into custom PG prefix.
- `ADR 0024:31` — "the same .so files are linked by the PG extensions (via PGXS) AND loaded by .NET via P/Invoke" — "via PGXS" is stale per ADR 0032 (PGXS retired). Mentioned again in C4 below.
- `ADR 0025:76` — "CI build/install steps double up — sudo make install runs in both extension subdirs. Bounded sudoers entry (make install*, per ADR 0019) already covers this" — `make install` is PGXS; per ADR 0032 it's `cmake --install` now.
- `ADR 0026:51` — `Laplace.Decomposers.Text` "IDecomposer for text (UAX#29 grapheme clusters via ICU)" — per ADR 0047 text decomposition is pure-primitive engine-side, not C# via ICU. Drift adjacent to C2/C5 but worth catching here.
- `FLOWS.md:340-343` — "Custom-built PG runtime (the actual runtime path)" — already partially updated; check end-to-end coherence.

**Reconciliation.** One PR updating OPERATIONS.md to (a) default `LAPLACE_PG_PREFIX=/opt/laplace/pgsql-18`, (b) remove the staged-extensions framing, (c) name the actual custom-PG data directory, (d) fix `just launch-db` description, (e) fix `just regress` description. ADR 0024 + 0025 + 0026 minor updates land in the same PR. R12-protected: diff first for OPERATIONS.

---

## C4. PGXS retired (ADR 0032) — every PGXS / Makefile reference is stale

**Ground truth.** ADR 0032 retires PGXS in favor of unified CMake (Path B). `cmake --install`, not `make install`. No PGXS Makefiles in `extension/{laplace_geom,laplace_substrate}/`.

**Stale references.**
- `ADR 0024:31` — "(via PGXS)" qualifier.
- `ADR 0025:75` — "Folder restructure required. `extension/` becomes `extension/{laplace_geom,laplace_substrate}/`, each with its own Makefile (PGXS)" — wrong; CMake, no PGXS.
- `ADR 0025:76` — "sudo make install runs in both extension subdirs" — should be cmake --install.
- `OPERATIONS.md:108` already says CMake-driven install. Consistent.
- `STANDARDS.md:331` — "CMake as the engine + extension + top-level build system (Path B per ADR 0032; PGXS retired)" — already correct.
- `RULES.md` R17 (ADR 0034) — modular `.sql.in` via cpp; no PGXS implication. Correct.

**Reconciliation.** Amend-in-place ADR 0024 + 0025 with "Amended 2026-05-28: PGXS framing superseded by ADR 0032" headers + line-level updates.

---

## C5. IDecomposer is C# (ADR 0051), IFormatWriter is C# (ADR 0059) — DESIGN §VI C++ sketches are superseded

**Ground truth.** ADR 0051 explicitly supersedes the C++ `IDecomposer` sketch in DESIGN VI with a C# contract. ADR 0059 explicitly supersedes the C++ `IFormatWriter` sketch in DESIGN VI with a C# contract. `IArchitectureTemplate` + `IFeatureExtractor` + `ISource` + `IProtocolEndpoint` haven't been explicitly re-spec'd in C# (per their respective ADRs they remain partially C++ engine primitives with C# binding layers; that's still consistent with current architecture).

**Stale references.**
- `DESIGN.md:649-654` — `class IDecomposer { virtual TierTree decompose(const Bytes& content) = 0; virtual std::vector<EntityRef> chunk_at_tier(const Entity& parent, Tier t) = 0; ... };` — superseded by ADR 0051's C# `IDecomposer`. Should reference ADR 0051 + note the C++ sketch is historical / superseded for this interface; the *engine-side* primitives `TextDecomposer` + `HashComposer` are the C kernels per ADRs 0047 + 0048.
- `DESIGN.md:664-669` — `class IFormatWriter { virtual void write(const ModelData&, std::ostream&) = 0; ... };` — superseded by ADR 0059's C# `IFormatWriter`. Should reference ADR 0059 + note the C++ sketch is historical.
- `DESIGN.md:656-662` — `IArchitectureTemplate` C++ sketch is still load-bearing per ADR 0011 + ADR 0043 (template wires substrate-canonical recipe layout back into emit-time tensor distribution per memory codec_synthesis_not_pseudoinverse) — keep, but cross-link ADR 0011 + ADR 0043.
- `RULES.md` R10 — lists the six plugin interfaces by name; correct vocabulary; no per-interface language placement claim. Fine.
- `STANDARDS.md` — interface I-prefix discipline applies to both C++ + C#. Fine.

**Reconciliation.** R12-protected DESIGN.md edit: add supersession notes pointing at ADR 0051 + ADR 0059. Diff first.

---

## C6. ADR 0017 internal inconsistency — body still says "update STATE.md" after the amendment retired STATE.md

**Ground truth.** ADR 0017's amendment header (2026-05-24) retires `STATE.md` / `decisions.md` cadence files. ADR 0017's body still reads "At chunk end: tick acceptance criteria; close issue via commit; update STATE.md." Internal contradiction.

**Stale references.**
- `ADR 0017:23` — "At chunk end: tick acceptance criteria; close issue via commit; update STATE.md." — drop the STATE.md sentence; the chunk framing also needs C1's reconciliation.
- `ADR 0017:31` — Consequence: "Future agent sessions read STATE.md + decisions.md + ADRs + plan.md and resume exactly where left off." — drop STATE.md + decisions.md + plan.md (the only durable record is issues + ADRs per the amendment).

**Reconciliation.** Amend-in-place edit to ADR 0017 body, consistent with its own amendment header.

---

## C7. `laplace_priv` wrapper / SECURITY DEFINER pattern fully retired (ADR 0045) — cross-check residuals

**Ground truth.** ADR 0045: `laplace_admin` is SUPERUSER; the `laplace_priv` schema + `install_extension` / `drop_extension` wrappers + the allowlist + `template_laplace` machinery are all removed. ADR 0023, ADR 0025, ADR 0027 are amended by 0045.

**Stale references.**
- ADR 0023 + ADR 0025 + ADR 0027 — already note the amendment per ADR 0045's "Amends to prior ADRs" section. Read-through quick — ADR 0025:74 explicitly notes the supersession in a strikethrough block. ADR 0025:77 also notes it. Looks clean.
- `OPERATIONS.md:212` — already says "DbUp connects as laplace_admin via peer auth and runs CREATE EXTENSION directly — no SECURITY DEFINER wrapper" — current.
- `RULES.md:234` (R16) — SQL row references ADR 0045 + says `laplace_admin is SUPERUSER`. Current.
- `db/migrations/` — should not reference `laplace_priv.*`. Quick grep would confirm.

**Reconciliation.** Likely no doc churn needed; one grep across the repo + a quick fixup PR if anything residual surfaces.

---

## C8. ADR 0044 + GLOSSARY `TrustClass_AIModelProbe` — the trust-class ladder itself contradicts the anchor

**Anchor flag (truth #5, docs/SUBSTRATE-FOUNDATION.md).** This is NOT a "naming churn vs canonical-name stability" decision. Truth #5 states: trust is a Glicko-2 value, self-tuning from cross-source agreement — *never a tier or fixed class*; any `TrustClass_*` ladder is corruption; the word "tier" is reserved exclusively for the Merkle stratum (T0 = Unicode codepoints). The real drift is that a hardcoded `TrustClass_*` entity taxonomy (and the "kind value tier" / "trust class table" framing it sits in) should not exist; source trust must emerge as a Glicko-2 value, not be assigned by class. Picking between "rename to TrustClass_AIModelStaticETL" and "keep the canonical name" is choosing between two corrupt forms. This is a substrate-conceptual question for Anthony, not a PR-bounding tradeoff. Do not auto-resolve.

---

## C9. IngestRunner checkpoint outlives db-nuke — issue # mismatch in memory

**Ground truth (from memory).** `feedback_ingest_checkpoint_db_drift`: `/tmp/laplace-ingest/<source>/checkpoint.bin` survives db-nuke + content-addressed intent IDs match across runs → after nuke the runner SKIPS intents whose entities are gone → FK failures. Workaround = wipe checkpoint after nuke; real fix = substrate-side intent journal per Story #229.

**Issue #229's actual title** = "ADR — Determinism verification for ingest (tracking)". Different story from "substrate-side intent journal". Either the memory entry is wrong about #229 being the substrate-side intent journal story, or there's a related issue I haven't found yet, or the intent journal work is folded under #229's body. Need to read #229's body to confirm.

**Reconciliation.** Read issue #229 body. If it covers the substrate-side intent journal, update memory entry to clarify; if not, open the missing story + fix the memory reference.

---

## C10. FLOWS.md self-audit (`Doc / code mismatches (audit 2026-05-25)`) — already-flagged residuals to either fix or annotate as still-open

FLOWS.md is the only doc that audits itself. Its existing table (`Doc / code mismatches`, lines 804-818) names:
- `Writer dedup` — ADR 0050 / writer XML says MerkleDedup.FilterNovel; writer uses inline bitmap loop.
- `Unicode layer #` — ADR 0037 layer 1 vs UnicodeDecomposer.LayerOrder = 0.
- `ADR 0050 / 0052 status` — Proposed in ADRs but code + tests exist (should bump to Accepted).
- `Layer gate loop` — ADR 0052 1-based vs code 0-based.
- `HasLayerCompleted` attestations — reader MVP may always return false until #183.
- `Perf-cache vs DB cross-verify` — partial; `scripts/verify-perfcache.sh` missing.
- `entities_exist_bitmap` — implemented as `11_entities_exist_bitmap.sql.in`.

**Reconciliation.** Either reconcile each (code or doc moves) or update FLOWS.md to note which still-stand as work items. Each item small enough to land alongside whichever concept PR it correlates with (FLOW writer-dedup → C2-adjacent; FLOW unicode-layer-number → choose 0 or 1 and update both sides; FLOW status fields → flip ADR 0050 + 0052 to Accepted in their own micro-PR).

---

## C11. ADRs in `Proposed` status that have shipped to code

`Proposed` status with shipped code (production code + tests under `app/` or `engine/` referencing the ADR):
- ADR 0049 `SubstrateChange` — type lives at `app/Laplace.SubstrateCRUD/SubstrateChange.cs`.
- ADR 0050 `SubstrateCRUD` — type lives at `app/Laplace.SubstrateCRUD/Npgsql/NpgsqlSubstrateWriter.cs`.
- ADR 0051 `IDecomposer` — lives at `app/Laplace.Decomposers.Abstractions/IDecomposer.cs`.
- ADR 0052 `IngestRunner` — lives at `app/Laplace.Ingestion/IngestRunner.cs`.
- ADR 0054 selective deployment profiles — partially implemented per ADR 0053 amendment.
- ADR 0055 static structural parse — partial (no per-format parsers shipped beyond safetensors mention).
- ADR 0056 weight-tensor ETL — Stream B-min shipped per commit f8b0be4 (memory project_stream_a_codec_revert_2026_05_27).
- ADR 0057 substrate emission discipline — codified but no enforcement code yet; doc-only artifact.
- ADR 0058 canonicality criterion — doc-only artifact.
- ADR 0059 format-writer matrix — doc-only artifact + scaffold per memory project_stream_a_codec_revert_2026_05_27.

**Reconciliation.** Flip status `Proposed` → `Accepted` for ADRs whose code has shipped + tests cover it. Per-ADR one-liner; can batch in a single "ADR status reconciliation" PR.

---

## C12. Universal CLI surface — issues #259–#279 codify the C# refactor; no stale doc surface to update beyond what they themselves cover

The cleanup epic #259 already opened with #260–#279 as its stories. The stories themselves are the reconciliation. No additional doc drift surface to land here — verify the epic + stories are coherent post-Stream-A revert (commit be99495). Memory's `feedback_universal_cli_surface` already names the principle (no model-family CLI verbs; `synthesize substrate <recipe.json>` / `synthesize passthrough <model-dir>` / `ingest <source>`). Consistent.

---

# Sequencing — which PRs land in what order

| # | Concept | PR shape | Blocks |
|---|---|---|---|
| 1 | C11 ADR status flips | Tiny; ADRs only | Nothing |
| 2 | C7 cross-grep `laplace_priv` residuals | Tiny | Nothing |
| 3 | C6 ADR 0017 internal inconsistency | Tiny ADR amend | Nothing |
| 4 | C4 PGXS scrub | ADR 0024 + 0025 amend | Nothing |
| 5 | C5 DESIGN §VI supersession notes | R12-protected — diff first | Nothing |
| 6 | C2 probe → static-ETL scrub | R12 RULES + GLOSSARY + DESIGN + STANDARDS diffs, ADR amend batch | C8 |
| 7 | C8 trust-class ladder contradicts anchor truth #5 | OPEN — surface with Anthony; do not rename or scrub a banned ladder into another banned ladder | — |
| 8 | C3 custom-PG runtime cleanup | R12 OPERATIONS diff | Nothing |
| 9 | C1 chunk-sequence retirement language scrub | R12 CLAUDE + CONTRIBUTING + DESIGN + README diffs + issue body edits | Nothing |
| 10 | C9 verify issue #229 covers intent journal | Read + memory fix or new story | Nothing |
| 11 | C10 FLOWS.md residuals | Land alongside affected concept PRs | Various |

Cleanup epic = the index issue tracking these 11 PRs.
