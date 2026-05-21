# `.agent/retros/` — Retrospectives

One retro per chunk, written after the chunk is closed. Format: `chunk-<N>-retro.md`. The retro is part of the [Definition of Done](../../CONTRIBUTING.md#definition-of-done) — a chunk isn't actually done until the retro is written.

## Why

Without retros, the same anti-patterns recur. The substrate spent 12 months at 0% progress across prior iterations partly because every iteration hit the same set of failure modes and nobody captured them anywhere durable enough to break the loop.

A retro after each chunk is the anti-loop mechanism.

## Format

```markdown
# Retro: Chunk N — <title>

**Closed:** YYYY-MM-DD
**Duration:** N days (start → close)
**Issue:** #NNN

## What went well

- ...

## What didn't

- ...

## Surprises

(Things that weren't a problem, just unexpected — useful for calibration.)

- ...

## Action items for next chunk

- [ ] Concrete change ...
- [ ] Concrete change ...

## Anti-patterns to watch for

(Sabotage-shaped behaviors that almost happened or did happen. Capturing these is the whole point.)

- ...
```

## Conventions

- **Concrete, not abstract.** "I wrote a script with sudo commands in chat instead of building it into the bootstrap" is useful. "Communication was sometimes unclear" is not.
- **Name the anti-pattern.** If it has a name (per [feedback_no_sabotage.md](../../../.claude/projects/-home-ahart-Projects-Laplace/memory/feedback_no_sabotage.md) or similar), use it. New anti-patterns get a name and a memory entry.
- **Action items are commitments**, not suggestions. They either land in the next chunk or get bumped to a tracked issue.
- **No retroactive editing.** A retro captures what was true at the time. If the assessment changes later, write a follow-up retro entry, not an edit.
