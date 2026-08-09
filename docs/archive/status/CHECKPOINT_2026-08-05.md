> Archived status snapshot. Re-measure the running system and consult GitHub issues.

# Checkpoint — 2026-08-05 (forest re-measure)

Measured this host and GH. Supersedes
[CHECKPOINT_2026-08-02.md](CHECKPOINT_2026-08-02.md) for **status**; that file
remains the historical record of what #771–#776 landed.

**Measured at:** 2026-08-05 · **origin/main tip seen:** `6fb36e78`
(Merge #873; later merges #874–#877 also on remote during this pass) ·
**this checkout:** `main` behind `origin/main` by ≥10 commits · local junk:
deleted `explore-entity-200.actual.json` (already untracked on origin via #869).

---

## 1. Live box (this host) — empty, not “orphan Unicode”

| Probe | Result |
|---|---|
| `substrate_health()` | `ok=t`, `bootstrap_entities=41`, `deep_checked=f` |
| `entities` / `attestations` | **41** / **1** |
| `ingest_run_journal` | **0 rows** (no orphan `running`) |
| `source_counts_approx()` | **0 rows** |
| `scripts/check-substrate-floor.sh` | **THIN_SUBSTRATE** — all 10 foundation `HasLayerCompleted` markers missing |
| `scripts/eval-generation.py` | **exit 2** — `unseeded / empty substrate` (correct refuse) |

The 2026-08-02/03 checkpoint text that said foundation markers were present and
a long `ChessPgn` seed was `running` is **false on this host**. Do not plan from
that prose. Heal path is unchanged: `bash scripts/ensure-foundation.sh`
(or `gh workflow run seed-foundation.yml --ref main`). No auto-reseed from the
floor script. No second ingest while one is live.

#777 retitled 2026-08-05: empty-box / THIN_SUBSTRATE on this host. Prior
wording (“orphan Unicode journal”) described a different machine state.

**Ops action this session:** cleared two agent-created `UnicodeDecomposer`
`running` orphans (diagnostic `timeout 60` + a `nohup` that died with the
agent shell — do not repeat either). Durable restart via `setsid`:
`ensure-foundation.sh` PID **2931832** (ppid 1) → log
`build-logs/ensure-foundation-2026-08-05.log`. Do **not** start a second
ingest while that process is live. Watch with
`ingest_runs()` / `check-substrate-floor.sh`.

---

## 2. Tracker drift (why the forest keeps rotting)

| Claim (authority surface) | Measured 2026-08-05 |
|---|---|
| Ledger 42 end: **195** open, `priority:high` ≤12 | **232** open; **9** `priority:high` |
| `docs/plan/README.md`: “214 open” | stale count |
| W5 / #755 “no runner” / “next ungated” | **false** — `scripts/eval-generation.py` + `eval-probes.json` + `eval-baselines.json` exist; wired in `laplace.yml`; issue body still describes 2026-08-02 |
| W6 / #758 “G4/G2/G5/G6/G9/G10 remain” | **#758 CLOSED** 2026-08-03 (PR #829). #876 landed G2/G5/G10 grandfathered. G4 baseline key still populated; **destination** remains W3/`CALLS` in-degree (#765) |
| INDEX lists 44 chess filter; omits 41/43/44-G4 and several 38–40 logs | **INDEX incomplete** vs `.scratchpad/` on disk |
| Recent merged PRs (#853–#877) | Mostly geometry/read-path/gate microfixes — high visibility, low magnitude vs Phase 0–1 |

Stop-loss from ledger 42 failed: new issues keep landing unlabeled while agents
ship tree PRs. Open-issue count went **195 → 232** in three days.

---

## 3. finished-v1 lens (re-ranked against code + live)

1. **Substrate has content** — **RED here.** Zero foundation layers. Every
   converse/election/export claim on this box is opinion until seed completes.
2. **Measurement exists** — W5 runner **partially landed**; issue/docs not
   closed out. Acceptance (CI red on quality regress, baseline fingerprint)
   still needs a seeded box to mean anything.
3. **Speak** — #751 still open; blocked on honest measurement + seed.
4. **Export / sell** — #8/#111/#791 still open; not the bottleneck while the
   box is empty.
5. **Self-model graph** — #765 still true: sources **declare**
   `CALLS`/`DEFINES`/`REFERENCES`; `CodeDecomposer.cs` has **zero** emission
   sites for those names (grep). Discovery half (#774) ≠ structural body.

---

## 4. Ranked next work (next-task criteria)

Judge: Rule #8 spine throughput, Rule #6 one-implementation, Mold-A-Model
consensus supply. Easy-win gate/geometry PRs lose unless they unblock these.

### 1) Foundation seed on this host (#777 / Phase 0)

- **Evidence:** floor script + entity census above.
- **Dependency:** nothing conversational or eval-scored is real until this
  completes. One ingest at a time; log to a file; do not bounce PG mid-run.
- **First step:** `bash scripts/ensure-foundation.sh` with
  `PGHOST`/`PGUSER`/`LAPLACE_DBNAME` matching the live socket
  (`laplace_admin` works here). Or dispatch `seed-foundation.yml` on `main`.
- **Verify:** `bash scripts/check-substrate-floor.sh` exits 0;
  `source_counts_approx()` non-empty; `eval-generation.py` no longer exit 2.

### 2) W3 structural emission (#765)

- **Evidence:** `CodeSource.cs` / `RepoSource.cs` declare the relations;
  `CodeDecomposer.cs` does not emit them; W3 acceptance still unpaid.
- **Dependency:** G4’s real destination (substrate `CALLS` in-degree) and
  any “the substrate reads itself” claim.
- **First step:** implement SQL `CREATE FUNCTION` → `DEFINES` (node set), then
  `CALLS` edges; declare whatever is missing in `InitializeAsync`; prove with
  `ingest code` + non-zero relation counts — not a discovery-only PR.
- **Verify:** journal `input_units > 0` and attestation counts for
  `CALLS`/`DEFINES` on the code source after ingest.

### 3) Close the W5 paper cut, then speak (#755 remainder → #751)

- **Evidence:** runner exits 2 correctly on empty DB; issue #755 body and
  plan README still describe a world without a runner; #876/#829 moved W6.
- **Dependency:** seeded box for exit-0 path and CI meaning; then W1/#751
  is the user-visible speak wire.
- **First step after seed:** retitle/close or carve #755 to remaining
  acceptance (HTTP surface, fingerprint baselines, push-gates-quality);
  update `docs/plan/README.md` W5/W6 rows to match GH; then execute W1.
- **Verify:** seeded `eval-generation.py` exit 0 against recorded baseline;
  chat path calls steered walk (W1 acceptance).

---

## 5. Explicit non-work (trees agents keep choosing)

Do **not** schedule these ahead of §4 unless they are the named slice:

- Further Class-A `SET search_path` drip (#860) while the DB has 41 entities
- One-off G4 function deletes without W3 `CALLS` graph (#872-class)
- Chess compose micro-opts (#839) while foundation is unseeded here
- New unlabeled issues that restate plan rows already open

---

## 6. Doc/issue hygiene owed in the same breath as code

| Surface | Action |
|---|---|
| This file | **status authority** for resume until the next dated checkpoint |
| `docs/plan/README.md` | point Resume at this file; fix W5/W6/#777 rows |
| `docs/INDEX.md` | add this checkpoint; index missing scratchpads 41/43/44-G4 |
| #755 | body/title must stop claiming there is no runner |
| #777 | note empty-box symptom on hart-server (or whatever this host is) |
| Ledger 42 | append-only drift note: 195→232; do not treat end-of-pass counts as current |

Report misses before hits. Do not declare Phase 1 done from this checkpoint.
