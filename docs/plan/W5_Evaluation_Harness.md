# W5 — Evaluation harness

**Issue:** #755 · **Plan:** `COMPLETION_PLAN.md` R6 / Phase 1 · **Blocks:** every
"is it better" verdict, and the OP9 caller migration (W-ISA) which touches the
hot path with no quality signal today

---

## 1. Why this exists

Nothing in this repository fails when the conversation surface emits garbage.
`converse_walk`, `steered_walk_raw`, `steer_candidates`, `converse_compose`,
`converse_tiered` and `walk_branches(p_topic_bias)` have **zero** pg_regress
coverage. `scripts/prompts_smoke.txt` holds five probe prompts and has **no
runner** — its only references in the entire tree are two lines in the
completion plan noting that it has no runner. `EvalCommands.cs` measures ingest
fidelity only and **always returns 0**.

So every claim about quality — including the ones in this session's own commits
— rests on hand-run probes and judgment. That is the condition this whole plan
exists to end.

There is one working quality gate in the tree, and **it runs nowhere**:
`scripts/verify-model-behavioral.py` is invoked only by
`scripts/win/verify-model.cmd:34`; zero GitHub workflows reference it.

## 2. What already exists — and is worth copying rather than reinventing

### 2.1 `verify-model-behavioral.py` — the model to port

Its thesis (`:3-11`) is the correct one for this repo:

> *The project's defining failure mode has been SIMULATED success… This harness
> gates on CONTENT: the expected continuations for each probe word are pulled
> from the substrate's own consensus — the same evidence the model was
> synthesized from.*

Mechanics worth taking verbatim:

- **Expectations are substrate-derived** (`:70-88`): pulled from
  `v_consensus_unrefuted` for the probe id, ranked by conservative estimate. A
  gate whose expectations come from the substrate cannot be satisfied by
  something that does not carry the knowledge.
- **Deterministic decode with loop suppression** (`:91-118`): greedy +
  repeat-penalty, *"the decode any real consumer would use."* Output extraction
  stops at diagnostic lines so tooling noise never enters scored text.
- **Glue-word exclusion** (`:38-44`, added 2026-07-08): determiners and
  prepositions *are* legitimately attested continuations, so counting them
  inflates the score. Content-word rate is the honest number.
- **Four detectors, each named for the fraud it catches** (`:264-280`):
  `load_or_generate_failed`, `empty_output_collapse`, `global_hub_collapse`
  (modal first token share > 0.5 — the frequency-hub failure), and
  `no_scorable_probes` (the vacuous-pass guard).
- **Exit contract**: 0 pass, 1 content-gate failure, **2 harness/setup error** —
  so an unseeded box can never be mistaken for a pass.

### 2.2 `ingest-baseline.py` + `ingest-baselines.json` — the threshold precedent

This is the repo's existing answer to "how are thresholds stored so a regression
fails," and the new harness should mirror it exactly:

- `record` / `check` / `show` subcommands; the JSON is **committed**.
- Tolerance is **wide on purpose** (`:34-37`): *"a flaky gate teaches people to
  ignore it. It is here to catch a 2x regression, not a 10% one."*
- **Refuses to pass with no baseline** (`:145-149`) — *"Refusing to pass a check
  with nothing to compare against."*
- **Two-axis comparison** (`:180-193`): timings get a ratio tolerance; row counts
  get **exact equality**, because ids are content hashes and a changed count
  means different content was ingested.

### 2.3 CI already has a seeded box and the idiom to use it

- `publish` emits a job output `seeded` derived from `/health/ready`
  (`laplace.yml:429-433,462-496`), distinguishing "deployed but unseeded" from
  ready.
- **`smoke` already gates on `needs.publish.outputs.seeded == 'true'`**
  (`:505-513`) and posts a real chat completion (`:526-531`) — **and discards
  the answer.** A seeded-substrate quality job already exists in skeleton.
- `integration-test` runs against *"the standing (already seeded) substrate"*
  (`:348-356`).
- The regress lane is **not** an option: it recreates the DB empty
  (`tests/CMakeLists.txt:16-19`).

## 3. How it should work

```
scripts/eval-generation.py     # runner        (model: verify-model-behavioral.py)
scripts/eval-probes.json       # probes + expectations, versioned, dev vs heldout
scripts/eval-baselines.json    # scores + thresholds, versioned  (model: ingest-baselines.json)
```

Retire `prompts_smoke.txt` into `eval-probes.json` — keeping a runner-less probe
file beside a runner is the exact drift spec 37 §0 names.

```
python3 scripts/eval-generation.py \
  --db "host=/var/run/postgresql user=laplace_admin dbname=laplace" \
  --api http://127.0.0.1:5187 \
  --probes scripts/eval-probes.json --baseline scripts/eval-baselines.json \
  --report .eval-proof/generation.json [--record]
```

Exit contract mirrors the existing harness exactly: **0 / 1 / 2**.

### Detectors

