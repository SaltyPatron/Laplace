# Substrate operation ISA

## Contract

Every read, generation, game, code, model-analysis, and export path is a typed program
over the operation families below. Public SQL/MCP/HTTP tools bind and compose these
operations; they do not invent private semantic implementations.

| Opcode | Family | Contract |
|---|---|---|
| OP0 | RESOLVE | surface/content/scope → canonical ids and typed input |
| OP1 | ORIENT | session/task/evidence → active context |
| OP2 | ROUTE | context → typed relations, bands, modalities, constraints |
| OP3 | SCAN | typed query → bounded indexed candidates |
| OP4 | COMPOSE | candidates/evidence → typed frontier/trajectory |
| OP5 | PROPOSE | frontier → valid next entities/actions |
| OP6 | STEER | proposals + state/evidence → ranked proposals |
| OP7 | SELECT | ranked proposals + policy → selected identity/action |
| OP8 | REALIZE | identity/action → requested external surface |
| OP9 | WITNESS | outcome/receipt → governed append-only testimony |

## Ordering

The canonical order is OP0 through OP9. A program may omit an operation only when its
precondition is already established and the trace identifies that fact. It may loop
OP2–OP8 for multi-hop or multi-constituent generation. OP9 follows an actual outcome;
it is never an implicit side effect of a read.

## Shape and typing

Each operation declares input/output entity classes, relation families, source/context
scope, null/unknown behavior, ordering, score domain, bounds, and receipt fields. Shape
is data/program memory, not a switch statement scattered across endpoints.

The ISA distinguishes:

- exact identity from spatial candidate discovery;
- observed from calculated testimony;
- sequence evidence from consensus evidence;
- source-scoped from pooled reads;
- proposal from selection;
- realization from classification.

## Implementation law

There is one canonical implementation per operation fact. SQL references and native
accelerators require parity tests. Endpoint-specific helpers delegate to the same
program. Installation cannot arbitrarily select competing semantic bodies.

## Receipts

Every completed program can report its operation sequence, inputs, source/context
scope, candidate reductions, evidence cells, scoring policy, selected identity,
realization, and writes. Receipts are bounded and content-addressable.

## Acceptance

- Every product surface maps to an inspectable ISA program.
- Static gates reject untyped/private operation implementations.
- Native and reference paths agree on ids, ordering, scores, and unknowns.
- MCP and OpenAI requests with equivalent meaning execute the same program.
- Model/source ablation changes declared scope, not endpoint code.
