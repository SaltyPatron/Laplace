# Agent onboarding — the starting prompt

Paste the block below into an agent session at the repository root. It is
written for **Cursor CLI (`cursor-agent`) on this Ubuntu host**, and it is
harness-agnostic apart from the shell commands, which are verified against this
box.

Invoke with the repo as the working directory:

```
cd /home/ahart/Projects/Laplace
cursor-agent
```

Then paste the prompt. For a one-shot non-interactive run instead:

```
cursor-agent -p "$(cat docs/plan/ONBOARDING.md | sed -n '/^```text$/,/^```$/p' | sed '1d;$d')"
```

**Maintenance:** this prompt is itself unverified until an agent has run against
it. When an agent goes wrong in a way the prompt should have prevented, fix the
prompt in the same PR as the work. It is short on purpose.

---

```text
You are working on Laplace, a content-addressed knowledge substrate:
a PostgreSQL 18 extension (C + SQL), a .NET ingest pipeline, and a C/C++
engine. Repo root: /home/ahart/Projects/Laplace on an Ubuntu server. You are
in a terminal, working agentically and iteratively: pick work, verify it,
ship it, repeat. Nobody is watching each step — do not ask permission for
reversible work that follows from the plan.

ENVIRONMENT
- Shell is zsh on Ubuntu. Build/test entry point is scripts/pipeline.sh
  (phases: build install sync-extension migrate test). The scripts/win/*.cmd
  files are a Windows toolchain -- not yours to run, but do not read them as
  noise: 84 entry points including regress.cmd run the SAME pg_regress suite
  as Linux, and five expected-output files pin LITERAL 128-bit content ids.
  Both platforms must produce byte-identical output or the build fails, so
  that suite is a continuous cross-platform proof that identity and the
  fixed-point fold are bit-reproducible. It is the strongest single piece of
  evidence in the repo.
- Query the substrate directly with psql (verified working on this host):
    psql -h /var/run/postgresql -U laplace_admin -d laplace -c "SET search_path=laplace,public; <SQL>"
- Introspect the installed SQL surface before assuming a helper is missing:
    psql -h /var/run/postgresql -U laplace_admin -d laplace -c "SET search_path=laplace,public; SELECT * FROM api('walk');"
- Redirect long-running output to a log file rather than streaming it.

READ IN THIS ORDER BEFORE TOUCHING ANYTHING
1. CLAUDE.md — operating rules. They override your defaults.
2. docs/ARCHITECTURE.md — the system, with file citations.
3. docs/plan/CHECKPOINT_2026-08-02.md — dated resume stage (what landed on
   main, live seed block, Phase 1 remainder). Do not skip this for "status."
4. docs/COMPLETION_PLAN.md — §0 (standard of evidence) is binding on you,
   then the gap register R0-R11 and the phases.
5. docs/plan/README.md — the workstream index, and its "Findings that changed
   the plan" section. Read those five refuted claims carefully. Each was
   plausible, written down as fact, and false. They are your calibration.
6. The specific docs/plan/W*.md for whatever you pick up.

THE STANDARD OF EVIDENCE - this is the job, not a formality
- The running system outranks all prose, including every doc above and every
  code comment. Comments here are prior sessions' output and several are
  provably stale.
- State no finding without the command that produced it. Put the command in
  the PR or the issue comment.
- Report misses before hits. The owner has been told things worked that did
  not, for fourteen months. A confident wrong claim costs more than "not
  measured."
- Held-out verification is mandatory. A fix verified only on the example it
  was developed against is a defect - that exact pattern shipped
  converse_tiered, which then hung on every common topic.
- Distributions stay distributions until the final step. Argmaxing early is
  this codebase's recurring bug.
- Never conclude absence from a type, source, or tier filter. Identity is the
  existence test: hash the content and probe the id. "Documents were never
  ingested" has been wrongly concluded twice this way.

AD-HOC SQL IS A DIAGNOSTIC, NEVER A DELIVERABLE
You will write exploratory SQL. That is correct and expected while you are
iterating - it is how you measure. What is NOT acceptable is leaving it there.

- A query you ran ONCE to check something is a diagnostic. Fine.
- A query you ran TWICE, or that any product path would run, is a missing
  surface. Install it: a .sql.in under extension/laplace_substrate/sql/functions/,
  registered in BOTH sql/manifest.install and sql/manifest.upgrade, named,
  parameterized, documented with the measurement that justified it.
- The MCP `sql` tool is operator-lane only and defaults CLOSED
  (LAPLACE_MCP_OPERATOR=1, app/Laplace.Endpoints.Mcp/SubstrateTools.cs). If you
  find yourself reaching for it on a path a user would take, that is a backdoor
  surface, not a feature. An end user must NEVER compose SQL against this
  substrate - they call laplace.<fn>(args) or a typed tool. Anything else means
  arbitrary queries are the API.
- Typed tools must be PARAMETERIZED end to end. A tool that string-builds SQL
  is the same hole wearing a schema.
- Corollary you will hit constantly: when a hand-run composition works, your
  job is not done until it is an installed function with a test. Two examples
  from this repo's history - `infer()` and `chess_opening_preference()` - were
  each promoted from a hand-run the same day they were discovered, and the
  chess one exists specifically because its four-table join map was tribal
  knowledge that took four failed attempts to rediscover.

REUSE BEFORE YOU BUILD
- Check the installed surface first:
    psql -h /var/run/postgresql -U laplace_admin -d laplace -c "SET search_path=laplace,public; SELECT * FROM api('<substring>');"
  and the MCP tool catalog (the `help` tool). Concluding "no helper exists"
  without checking is a documented recurring error here.
- One implementation per operation; variants are PARAMETERS, not sibling
  functions. A "fast" copy of an existing function is a defect, not an
  optimization (docs/specs/37, L7). render_text vs render_text_fast and
  senses vs senses_with_context are live counter-examples.
- Read-path law before you write any query: hash space until the final step;
  no realize/label/render inside a row-producing SELECT; batch through
  realize_batch over survivors only; both-direction reads are two indexed
  scans, never an OR predicate (that shape produced a 280-second hang).

THE FAILURE MODE THAT DEFINES THIS REPO (docs/specs/37 section 0)
"An operation gets a canonical implementation. The orchestrator that should
call it is never rewired. Both survive. They drift."
Five live instances were found by grep, because grep was the only instrument
available: converse_compose, converse_tiered, walk_branches(p_topic_bias),
prompts_smoke.txt, DocumentRouter.
Before writing anything new: grep for the canonical that already exists and
check its callers. PREFER WIRING TO WRITING.

YOUR WORK LOOP
1. Pick the next item. Order by docs/plan/README.md's phase column, respecting
   stated blockers. Each workstream links a GitHub issue; check it with
   `gh issue view <n>`. There are ~214 open issues and the plan covers ~15 of
   them - the conversational/inference axis. Other axes (model-lane, perf,
   ingest, substrate-law) are real work the plan does not describe.
2. Re-verify the spec against the live system before building. Specs go stale,
   and several claims are explicitly conditional on which corpora are seeded.
3. Work in your own git worktree:
     bash scripts/agent-worktree.sh <name>
   The root checkout stays on main. Stage explicit paths, never `git add -A` -
   it sweeps another agent's files into your commit. Commit early;
   uncommitted work in a shared tree is a data-loss problem, not a merge
   problem.
4. Verify against the workstream's acceptance criteria. All of them. If you
   cannot demonstrate one, say which. Do not declare done.
5. Verify locally (focused tests / `scripts/pipeline.sh` phases as needed).
   Prefer merge → one `main` CI run as the merge validation. Do not burn a
   redundant `gh workflow run` on the feature branch unless something about
   the change cannot be validated locally.
6. Open a PR stating what you measured, what you did not, and what you got
   wrong on the way. Merge on green main CI.
7. Update the issue, the W*.md, and the current CHECKPOINT if reality
   differed from the spec. A stale spec nobody corrected is how this repo
   accumulated the drift you are removing.

HARD RULES - violating these destroys work or data
- One ingest at a time. An unexplained COPY means an ingest is running; leave
  it alone. Never kill a process you did not start.
- /vault and /archive are read-only. Never modify, move, or resize them.
- One database: laplace. No ad-hoc or per-run databases.
- The .sql.in files are the schema of record. Never hand-ALTER a live
  database, never add DbUp migrations for substrate objects, never hand-edit
  generated C (edit the codegen script instead).
- After any engine rebuild: build then install. The extension links the engine
  statically, so engine freshness is not extension freshness.
- CI recreates the database empty. A green fixture test says nothing about a
  seeded box.
- A push to main restarts PostgreSQL and kills any running ingest.

WHERE TO START
Read docs/plan/CHECKPOINT_2026-08-02.md first — it may already answer "where
are we." Then check what this instance actually holds:
  psql -h /var/run/postgresql -U laplace_admin -d laplace -c "SET search_path=laplace,public; SELECT source_name, status, attestations, ended_at FROM ingest_run_journal ORDER BY started_at DESC LIMIT 20;"

If a row is still status=running with no live ingest process, that is an
orphan cut-off — clear it per the checkpoint ops sequence BEFORE starting
another foundation seed. Do not stack a second ingest on a lying journal.

If the box is unseeded or only holds thin residue after a failed Unicode
apply, seeding is the prerequisite for most verification — but only after
the orphan is cleared and main carries the #776 apply dedup. Then:
  gh workflow run seed-foundation.yml --ref main

Then run this, because it decides the order of Phase 3 and two prior analyses
got it wrong:
  psql -h /var/run/postgresql -U laplace_admin -d laplace -c "SET search_path=laplace,public; SELECT tier, count(*) FROM entities WHERE id = word_id('a') GROUP BY tier;"
One row  -> sense priors first (W4 section 2).
Two rows -> the tier seam first (W4 section 1).

Highest leverage after ops unblock, in order: W5 (the eval harness - until
it exists, every quality claim including yours is opinion), W6 remainder
(G4 scaffolding then destination via W3), W3 structural SQL edges (#765),
then W1 (the speaking loop - the machinery is built and simply never called).
Elector + G1/G3/G7/G8 already landed — do not re-implement them.

WHAT IS TRUE ABOUT THIS SYSTEM
It ingests a 9,000-game PGN archive into folded, queryable consensus in
minutes. It rates 1850s book games and 2026 blitz in one arena. It answers
"the capital of California is" through an installed forward pass, in id space,
with the full distribution visible beneath the answer. Its identity law is
verified: the same game witnessed by a book and by an archive is one entity.
It cannot yet hold a conversation. That is the work.
```
