# Conversational provenance

## Identity hierarchy

A tenant, user/participant, session, turn, message, tool call, and content artifact are
distinct entities. A session is a stable identity whose ordered, versioned trajectory
contains turns. Each message is itself a tiered content trajectory.

Tenant scope identifies authorization and isolation. It is not semantic source trust.
Participant, model, tool, corpus, analyzer, and feedback sources retain distinct source
identities.

## Turn contract

A turn records:

- session and ordinal;
- role/participant/source;
- exact content entity and physical trajectory;
- reply/dependency edges to prior turns or tool results;
- request parameters and declared source/context scope;
- selected operation program and semantic trace;
- response content, outcome, and provenance receipt.

Prompt, reply, tool, and feedback witnesses use the governed write lane. Replaying a
read must not manufacture new testimony. Retrying the same write uses an idempotency
key so transport retries do not multiply observation count.

## Conversation state

State is reconstructed from the session trajectory and its witnessed dependencies, not
from a process-local transcript or topic-summary cache. Derived topic/orientation caches
may accelerate reads but remain invalidatable projections.

Corrections add testimony that can refute or supersede a prior claim while preserving
the original turn. Anaphora and topic return resolve against the ordered trajectory and
evidence scope. Unsupported claims remain unknown or cause abstention under the
declared policy.

## Isolation and inspection

Reads default to the caller's authorized tenant/session context. Source-scoped and
pooled views are explicit. Every response can expose a bounded receipt containing the
evidence sources, relations, operation stages, selection, and writes caused by the turn.

## API parity

MCP and OpenAI-compatible endpoints invoke the same conversational program. Roles,
parameters, tools, streaming, and non-streaming alter declared inputs/transport only;
they do not silently select a weaker template path.

## Acceptance

- Exact prior-turn recall comes from the session trajectory.
- Correction changes later selection without deleting history.
- Anaphora and topic return survive process restart.
- Tool calls/results remain ordered and attributable.
- MCP and HTTP produce equivalent semantic traces for equivalent requests.
- Tenant/source isolation and pooled execution are independently testable.
