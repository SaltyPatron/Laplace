# 42 — Issue backlog triage ledger (2026-08-02)

Audit/disposition campaign implementing the Issue backlog triage plan.
Baselines: `.scratchpad/37` (2026-07-26) and `docs/plan/` (completion axis only).
Ground truth: GitHub open issues + code + live ops notes in CHECKPOINT.

**Open count at start of pass:** 216  
**Open count at end of pass:** 195 (all carry exactly one or more `triage:*` labels; `priority:high` = 12)  
**Closed this pass:** 24 (Phase B ×20 + Phase C ×4: #540/#541/#555/#595)  
**Filed this pass:** #791 (monetization), #792 (empty-substrate alarm), #793 (model-lane campaign)

## 0. Trust order

1. Live GH issue state after this ledger's dispositions
2. Code / `psql` / CI for any "done" claim
3. `docs/plan/CHECKPOINT_2026-08-02.md` for the ~15 completion-axis issues
4. This ledger for backlog disposition
5. `.scratchpad/37` as **drift baseline only** — not status authority

## 1. Drift vs doc 37 (re-measured 2026-08-02)

| Claim (doc 37) | Now | Disposition |
|---|---|---|
| 200 open issues | **216** | Tracker still accumulates |
| `priority:high` = 26; P5 said force ≤10 | **33** | Phase E demotes to ≤12 |
| `tracker-migration` = 25 | **25 unchanged** | Phase B/D drain |
| `model-lane` = 31 | **31 unchanged** | `triage:deferred` campaign parent in Phase D |
| ADR 0001 PROPOSED; unblocks #551/#399/#413 | ADR **ACCEPTED**; **#551 CLOSED** | #399 stays decision; #413 re-scope/close |
| Close #375 as retired-by-spec-36 | Still open | Phase B close |
| File monetization epic | Still untracked (#531 only nearby) | Phase D file or defer umbrella |
| P0 empty substrate | Mutated → #777 orphan Unicode journal + thin residue | `triage:ops-blocked` |
| P1 S7 wire / converse finish | Reframes as #751/#755/#758 in `docs/plan/` | Carve-out `triage:completion-axis` |
| P3 #111/#112 llama.cpp | Still open | Keep under foundry campaign |
| P4 model-lane witnessing list | Still open | Deferred campaign |
| Closed since 07-26 | ~4 issues | Closing pressure was near-zero |

## 2. Triage label meanings

| Label | Meaning |
|---|---|
| `triage:active` | Verified open work; keep and schedule |
| `triage:likely-done` | Acceptance likely met; verify then close |
| `triage:decision` | Needs operator/ADR decision before build |
| `triage:research` | Spike/research; not a sprint delivery |
| `triage:stale-framing` | Framing retired by later spec; close or rebind |
| `triage:ops-blocked` | Blocked on live seed/ops proveability |
| `triage:completion-axis` | Owned by `docs/plan/` — do not re-prioritize here |
| `triage:deferred` | Coverage widening; not finished-v1 blocking |

## 3. Initial disposition census (Phase A)

| Count | Label |
|---:|---|
| 96 | `triage:active` |
| 56 | `triage:deferred` |
| 25 | `triage:stale-framing` |
| 14 | `triage:completion-axis` |
| 12 | `triage:decision` |
| 10 | `triage:research` |
| 2 | `triage:ops-blocked` |
| 1 | `triage:likely-done` |

Primary-label cohort sizes (one primary per issue for census):

| Count | Cohort |
|---:|---|
| 86 | (other / multi / unlabeled) |
| 26 | model-lane |
| 25 | tracker-migration |
| 24 | ingest |
| 16 | read-side |
| 16 | perf |
| 12 | substrate-law |
| 9 | foundry |
| 2 | design-decision (as primary; 12 total with label) |

## 4. Completion-axis carve-out

Owned by `docs/plan/` + CHECKPOINT. Triage only syncs titles/close criteria.

| Issue | Title | Triage |
|---|---|---|
| #574 | chess: book decomposer under-extracts — grandmaster books yielded | `triage:completion-axis` |
| #751 | generation: wire the steered lane into chat — walk shape runs the | `triage:completion-axis` |
| #752 | substrate: tier-collision seam — a surface's sense set unions its | `triage:completion-axis` |
| #753 | coherence: sense election needs a salience prior — content-band m | `triage:completion-axis` |
| #754 | ingest: finish the document lane (Pillar 0) — stop borrowing User | `triage:completion-axis` |
| #755 | eval: generation-quality harness — prompts_smoke.txt has no runne | `triage:completion-axis` |
| #756 | read-side: questions route themselves — relation-name attestation | `triage:completion-axis` |
| #757 | read-side: port infer() to C — both directions, n-hop bias family | `triage:completion-axis` |
| #758 | gates: implement spec 37 G1-G10 — elector done; G4 dead-canonical | `triage:completion-axis` |
| #759 | voice: discourse readback — orientation reads one topic id while  | `triage:completion-axis` |
| #760 | ops: source_roster — code excludes relation bootstrap; live Chess | `triage:likely-done` |
| #761 | campaign: seed the corpus through finished lanes, re-scoring the  | `triage:completion-axis` |
| #764 | standardization: 358 functions, zero recorded dependencies — adop | `triage:completion-axis` |
| #765 | code lane: .sql.in discovery done; SQL still emits zero DEFINES/C | `triage:completion-axis` |
| #777 | ops: foundation seed blocked — orphan Unicode journal + prove #77 | `triage:ops-blocked` |
| #785 | read-path: sense election ignores the language it already compute | `triage:completion-axis` |

Note: #777 is labeled `triage:ops-blocked` (ops wins for scheduling) but remains on the completion Phase 0 map. #760 is `triage:likely-done` (code in #773; close after live ChessPgn/ChessOpenings recheck).

## 5. `priority:high` inventory at Phase A (33)

| Issue | Triage | Title |
|---|---|---|
| #8 | `triage:active` | Milestone: run an exported substrate GGUF in llama.cpp and j |
| #121 | `triage:active` | Epic D — MKL / Eigen / Spectra / TBB integration + determini |
| #164 | `triage:active` | D.6 — Determinism ctest — same output across thread counts |
| #165 | `triage:active` | D.7 — CI verification of MKL/TBB linkage |
| #259 | `triage:active` | Refactor C# app to engine-orchestration shape (delete reinve |
| #264 | `triage:active` | Recipe lane residual: RecipeInfo DTO + managed canonicalizat |
| #272 | `triage:active` | Export metadata must be recipe-driven — WriteGgufMetadata is |
| #273 | `triage:active` | Move byte-BPE re-encoding + generation_config read out of Wr |
| #365 | `triage:active` | ConceptNet: re-seed on hart-desktop (0 attestations currentl |
| #368 | `triage:active` | Uncracked-List B — Native SPI scorer (model_pair_score/model |
| #374 | `triage:active` | Uncracked-List I — Argentina gate (campaign acceptance test) |
| #379 | `triage:active` | doc 18 Q6 — Echo-loop guard for the generation corpus (self- |
| #488 | `triage:deferred` | model-lane: model_forward v0 was merged while labeled UNVERI |
| #489 | `triage:active` | chess: HTTP /chess/* surface is unauthenticated (C04 auth ha |
| #512 | `triage:active` | chess: build the board geometry ladder — square/piece S3 anc |
| #515 | `triage:deferred` | foundry: tier-scheduled layer operators — replace all-ops-ev |
| #521 | `triage:deferred` | foundry: typed residual stratum allocation (S/W/C/F/G subspa |
| #525 | `triage:active` | substrate: highway perfcache blob has no BLAKE3 CRC and no i |
| #540 | `triage:deferred` | model-lane: hidden_act is never read — activation identity i |
| #547 | `triage:active` | chess: game-tier mantissa-packed trajectory is not wired — E |
| #548 | `triage:active` | ingest: XPOS tags are minted unnamespaced (bare NodeHash) wh |
| #555 | `triage:decision` | generation: corpus is containment (containers_of), not a pin |
| #588 | `triage:ops-blocked` | write path: 5 substrate perf/consolidation gates RED on main |
| #595 | `triage:active` | engine: grammar_compose span lookup + span array growth are  |
| #596 | `triage:active` | ingest document: malformed-encoding file crashes the entire  |
| #751 | `triage:completion-axis` | generation: wire the steered lane into chat — walk shape run |
| #752 | `triage:completion-axis` | substrate: tier-collision seam — a surface's sense set union |
| #754 | `triage:completion-axis` | ingest: finish the document lane (Pillar 0) — stop borrowing |
| #755 | `triage:completion-axis` | eval: generation-quality harness — prompts_smoke.txt has no  |
| #758 | `triage:completion-axis` | gates: implement spec 37 G1-G10 — elector done; G4 dead-cano |
| #764 | `triage:completion-axis` | standardization: 358 functions, zero recorded dependencies — |
| #765 | `triage:completion-axis` | code lane: .sql.in discovery done; SQL still emits zero DEFI |
| #785 | `triage:completion-axis` | read-path: sense election ignores the language it already co |

## 6. finished-v1 lens (from doc 37; status refreshed)

1. **Converse** — specified in `docs/plan/`; live seed blocked (#777); S7 wire (#751) open.
2. **Export** — #8 / #111 / #112 / #114 still open.
3. **Sell** — untracked; Phase D files epic or records explicit deferral.

Coverage widening (most `model-lane`, many `perf`) is `triage:deferred`.

## 7. Phase log

### Phase A (this section) — DONE 2026-08-02

- Created `triage:*` labels
- Bulk-labeled all 216 open issues
- Wrote this ledger
- INDEX.md update deferred to Phase E

### Phase B — cheap closes / supersedes

See §8 after execution.

### Phase C — hot-path verify

See §9 after execution.

### Phase D — campaign umbrellas

See §10 after execution.

### Phase E — priority rebalance + INDEX

See §11 after execution.

## 8. Phase B dispositions

**20 closes** with evidence comments (2026-08-02):

| Issue | Disposition | Evidence |
|---|---|---|
| #375 | closed — retired framing | spec 36 §6 |
| #413 | closed — defect class cured | ADR 0001 ACCEPTED + #551; residual = ops reseed |
| #422 | closed — fixed | `Justfile` shebang; `just --list` ok |
| #423 | closed — fixed | `build-system-deps.sh` `safe.directory=*` |
| #396 | closed — framing false | real `canonical_coord` extractor |
| #402 | closed — id-law pinned | `PhysicalityIdRegressionTests` |
| #404 | closed — no repro | wait-for-reproduction since migration |
| #414 | closed — fixed | `EstimateMatchupUnits` structure-mode |
| #415 | closed — non-delivery | analyzer smell, not recorder bug |
| #424 | closed — umbrella bag | not actionable |
| #432 | closed — telemetry landed | `MultiFileTelemetry`; Pillar 0 → #754 |
| #230 | closed — ADR tracker dead | `docs/adr/` removed; code lives |
| #361 | closed — superseded | → #751 / #755 (spec 36 §6) |
| #398 | closed — superseded | → #376 |
| #407 | closed — superseded | → #462 |
| #419 | closed — superseded | → #433 |
| #421 | closed — superseded | → #430 |
| #220 | closed — superseded | → #272 / #273 |
| #227 | closed — superseded | → #539 |
| #229 | closed — superseded | → #50 |

Remaining former `stale-framing` KEEP items re-labeled: #401/#417/#418/#433 → `triage:active`; #403/#409/#412/#429/#430/#431 → `triage:deferred`. #399 comment: grain decision still owed. #760 comment: likely-done pending live recheck.

## 9. Phase C dispositions

Code-verified hot path (priority:high + design-decision + doc37 P3/P4). Comments left on each issue.

### Closed as already-fixed
| Issue | Evidence |
|---|---|
| #540 | `hidden_act` read + profile applied |
| #541 | `NormEps` wired via `For(ModelConfig)` |
| #555 | `containers_of` in `converse_walk`; GenCorpus retired from converse |
| #595 | span hash index O(1) in `grammar_compose.cpp` |

### Demoted off `priority:high` (still real; not finished-v1)
121, 164, 165, 259, 264, 272, 273, 365, 368, 374, 379, 488, 512, 515, 521, 525, 547, 548, 543, 545, 479, 481, 537

### KEEP-HIGH / export / sell / crash / ops
#8, #111, #112, #114, #489, #596, #588 (`ops-blocked`)

### COMPLETION-AXIS (sync only)
#751–#761, #764, #765, #777, #785, #574 — owned by `docs/plan/` + CHECKPOINT

### DECISION-OWED (all 12 design-decision still open after #555 close)
#399, #436, #451, #464, #467, #472, #491, #504, #523, #529, #535 (+ #555 closed)

`priority:high` after Phase C: **15** (Phase E trims to ≤12).

## 10. Phase D campaigns

| Campaign | Umbrella | Notes |
|---|---|---|
| Ops / proveability | **#777** (+ **#792** empty/thin substrate alarm) | Checklist comment on #777; #588 re-measure after seed |
| Completion axis | `docs/plan/` + CHECKPOINT | Out of scope except hygiene |
| Monetization / sell | **#791** | Children: Stripe self-provision, API keys, tenant, metering; links #489/#550/#436/#531 |
| Model-lane honesty | **#793** | Checklist #479/#481/#488/#537/#543/#545 (+ #368/#374); #540/#541 closed |
| Foundry external proof | **#8** | Checklist #111/#112/#114 (+ #272/#273 demoted) |
| tracker-migration remainder | 11 survivors rebound | #399/#401/#403/#409/#412/#417/#418/#429/#430/#431/#433 — comments cite binding docs; label kept as provenance |
| Perf / tech-debt | `triage:deferred` cohort | Profile-gated / law items stay; rest deferred |

## 11. Phase E rebalance

### `priority:high` = 12 (enforced)

| # | Role |
|---|---|
| #777 | Ops Phase 0 — foundation seed / prove #776 |
| #792 | Ops — empty/thin substrate fail-loud alarm |
| #751 | Converse — wire steered lane (W1) |
| #754 | Converse — document lane Pillar 0 (W2) |
| #755 | Converse — eval harness (W5) |
| #758 | Converse — ISA gates (W6) |
| #765 | Converse — self-ingest call graph (W3) |
| #8 | Export — GGUF in llama.cpp milestone |
| #111 | Export — vendor llama.cpp |
| #489 | Sell — unauthenticated `/chess/*` |
| #791 | Sell — monetization epic |
| #596 | Crash — document malformed-encoding kills run |

Demoted in Phase E (still open, campaign-tracked): #112, #114, #588, #752, #764, #785.

### Stop-loss rule for new issues

Every new issue must state in the body:

1. **Pillar:** `converse` | `export` | `sell` | `coverage`
2. **Blocks finished-v1?** yes/no
3. If yes → justify displacing one of the 12 highs (or wait until a high closes)
4. Apply exactly one `triage:*` label at open time

`docs/plan/` remains the authority for the ~15 completion-axis issues only; this ledger + GH `triage:*` own the rest of the backlog.

### INDEX

`docs/INDEX.md` updated: doc 37 marked historical/drift-baseline; this file listed as current backlog ledger; ADR 0001 marked ACCEPTED.

## Appendix A — stale-framing cohort (25)

| #375 | Uncracked-List J (spike) — Does consensus x geometry x trajectory rout | `area:extension, type:enhancement, priority:normal, spike, module:laplace_substrate` |
| #396 | foundry: feature-extraction scope beyond the canonical_coord S3 extrac | `foundry, tracker-migration` |
| #398 | foundry: MoE experts / attention-bias tensors synthesize all-zero | `foundry, tracker-migration` |
| #401 | read-side: generic native KNN driver (candidate generation + ranking i | `read-side, perf, tracker-migration` |
| #402 | ingest: tier-0 entities vs physicalities ~900-row count mismatch after | `ingest, tracker-migration` |
| #403 | walk_branches: profile the dominant cost at scale (SPI replan vs qsort | `read-side, perf, tracker-migration` |
| #404 | OMW grammar_compose_probe rc=-2 transient — instrumented, wait for rep | `ingest, tracker-migration` |
| #407 | read-side fragmentation: no single gateway; canonical entry points unb | `read-side, tracker-migration` |
| #409 | walk_text generation lane times out at scale — GenCorpus perfcache blo | `read-side, perf, tracker-migration` |
| #412 | evidence/statistics endpoints: extension-side residuals (multi_source_ | `read-side, perf, tracker-migration` |
| #413 | highway-bit layout reseed owed: stored masks predate current bit assig | `substrate-law, ingest, model-lane, tracker-migration` |
| #414 | EstimateMatchupUnits undercounts structure-mode rows (CONTAINS/PRECEDE | `model-lane, tracker-migration` |
| #415 | norm salience dominated by rare-token representation degeneration — wh | `model-lane, tracker-migration` |
| #417 | refold-on-reingest: novelty gate must reach the consensus fold | `ingest, perf, tracker-migration` |
| #418 | tree-sitter format router on IngestInput: route .md/.rst/.html/etc thr | `ingest, tracker-migration` |
| #419 | full UD reseed (~686 files) after the refold fix | `ingest, tracker-migration` |
| #421 | chess-scale ingest levers: pipelined apply/compose, EmitNodes hot-cach | `ingest, perf, tracker-migration` |
| #422 | Justfile fails to parse at HEAD | `ci, tracker-migration` |
| #423 | build-system-deps: safe.directory UNKNOWN-fingerprint bug | `ci, tracker-migration` |
| #424 | waste-audit deferred leftovers (doc 31 Tier-2/3): Windows gating, reca | `perf, ci, tracker-migration` |
| #429 | client-dedup + ON CONFLICT offload — drop the DB existence probe | `ingest, perf, tracker-migration` |
| #430 | producer/queue/consumer file pool completion (bounded queue, one conti | `ingest, perf, tracker-migration` |
| #431 | physicality GIST COPY 4x slow — cycle/defer geometry index during COPY | `ingest, perf, tracker-migration` |
| #432 | ingest telemetry + per-file provenance pillars (0 and 4) | `ingest, tracker-migration` |
| #433 | verified at-scale runs: UD, Tatoeba, chess-ANALYZE profile | `ingest, tracker-migration` |

## Appendix B — decision cohort

| #399 | highway_mask grain: relation-TYPE bits vs value-grained (reseed-class  | `substrate-law, design-decision, tracker-migration` |
| #436 | governance: adversarial analysis of prompt-injection-as-attestation be | `substrate-law, read-side, design-decision` |
| #451 | substrate: witness-trajectory evidence virtualization (O(witnesses) ro | `module:laplace_substrate, substrate-law, design-decision` |
| #464 | read-side: edge_strength(subject,type,object) + batch form; bless labe | `read-side, design-decision` |
| #467 | engine: decide the fate of karcher_mean / log_s3 / exp_s3 — test-only  | `area:engine, design-decision` |
| #472 | model-lane: rank-aware OV factor storage (v_h + per-head O-basis) befo | `design-decision, model-lane` |
| #491 | chess: unify Zobrist and PositionContent.Surface position identity (C1 | `area:app, design-decision` |
| #504 | ingest: decide the git-lane relation ledger (reuse vs new) BEFORE code | `ingest, design-decision` |
| #523 | substrate-law: XPOS->UPOS is recorded as IS_A, but doc 18 calls that a | `substrate-law, design-decision` |
| #529 | read-side: enforce that highway_mask/perfcache are accelerators only — | `substrate-law, read-side, design-decision` |
| #535 | substrate-law: write the doc 08 amendment — derivable-evidence virtual | `area:docs, substrate-law, design-decision` |
| #555 | generation: corpus is containment (containers_of), not a pinned global | `priority:high, substrate-law, read-side, design-decision` |

## Appendix C — ops-blocked / likely-done / research

### ops-blocked
| #588 | write path: 5 substrate perf/consolidation gates RED on main against t | `type:bug, priority:high, perf` |
| #777 | ops: foundation seed blocked — orphan Unicode journal + prove #776 app | `` |

### likely-done
| #760 | ops: source_roster — code excludes relation bootstrap; live ChessPgn/C | `area:extension, type:bug, read-side` |

### research
| #220 | ADR — Format-writer emission matrix (tracking) | `priority:normal, spike, module:engine_synthesis` |
| #227 | ADR — Model-specific special-token vocabulary (chat-template, FIM, too | `area:app, priority:normal, spike` |
| #229 | ADR — Determinism verification for ingest (tracking) | `area:ci, priority:normal, spike` |
| #230 | ADR — Prompt decomposition sharpened (tracking) | `area:app, priority:normal, spike` |
| #378 | Verify: UD per-token MISC Lang= HAS_LANGUAGE emission — intentional co | `area:app, type:bug, priority:low, spike` |
| #380 | doc 18 Q1-Q5 — Open design questions (WSD operator mechanics, RoPE/rol | `area:extension, type:enhancement, priority:normal, spike, module:laplace_substrate` |
| #475 | model-lane: Tier-2 behavioral-fidelity gates against a llama.cpp refer | `spike, model-lane` |
| #510 | spike: formalize attention-as-SELECT (RASP) in SQL terms | `area:docs, priority:low, spike` |
| #519 | foundry research: make the correction planes head-informative (apply t | `spike, foundry` |
| #536 | model-lane: B-prime firefly-lens re-measurement owed at real logit sca | `priority:low, spike, model-lane` |


## Session progress 2026-08-03 (agent — coding lane during ChessPgn seed)

- Closed #819 (PR #824 already on main).
- `priority:high` rebalanced to **12** (demoted #818/#820/#821/#811/#804/#809 off high with comments).
- PR #826 — #596 document malformed-encoding skip (extract path).
- PR #827 — #812 OpenAI `POST /v1/op` + shared `InstalledOpInvoker`.
- PR #828 — #755 W5 election-first eval harness (advisory CI).
- ChessPgn journal `running` — do not bounce PG / foundation seed (#777) until quiet.
- #760 still open (live ChessOpenings recheck owed).
- Root tree has uncommitted MCP observability WIP for #809 — leave alone / separate PR.

PRs opened this session:
- https://github.com/SaltyPatron/Laplace/pull/826 (#596)
- https://github.com/SaltyPatron/Laplace/pull/827 (#812)
- https://github.com/SaltyPatron/Laplace/pull/828 (#755)
- https://github.com/SaltyPatron/Laplace/pull/829 (#758 G4)
- PR for #489 chess /chess/* key-mode auth.

PRs opened 2026-08-03 coding lane (ChessPgn seed in flight — stage=test only, no PG bounce):
- #826 https://github.com/SaltyPatron/Laplace/pull/826 — #596 document skip
- #827 https://github.com/SaltyPatron/Laplace/pull/827 — #812 OpenAI /v1/op
- #828 https://github.com/SaltyPatron/Laplace/pull/828 — #755 W5 eval harness
- #829 https://github.com/SaltyPatron/Laplace/pull/829 — #758 G4 scaffolding
- #830 https://github.com/SaltyPatron/Laplace/pull/830 — #489 chess auth
Closed: #819 (G3 fix already on main via #824).
priority:high held at 12.
- #751: converse_compose measured; seed-diversity FAILED (identical dog@7 vs @991); not wired.
- PR #831 https://github.com/SaltyPatron/Laplace/pull/831 — #792 fail-loud substrate floor
  (`check-substrate-floor.sh`; publish no soft-skip; ensure-foundation layer probe fixes).
- CI for #826–#831 `stage=test` queued behind Seed — chess run 30800642257 (ChessPgn ~0.35%).
- #751 still blocked on converse_compose seed diversity (dog@7 == dog@991).
- PR #832 https://github.com/SaltyPatron/Laplace/pull/832 — #765 SQL tags.scm DEFINES/CALLS (unit green; live ingest prove after ChessPgn).
- GH issues kept current: #751 seed-diversity diagnosis, #777/#792 chess-blocked notes, PR CI-queue notes on #826–#831.

### Session 2026-08-03 (chess forward-pass / Gemini filter — no code motion)

- Wrote `.scratchpad/44_Chess_Forward_Pass_And_Industry_Filter_2026-08-03.md`; indexed in `docs/INDEX.md`.
- Updated `docs/guides/chess.md` (UCI = truncated pass; #833 in known gaps).
- Opened **#833** (Chess Forward Pass composition / STEER) under #818 — justified by framing consolidation, not scope creep.
- Rewrote #818 body + comments on #823/#605/#447/#575; #823 body tightened (validity≠completeness, scratchpad 44 cite).
- Cutechess remeasure vs SF14.1 Elo2000 running → `/tmp/laplace-vs-sf2000` (mid-run not stamped).
- **#834** opened: drive/measure protocol gap — naive cutechess defaults are not using Laplace; live explore shows Na3 over e4 on raw μ (#447); fold UCI still played e4 @ movetime 2s; chess-lab.md primary protocol section added.


## Drift note — 2026-08-05 (append)

Re-measured: **232** open issues (was 195 at end of this pass). `priority:high` = 9. Stop-loss failed; see `docs/plan/CHECKPOINT_2026-08-05.md`.
