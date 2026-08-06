# Checkpoint — 2026-08-02 (completion-axis session)

This is the **resume document** for the conversational/inference completion
axis after an agent session stopped on model API limits. It records what
landed on `main`, what is still false in older prose, the live-box ops
block, and the next ordered work. It does **not** replace
`docs/COMPLETION_PLAN.md` or the W*.md specs; it dates the stage.

**HEAD at write time:** `501f260` (`Merge #776`) · **main CI:** green —
https://github.com/SaltyPatron/Laplace/actions/runs/30731493955

---

## 1. What landed on `main` this session

| PR | Workstream / gate | Fact |
|---|---|---|
| [#771](https://github.com/SaltyPatron/Laplace/pull/771) | W6 elector | `ElectorArchitectureGateTests` — five `prompt_coherence` callers, one key order; prose no longer owns the site count |
| [#772](https://github.com/SaltyPatron/Laplace/pull/772) | W6 G1/G3/G8 | `scripts/isa-gate-check.py` + `isa-gate-baseline.json` — shrink-only ratchets in the policy job |
| [#773](https://github.com/SaltyPatron/Laplace/pull/773) | W12 / #760 | `source_roster` excludes relation-type subjects via `relation_canonical(subject) IS NULL`; pg_regress fixture |
| [#774](https://github.com/SaltyPatron/Laplace/pull/774) | W3 / #765 half | `CodeDecomposer.ModalityOf` strips `.in` and re-resolves (`.sql.in` → SQL grammar) |
| [#775](https://github.com/SaltyPatron/Laplace/pull/775) | W6 G7 | `IngestRosterParityTests` — C# dispatch ↔ witness-manifest + operational allowlist |
| [#776](https://github.com/SaltyPatron/Laplace/pull/776) | ingest correctness | Shared-writer entity identity dedup across staged intents + `23505` no longer treated as concurrency-transient |

Process note that binds future agents: **prefer local verify → merge → one
`main` CI run.** Do not burn a redundant `workflow_dispatch` on the feature
branch when merge-to-main is the validation that matters.

---

## 2. Live box — blocked for seed verification

**2026-08-03 update:** foundation `HasLayerCompleted` markers are all present on
the standing `laplace` DB; a long `ChessPgn` seed is `status=running` (do not
bounce PG / do not start a second ingest). #792 fail-loud floor
(`scripts/check-substrate-floor.sh`) names `INGEST_JOURNAL_NONTERMINAL` /
`THIN_SUBSTRATE` / `UNSEEDED_SUBSTRATE` — publish no longer soft-exits on empty
and skips smoke. Heal remains seed-foundation dispatch; no auto-reseed.

Historical note (checkpoint write time): after the cancelled foundation seed the
box had been thin residue + orphan Unicode `running` — that class of state is
exactly what #792 must fail loud on. #776 (shared-writer identity dedup) is
CI-green on main and still owed a clean foundation-ladder prove when the box
is quiet (#777).

### Ops sequence before any new foundation seed

1. Confirm no live ingest: `ingest_run_journal status='running'` / no `Laplace.Cli` you did not start.
2. Mark a true orphan journal row terminal (`failed` or `cancelled`) — do not leave `status=running` forever. Schema allows: `running|ok|failed|empty-noop|capped|cancelled|skipped-complete`.
3. Controlled reset **only if the operator orders `db-reset` / DROP** — agents must not invent that.
4. Re-run **one** foundation seed when owed: `gh workflow run seed-foundation.yml --ref main`
5. Re-check #760 live on ChessPgn / ChessOpenings after those sources exist.
6. Re-check #765 live `input_units > 0` on `ingest code` over converse SQL
   after the substrate can accept that ingest (still blocked on §3 W3 structural).

---

## 3. Phase map — honest state

Scope reminder: this plan is **~15 issues** on the conversation axis, not
the full ~214-issue backlog (`docs/plan/README.md`).

| Phase | Intent | State at this checkpoint |
|---|---|---|
| **0** | main / live / record agree | Code on `main` + CI green. Live seed **not** agreed — orphan journal + thin residue |
| **1** | Gates + measurement | **Partial** — see §4 |
| **2** | W1 speak | Not started |
| **3** | W4 senses | Not started (priors first per W4 §0 measurement) |
| **4** | W2 documents | Not started |
| **5** | W7–W9 | Not started |
| **6** | ISA consolidation | Not started (G1/G3/G7/G8 ratchets exist; opcodes unfinished) |
| **7** | Scale / seed waves | Blocked on Phase 0 live health + finished lanes |

---

## 4. Phase 1 remaining (resume here after ops §2)

Ordered for the next agent. Do not shrink this list to “whatever is
convenient.”

1. **Ops unblock (#777)** — §2 above. Without a real foundation, W5 and most
   acceptance criteria are opinion.
2. **W5 / #755** — generation-quality harness (`prompts_smoke.txt` runner +
   seeded fixture). Until this exists, quality claims remain ungated.
   **Progress 2026-08-05:** `generation_probe(prompt, seeds[], steps, lang)`
   installed (both lanes, one row per lane×seed; regress shape-pin) +
   `laplace eval generation` CLI verb through the shared read surface. First
   live measurement recorded on #751: converse_compose REPLAYS (byte-identical
   across seeds 7/991/12345 on 'dog') — fails its own wiring gate; converse_walk
   varies with seed but emits cross-language splice (no p_lang). steered_walk.c
   proven seed-alive (LCG feeds start pick + per-step score), so compose's
   replay is an input-array property: starts_n==1 and/or weights peaked past
   the 1e5 hash range. Wiring the ratchet forward: compose's revival as a
   called canonical shrank the G4 baseline (72→71).
   **Phase 2 item 2 re-evaluation (2026-08-05, GH #878):** converse_tiered
   hangs are fixed (ceef97d; dog 3.7s / pawn 1.4s) but content regressed to
   pure topic echo on every probe including pawn, and it runs CREATE TABLE
   internally (unrunnable read-only, 25006 via MCP op). Verdict: do NOT
   wire; stale chat.sql.in:429-436 hotfix comment replaced with measured
   state.
   **Evening sweep 2026-08-05:** stale-issue closures on in-tree evidence —
   #841/#842/#845/#846 (fixed by d18527b9, verified), #863 (sql tool
   operator-lane-gated + op() live catalog + new McpSqlLaneGateTests pinning
   it). #814 hardened: covered-set refusal (journal/attestations/consensus/
   entities/physicalities → typed ops named) + full-cost gap log; criterion 3
   (op-queryable gap log) recorded on-issue as a two-path design decision.
   #878 filed: converse_tiered echo regression + CREATE TABLE read-only
   violation, chat.sql.in stale comment replaced with measured state.
   **#765 live prove DONE (2026-08-05 23:00):** `ingest code` over
   extension/laplace_substrate/sql via MCP ingest — 428/428 units ok, 47s,
   19,255e+19,228p+1,114a novel. CALLS = 774 evidence rows for
   CodeDecomposer; HAS_DEFINITION per function (body→name). **G4's
   destination read (zero incoming CALLS) is now queryable against real
   data.** Open discrepancy recorded on-issue: DEFINES evidence = 0 (the
   @definition.function tag path; span-lookup suspect), REFERENCES
   unverified. Side effects: HAS_MOTIF partition pressure (19.1%) →
   hot=true + codegen on PR #879; #809 closed on in-tree evidence; CI
   cross-run workspace wipe race diagnosed (two cd-failures vs solo green)
   → whole-run queueing concurrency, PR #879, Copilot review addressed
   (no cancel — deploy safety; ref-independent group). #660 blocked on
   #805 source-kind decision; #847 shellcheck gate not built.
   **Late 2026-08-05, all CI-green on main:** PR #879 merged (whole-run
   queueing + HAS_MOTIF hot=true/codegen); PR #881 merged (ctest tripwire
   naming the missing-build/ failure). #880 carries the measured timeline;
   the ORIGINAL build/-deleter on the runner is unidentified — the tripwire
   names any recurrence.
   **#765 CLOSED (late 2026-08-05):** the "DEFINES=0 gap" was a probe error —
   DEFINES aliases HAS_DEFINITION; via relation_type_resolve the canonical
   counts 340 DEFINES + 774 CALLS for CodeDecomposer. W3 extraction fully
   working; REFERENCES zero by tags coverage (no @reference.type pattern
   yet), not defect.
   **W5 measurement half LANDED (2026-08-06, PR #882 merged):**
   scripts/verify-generation.py — R6 detectors over generation_probe +
   prompts_smoke, Unicode tokenizer (the ASCII draft scored Cyrillic replies
   as EMPTY and briefly bought a false interference theory — documented at
   the regex). First full numbers: compose REPLAYS on all topic probes and
   3/5 sentences but VARIES on 'The opposite of hot is' and 'Once upon a
   time' — replay is PROMPT-SHAPE-DEPENDENT (#751 hypothesis narrowed);
   walk varies everywhere, content 0–0.05 vs English expected sets (R1
   p_lang gap quantified).
   **#755 CI wiring LANDED (PR #883, 2026-08-06):** lane detectors run as an
   independent advisory step in the eval job on every deploy — proven live
   (the merge's own deploy printed the full table). Threshold flip via
   eval-baselines remains.
   **#751 diagnosis CONFIRMED (minimal pair):** 'hot water' varies across
   seeds with English cross-witness recombination; single-anchor prompts
   collapse compose's starts to the one max-cnt core gid → deterministic
   replay. Fix target named on-issue: widen the starts/core derivation for
   single-anchor prompts; acceptance = W5 variance detector flips 1/3→3/3
   on dog/water/king, printed by every deploy.
   **REPLAY FIXED (PR #884 merged, 2026-08-06):** two layers — starts
   widened to every kept gid (SQL) AND the root cause in steered_walk.c:
   the core-backbone branch seeded from core_elems[0],[1] unconditionally,
   rng never ran; now one LCG advance picks the entry offset (uint64
   modulus per review). Diagnosis detour on record: a starts-only fix
   passed local reasoning and failed acceptance — the CTE diagnostic showed
   159 kept gids, which forced the C read that found the dead branch. The
   MERGE'S OWN DEPLOY printed the proof: dog 3/3, water 2/3 (offset
   collision), king 3/3, sentences 3/3, zero REPLAY flags.
   **PHASE 2 ITEM 2 LANDED (PR #885 merged, 2026-08-06):** chat(shape='walk')
   speaks through converse_compose — frontier-steered, session-seeded
   (session id + prompt hashed with max(ord) turn count), p_lang threaded,
   walk as no-material fallback, query_shapes honest for both consumers.
   Live proof through chat() itself: two sessions, same prompt, two
   different coherent evidence-anchored replies. Honest limitation on
   record: p_lang threads but cannot bite — streams are word-level; a
   Bulgarian surface word has no English member to realize into. The
   concept-level stream is the remaining R1 work (#751).
   **RULINGS SESSION (2026-08-06, PR #886 merged):** three operator rulings
   now law-level in INVENTION.md — (1) unattested text is prompt-grade by
   design (#808 CLOSED, no re-attribution); (2) file metadata is a Merkle
   branch of the trunk (recorded on #806 for the parallel media-ladder
   campaign — see ~/.cursor/plans 'Media modality ladders': Cursor/Grok
   building image/audio/video infra, engine/core + new Substrate spines,
   no file-space collision with this lane); (3) unlearning is adjudication,
   eviction demoted to compliance hatch. Ops: runner-bounce.sh (four guards,
   no force; first live run correctly REFUSED on active backends) + sudoers
   via setup-host Layer 0c (SUDO_USER, visudo-validated) — operator runs
   setup-host once to install. Eval FLIPPED TO BLOCKING on replay+echo only
   (operator ruling: thresholds are the agent's call); content/flatness
   advisory while R1 moves them.
   Next session's ordered picks: (1) concept-level stream so p_lang bites
   (#751 — the last R1 blocker); (2) #814 criterion 3 once the operator
   picks path (a) ops-table or (b) gaps-as-testimony; (3) G4 destination
   read as an installed op; (4) Phase 2 items 3–5 (kappa/covered, pinned-
   seed generation regress). Big seeds (UD/ConceptNet/Tatoeba/Wiktionary/
   OpenSubtitles) parked pending decomposer rework — on disk, not blocking.
3. **W6 remainder / #758** — still open:
   - G4 dead-canonical (**destination** = substrate `CALLS` in-degree after
     W3; **scaffolding** = labeled grep + shrink-only allowlist may ship first)
   - G2 render-before-select ratchet
   - G5 / G6 complete / G9 / G10 blocked or partial per W6 §3
4. **W3 / #765 remainder** — `.sql.in` discovery is done; SQL still emits
   **zero** `DEFINES` / `CALLS` / `REFERENCES`. That is the real W3 body.
5. **W10 / #764** — `BEGIN ATOMIC` complement; do not confuse with W3.
6. **#760 close criteria** — code filter merged; **close only after** live
   ChessPgn / ChessOpenings rosters show source-distinct content.
   **CLOSED 2026-08-05:** positive control proven on both sources (relation-law
   HAS_NAME_ALIAS rows with relation-type subjects present), filter verified at
   its semantics, recheck made repeatable via new `source_bootstrap_present()`
   op with regress coverage. Ops §2.2 executed the same day: orphan ChessPgn
   run 183d8507 closed via new `ingest_run_close()` op (both ops: commit
   92333549). ChessPgn remains a ~2.3% partial seed — reseed NOT started, on
   operator's explicit hold.

Architectural debt called out during the seed failure (do not pretend #776
finished it): **Unicode still uses a hand `DecomposerMultiPhase` /
`IngestRunner` multi-change apply path**, not the `IngestBatchPipeline`
O(tier) working-set spine. Apply-side identity dedup is shared-writer
correctness; migrating Unicode onto the spine is a separate Rule #8 item.

---

## 5. Issue hygiene at this checkpoint

| Issue | Title debt | Status truth |
|---|---|---|
| [#758](https://github.com/SaltyPatron/Laplace/issues/758) | Retitled (was “four-elector”) | Elector + G1/G3/G7/G8 landed; G4/G2/G5/G6/G9/G10 remain |
| [#760](https://github.com/SaltyPatron/Laplace/issues/760) | Retitled; body was pre-fix | Filter in `source_roster.sql.in`; live recheck owed |
| [#765](https://github.com/SaltyPatron/Laplace/issues/765) | Retitled to split defects | Discovery half done (#774); structural edges still zero |
| [#777](https://github.com/SaltyPatron/Laplace/issues/777) | **Opened this checkpoint** | Orphan Unicode journal + prove #776 on live foundation ladder |
| [#755](https://github.com/SaltyPatron/Laplace/issues/755) | — | Untouched; next measurement workstream after #777 |
| [#764](https://github.com/SaltyPatron/Laplace/issues/764) | — | Unblocked in principle after W3 graph exists; not started |

---

## 6. How the next session should start

```text
cd /home/ahart/Projects/Laplace
git fetch && git status -sb   # root must be clean main
# Read: CLAUDE.md → this CHECKPOINT → COMPLETION_PLAN §0 → plan/README → W* for the slice
# Ops first if journal still shows UnicodeDecomposer running
bash scripts/agent-worktree.sh <name>
```

Pick **one** slice from §4 after ops. Prefer W5 or W6-G4 scaffolding if the
box is still unseeded; prefer #760 live close + W3 structural extract once
foundation is healthy.

Report misses before hits. Do not declare Phase 1 done from this checkpoint.
