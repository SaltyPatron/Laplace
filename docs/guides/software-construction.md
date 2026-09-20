# Structural software construction

This guide explains the software-development consequence of Laplace's universal typed AST, canonical identity, trajectories, witnessing, query-relative coupling and forward program.

It is a product/implementation guide under docs/INVENTION.md, docs/CAPABILITIES.md, and specs 36/37. It does not define a private code-generation architecture.

## Core rule

Laplace constructs software structure. It does not primarily predict source text.

~~~text
requirement / obligation
-> exact target language + grammar + runtime/toolchain generation
-> canonical repository/application state
-> COUPLE against known AST/call/dependency/repair state
-> reuse
-> compose
-> minimally adapt
-> construct novel AST only where necessary
-> REALIZE source
-> compile / link / test / analyze / simulate / run
-> WITNESS outcomes
-> repair smallest divergent subtree
-> repeat until obligations close or WHY_NOT remains
~~~

Source text is a realization of selected canonical structure.

## Exact language means exact language

A request for Bash is bound to the admitted Bash grammar/runtime contract. A request for Zsh requires a qualified Zsh provider. Similar-looking shell languages are not interchangeable merely because a probabilistic generator has seen them together.

The same applies to language versions, dialects, frameworks, ABI targets, toolchains and platform contracts.

If the requested grammar/provider is unavailable, the correct result is explicit missing capability, not syntactic guessing.

## Search before invention

Before creating a new implementation, Laplace asks:

~~~text
1. Does the exact canonical implementation already exist?
2. Can existing canonical structures compose to satisfy the requirement?
3. Is there a close lawful implementation that needs only a bounded mutation?
4. Only then: what genuinely novel structure is required?
~~~

This means duplicate elimination is part of generation.

### Exact duplicates

If two admitted AST subtrees are the same canonical composition under the same recipe, they have the same identity. Their occurrences, files, repositories and provenance remain distinct, but the implementation structure is already known to be identical.

### Deeper duplicates

Different source can still implement the same operation. Calculated comparison may include normalized AST, symbol/role normalization, call/dependency/control/data-flow shape, algebraic equivalence under a declared numeric contract, behavioral evidence from toolchains/runtime, and ordered trajectory/Fréchet comparison where lawful.

Same semantics with multiple implementations is a consolidation candidate. Similar names do not prove equivalence; different names do not disprove it.

## Development is a witnessed trajectory

A software task is not one prompt followed by one answer.

~~~text
R0
-> mutation Δ1
-> compiler diagnostics / exceptions / test failures
-> mutation Δ2
-> different observations
-> ...
-> verified R1
~~~

Every attempt has exact code identity, toolchain/provider identity, environment/target identity and outcome.

A failure remains useful negative evidence. If a structurally equivalent repair later appears elsewhere, Laplace can avoid repeating already-observed failed transformations.

A successful repair is not merely a diff. It is an evidence-backed transition explaining which obligation failed, which structure changed, and which verification closed the obligation.

## "One of these things is not like the others"

Software repair is a natural residual operation. If several candidate structures satisfy the same type/API/toolchain constraints and one fails, the failure is attached to the exact divergent structure and context.

Likewise, if a previously stable execution has one anomalous failure, Laplace can tug artifact identity, source/AST structure, dependencies, toolchain/runtime versions, target hardware, input, execution path, environment and prior repair trajectories. The unresolved residual remains explicit when the current model cannot explain it.

## Cross-repository maintenance

A new repair can immediately query other authorized repositories.

Useful questions include:

~~~text
Which repos contain the exact defective subtree?
Which contain a normalized equivalent?
Which contain the same dependency/control-flow preconditions?
Which have a Fréchet-close failure/repair trajectory?
Which already contain a verified solution?
Which appear mitigated?
~~~

The match must explain its evidence. Fréchet is one comparison plane, not a universal similarity score.

Authority is separate from discovery. A principal may be able to analyze/report or propose a patch without having permission to write, merge or deploy.

## Repository root is the application

A repository is recursively composed structure, not merely a directory of independent files.

A local edit creates a new leaf/subtree and new ancestors to the root:

~~~text
expression*
-> statement*
-> block*
-> method*
-> class/file*
-> directory ancestors*
-> repository root R1
~~~

Unchanged canonical subtrees remain shared with R0. Laplace therefore logically has the complete R1 application while physically constructing only the semantic delta and its ancestry. A checkout is realization of R1.

## Full-repository realization

Construction work and output work are different:

~~~text
construction work ~ semantic delta + affected ancestry
realization work  ~ requested output bytes/files
~~~

A full checkout must still write the requested bytes, but it must not redo cognition for every unchanged file.

The repo-mutation-roundtrip benchmark in docs/benchmarks/SUITE_ROADMAP.md owns the falsifiable full-repository proof shape.

## Patch and deployment

A patch can be represented as a complete root transition:

~~~text
FROM R0
TO   R1

transfer = closure(R1) - objects already present
proof    = mutation/toolchain/runtime receipts
activate = R1
rollback = R0
~~~

Mutable database/configuration/secrets/external systems remain explicit transition obligations. Root immutability does not make mutable state disappear.

Deployment itself can become a governed operation: resolve current state, check authority, stage missing structure, perform declared migrations, validate R1, activate, observe, witness, and roll back when the target obligations are violated.

## Acceptance

A real software-construction implementation demonstrates that exact grammar/toolchain target is bound before construction; canonical reuse is attempted before novel structure; syntax is valid by construction for the selected grammar; type/API/dependency constraints participate; failures and successes are witnessed with exact provenance; repair changes a bounded subtree rather than regenerating the project; duplicate implementations feed reuse/consolidation; repository-root identity changes only through affected ancestry; complete checkout realization has exact parity; and cross-repository analogies are explainable and authority-scoped.

Tests prove these behaviors. They are not the behaviors themselves.


## Current implementation owners

- #894 — structural constructor, canonical reuse, toolchain witnesses and repair trajectories.
- #452 — repository Git/history/application-root representation and root-transition patching.
- #1710 — full-repository mutation/reuse/realization benchmark.
