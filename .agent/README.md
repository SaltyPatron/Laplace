# `.agent/` — Agent Territory

This directory is **agent-managed state**. Agents (Claude Code, Copilot, Aider, Cursor, or any other) may freely read and write under `.agent/`. **Files here are NOT user-authored documentation** — they will not be confused with the project's design spec or operational manual.

## Purpose

Keeping status, decisions, blockers, and agent working notes separate from the user-authored docs (`DESIGN.md`, `GLOSSARY.md`, `RULES.md`, `STANDARDS.md`, `OPERATIONS.md`, `README.md`) prevents "documentation infection" — the pattern where agents accumulate progress notes inside user-facing files until those files become incoherent.

## Layout

```
.agent/
├── README.md                # This file
└── status/
    ├── STATE.md             # Current project state — milestones reached, last verification
    ├── decisions.md         # Decisions log (timestamped; what / why / by whom)
    └── blockers.md          # Open blockers, with diagnostic context
```

## Conventions

- **All entries are timestamped** (ISO 8601 with timezone).
- **Decisions log is append-only.** Older decisions stay; superseded decisions get a follow-up entry referencing the original.
- **STATE.md is the single source of truth** for "what's done / what's in progress / what's next." Agents update it after meaningful state changes.
- **Blockers file:** open blockers at the top; resolved blockers move to a "Resolved" section at the bottom (chronological).
- **No agent-specific subdirectories** like `.agent/claude/` or `.agent/copilot/`. All agents share `.agent/status/`. Differentiation happens via "by:" lines in entries.

## What goes here

✓ Project state ("Chunk 3 in progress; Chunk 1 and 2 verified")
✓ Decisions ("Chose `int64` fixed-point for Glicko-2 — see RFC link")
✓ Blockers ("Spectra not installed yet — need download")
✓ Verification run results ("Determinism check passed on AVX2 box at <timestamp>")
✓ Agent working notes during multi-step investigations
✓ Drafts that aren't ready for user-authored docs

## What does NOT go here

✗ Architectural specs (those go in DESIGN.md)
✗ Terminology definitions (GLOSSARY.md)
✗ Rules / invariants (RULES.md)
✗ Coding standards (STANDARDS.md)
✗ Operational procedures (OPERATIONS.md)
✗ Any content the user expects to author directly

## For agents reading this

If you're about to put a status update, a decision, or a working note in any file at the project root that isn't `.agent/`, **stop**. It goes here.