| Detector | Provenance |
|---|---|
| `on_topic_rate` | ported; tokens ∩ expectations derived from the **elected topic id**, not the raw prompt word |
| `content_word_rate` | ported; **import** the glue stoplist, do not retype it |
| `global_hub_collapse` | ported (modal first content word share > 0.5) |
| `empty_output_collapse` | ported |
| `prompt_echo` | **named in the existing harness and never implemented** — build it (Jaccard of generated vs prompt tokens) |
| `election_correctness` | **new, and the one that matters most here.** Assert `prompt_coherence`/`resolve_topic` rank-1 equals a hand-written expected id per probe. This is the detector that catches "What is a pawn in chess?" → *"A is the 1st letter of the Roman alphabet"* and "What is a glacier?" → the article |
| `latency_budget` | **new.** Per-surface p50/max. The outage class it guards is named in the tree: a 29.6 s band-mass scan and a >280 s election hang |

### Thresholds

Keyed by `<surface>/<probe-class>` in a committed JSON. Rates get a wide
absolute tolerance; **`election_correctness` gets exact equality, no tolerance**
— it is a correctness fact, the analogue of `ingest-baseline`'s row counts, not
a timing. Latency gets a ratio tolerance plus a hard ceiling. No baseline →
exit 1 with an explicit refusal. `--record` is a separate deliberate step, never
automatic on a gate run.

### CI wiring

A new `eval` job after `smoke`, guarded by
`needs.publish.outputs.seeded == 'true'` — the established idiom, and the reason
`smoke`'s own comment at `:506-509` exists: a job that must not silently skip
has to state its own requirement, because `success()` is evaluated over the
whole transitive `needs` graph. Add the three new files to the **CI presence
tripwire** (`:89-115`) so a hygiene sweep cannot delete the harness. Add a
`just eval` target beside `just e2e`.

## 4. What to consider

| # | Decision | Recommendation |
|---|---|---|
| D5 | HTTP surface vs SQL surface | **Both, as two probe surfaces in one JSON.** A divergence between them is itself a finding; the SQL lane can run in `integration-test` without a publish, the HTTP lane needs one. |
| D6 | python runner vs `laplace eval generation` CLI | **Python**, matching the two existing gate scripts (both shell `psql`, both write JSON reports, both are invoked directly by workflow steps). If a substrate read recurs inside it, **graduate that read to an installed surface** — plan standard #5. |
| D7 | blocking or advisory first | **`--record` → commit a known-good baseline → one week advisory (`continue-on-error`) → flip.** Record the flip date in the JSON. A gate that goes red on merge-day gets ignored. |
| D8 | expectations substrate-derived or hand-written | **Substrate-derived for content rates** (that is the whole insight — a gate the system can't cheat), **hand-written for `election_correctness`**, because election is precisely what the system currently gets wrong and cannot be trusted to grade. |

## 5. Where to look

| Concern | File |
|---|---|
| The harness to port | `scripts/verify-model-behavioral.py` (`:3-11` thesis, `:38-44` glue, `:70-88` expectations, `:91-118` decode, `:236-286` scoring/detectors/exit) |
| Threshold storage precedent | `scripts/ingest-baseline.py:20-23,34-37,145-149,180-193`, `scripts/ingest-baselines.json` |
| Seeded-job idiom | `.github/workflows/laplace.yml:429-433,462-496,505-531` |
| Live-data gate precedent | `scripts/decomposer-gate-check.py`, `.github/workflows/_ingest.yml:149-167` |
| Presence tripwire | `.github/workflows/laplace.yml:89-115` |
| Existing (non-)eval | `app/Laplace.Cli/EvalCommands.cs` (measurement, always exit 0), `BenchCommands.cs:66-68` (**stub — returns ok unconditionally, do not build on it**) |
| Regress DB is empty | `extension/laplace_substrate/tests/CMakeLists.txt:16-19` |

## 6. Acceptance

1. Runner on the seeded box exits 0 and writes a JSON report with per-probe
   records and named verdicts.
2. Reverting the election fix (`4c4106d`) makes it exit **1** naming
   `election_correctness`.
3. Reintroducing a known slow path makes it exit **1** naming `latency_budget`.
4. An empty/unseeded DB exits **2**, never 0.
5. No baseline → exit 1 with an explicit refusal message.
6. Held-out misses are reported **before** hits, per the plan's standard of
   evidence.
7. A push whose chat quality regresses cannot reach green.

## 7. Risks / ordering

1. **A quality gate on a shared, mutating substrate is inherently noisy.** The
   standing database is reseeded by operator dispatch, so scores move when data
   moves. Key baselines by a substrate fingerprint (`substrate_counts()` is
   already surfaced in the smoke job) and treat a fingerprint change as
   *re-record required*, not *regression*.
2. **The `seeded` guard is mandatory**, or the job goes red on a fresh box —
   the transitive-skip trap is documented twice in the workflow itself.
3. **Land `election_correctness` first.** It is the only detector that would
   have caught the pawn/glacier failures, and it is the cheapest.
4. **Wire `verify-model-behavioral.py` into a workflow while here** — a working
   gate that runs nowhere is free signal being discarded.
5. **Ordering constraint that binds another workstream:** the ISA's OP9 caller
   migration touches `chat.sql.in` and `converse_facts.sql.in` with no quality
   signal today. This harness must be at least advisory **before** that work
   begins.
6. **Election quality is capped by the tier-collision seam** (see W4). Record
   the cap in the baseline rather than silently lowering the threshold.
