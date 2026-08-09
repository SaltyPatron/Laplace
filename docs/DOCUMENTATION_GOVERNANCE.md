# Documentation and agent-instruction governance

This document defines which repository artifacts may direct work. It prevents a dated
report, agent note, prompt, or issue body from silently overriding the invention or the
running system.

## Authority hierarchy

When two sources disagree, use this order:

1. Operator direction for the current task and its authorized scope.
2. Running behavior and live schema, measured through installed canonical operations.
3. Source code, generated manifests/inventory, and executable tests.
4. Normative contracts in `docs/specs/` and accepted decisions.
5. Current architecture/product definitions: `docs/ARCHITECTURE.md`,
   `docs/INVENTIONS.md`, and the real-conversation/model-consensus finish line.
6. GitHub issues for active work ownership, dependencies, and acceptance status.
7. Plans and guides for implementation context.
8. Research and archive material as evidence about the past only.

No agent-generated memory, transcript summary, scratchpad, checkpoint, report, branch
description, or issue prose can override levels 1–5.

## Artifact classes

| Class | Location | May contain status? | May direct implementation? |
|---|---|---:|---:|
| Normative specification | `docs/specs/` | No | Yes, within authorized scope |
| Accepted decision | `docs/decisions/` | No | Yes |
| Architecture/product definition | `docs/ARCHITECTURE.md`, `docs/INVENTIONS.md`, active finish-line docs | No transient status | Yes |
| Plan/design | `docs/plan/`, `docs/invention/` | No mutable status tables | Only with GitHub owner and authorization |
| Guide/runbook | `docs/guides/` | Operational prerequisites only | Yes for the named operation |
| Active work item | GitHub issues | Yes | Defines scope/acceptance, not factual truth |
| Generated inventory | `docs/INVENTORY.md` | Generated facts only | Evidence |
| Historical record | `docs/archive/`, `.scratchpad/` | Yes, frozen | Never |

## Normative-document rules

A normative file states stable requirements and acceptance behavior. It must not contain:

- “current,” “still open,” “landed,” “closed,” or branch/PR implementation status;
- live row counts, timings, machine state, or dated database observations;
- remediation execution logs, incident narratives, or session history;
- a mutable issue checklist or backlog ranking;
- hardcoded countable inventory owned by generated files;
- instructions to trust an agent-authored report.

Superseded text is preserved through Git history or an archived snapshot. The current
spec is edited into a coherent contract; readers are not required to interpret layers
of contradictory inline annotations.

## GitHub issue contract

GitHub is the only active work-status system. A finish-line issue includes:

- user-visible outcome;
- verified gap/evidence pointers;
- explicit non-success/non-goals;
- dependencies and existing owners;
- behavioral acceptance with a named seed/runtime profile;
- required provenance/trace evidence.

Issue state does not prove runtime behavior. Closing requires executable acceptance,
and a closed issue cannot override a failing product gate.

## Agent-instruction contract

Standing agent instructions are short, stable, and operational. They may define
authorization boundaries, destructive-action bans, worktree discipline, database
discipline, build order, and communication constraints. They must not contain:

- threats, violent incident narratives, or speculation about operator intent;
- accusations, motive claims, or emotional framing;
- duplicated architecture/status prose;
- stale counts, filenames, branches, PRs, or machine measurements;
- harness-specific tool requirements without an available fallback;
- contradictory “execute automatically” and “never implement” rules.

Authorization resolves the apparent start conflict: execute the next mechanical step
inside an explicitly authorized task; otherwise continue read-only research or ask only
for a genuinely material choice. Never demand a ceremonial trigger such as “say go.”

## Historical material

`.scratchpad/` and `docs/archive/` are evidence preservation areas. Agents do not scan
them for status or next work. A current issue/spec may cite a specific historical file
for rationale, but must restate every active requirement and verify it against current
code or the running system.

## Measurement discipline

Measurements name the environment, seed/source scope, operation, and observation time.
They belong in issue comments, test artifacts, or archived reports. Stable docs describe
how to measure and what acceptance means, not yesterday's result.

Sandbox visibility and host state are separate facts. A harness reporting a read-only
mount or failed network call does not prove the host mount or credentials are broken;
verify in the authorized execution context before reporting an operational failure.

## Change discipline

- Preserve unrelated work in isolated worktrees.
- Stage explicit paths only.
- Update the authority map and affected cross-links in the same change.
- Archive rather than delete unique research or invention material.
- Add active gaps to GitHub instead of embedding a new tracker in prose.
- Validate documentation inventory and governance checks before publication.
