# Retro: Chunk 0 — Framework + SDLC foundation

**Closed:** 2026-05-21
**Duration:** ~1 day of agent work (interleaved with Anthony's redirects)
**Issues:** SDLC refactor — no single closing issue (cross-cutting)

## What went well

- **PG-extension research lifted the architecture out of a bad split.** Web search surfaced `pg_extension_config_dump()` + `ALTER EXTENSION UPDATE` + `@extschema@` — together those make extension-owned schema strictly better than DbUp-owned schema for Laplace's case. The outcome is [ADR 0023](../../docs/adr/0023-extension-owns-schema-dbup-orchestrates.md); DbUp now narrows to extension-lifecycle orchestration only.
- **Three-layer architecture survived first contact.** Layer 0 (root bootstrap) / Layer 1 (DbUp + extension) / Layer 2 (Justfile + CI) cleanly separated, each independently resettable. `bootstrap-reset` doesn't touch substrate data; `db-nuke` doesn't touch runner identity.
- **ADRs caught up with reality.** 23 ADRs documenting every architectural decision so far, including supersedence chains (0003 → 0015 for hashing; 0014 → 0019 for runner identity). The decision history is now first-class.
- **Conventional Commits + release-please + DoD landed together.** Versioning, changelog, and "Done means Done" all share one definition.

## What didn't

- **Multiple "MVP-shaped" detours.** Initial draft used dbmate (Go binary) without noticing the project was already in .NET / Npgsql. Initial draft did `createuser --superuser ahart; createdb ahart` — fast, wrong, sabotage-shaped. Initial draft put sudo commands in chat for Anthony to paste, instead of building them into bootstrap. Each one was a corner-cutting reflex masquerading as forward progress.
- **Hyperfocus on the last thing said.** When Anthony pushed back on sudo-commands-in-chat, the response narrowed to "stop putting sudo in chat" and missed the bigger lesson: the entire setup model was wrong. He had to redirect with "ultrathink about everything as a whole and how you're just hyperfocused on the last thing said and ignoring the forest for not even the trees."
- **Bootstrap script Write failed mid-refactor** because Write requires Read first after a rename, and the prior agent didn't sequence that correctly. Recoverable but added friction.
- **Default version was inconsistent.** `extension/laplace.control` said `1.0.0`; `extension/laplace--1.0.0.sql` matched, but `laplace_version()` in C returned `0.1.0` and the CI smoke test expected `0.1.0`. Three sources disagreed; nobody caught it until ADR 0023 forced the bump.

## Surprises

- **Anthony was right that PG extensions could own tables — including with `pg_extension_config_dump` for user data.** I had been treating extensions as "only types + functions" by default. The PG docs were explicit and the design fit Laplace better than the migration-tool model.
- **DbUp's `EnsureDatabase` does what we need.** Initial plan had Layer 0 creating the database, which leaked Layer-1 concerns into Layer-0 root scripts. DbUp does it natively in C# with no extra ceremony.
- **The bash bootstrap script ended up at ~400 lines.** That feels long, but every section is doing real work and is reversed by `reset`. Probably worth a future ADR confirming "bash with `set -euo pipefail` is the right tool for Layer 0; don't rewrite in Python/Go later."

## Action items for next chunk

- [ ] **Re-trigger CI on `main`** once this refactor lands; watch `db-ensure` job apply migrations cleanly.
- [ ] **Verify `bootstrap-reset` end-to-end** on a real run (it's untested in this session — only syntax-checked). Catch any sed regex edge case before the next bootstrap.
- [ ] **Add a `precommit` script entry** that runs the Conventional-Commits validator locally + banned-vocabulary scan, mirroring what `.pre-commit-config.yaml` does at hook time.
- [ ] **Stop guessing version numbers.** When introducing new files or .so functions, derive the version from `awk -F\' '/^default_version/{print $2}' extension/laplace.control`, never hardcode.
- [ ] **Start Chunk 1 with a written plan** in `.agent/status/plan.md`, not as agent-driven inference from `DESIGN.md`. The plan IS the artifact; following it is the discipline.

## Anti-patterns to watch for

- **"MVP first, refactor later."** Sabotage. Never. Anthony was explicit: "no MVPs." If it's not real, don't build it.
- **Hyperfocus on the last redirect.** When pushed back on, zoom OUT, not in. The redirect is almost always a clue that the broader frame is wrong, not that one detail needs adjusting.
- **Doing root tasks in chat instead of in a script.** If a Layer-0 operation needs `sudo`, it goes in `scripts/bootstrap-laplace-runner.sh` (or another versioned, reset-capable script). Never copy-paste-able sudo commands as a UX.
- **Convention drift between sibling files.** `default_version` in .control vs filename of .sql vs hardcoded version in C vs expected value in CI: four places, one truth. Always derive, never duplicate.
- **Picking a tool without checking what's already in-stack.** dbmate would have been fine for a project starting from zero; Laplace had .NET / Npgsql already. The same goes for any future "let's add tool X" reflex — check the stack first.
- **Treating database schema as application schema.** The substrate IS the database. ADR 0023 captures this; future chunks must not regress into thinking of `entities` / `physicalities` / `attestations` as ordinary app tables that happen to live in PG.
