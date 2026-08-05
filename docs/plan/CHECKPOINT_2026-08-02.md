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
