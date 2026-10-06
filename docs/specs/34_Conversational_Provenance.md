# Conversational provenance

## Identity hierarchy

A tenant, user/participant, session, turn, message, tool call, and content artifact are
distinct forms:

- Tenant is authorization and isolation, not trust and not a source identity.
- A user/participant is a witness.
- A session is a handle: a projection over a growing ordered trajectory of turns, like a
  git ref. It is not a hashed identity.
- A turn is one act. Its session and ordinal are position, never hashed.
- A message is a content trunk. A prompt with text, a photo and a video is one trunk
  `[metadata, content]` whose content is `[text, image, video]`, each a tree down to
  codepoints. A photo of a sentence is pixels, not the sentence; an OCR or ASR link is a
  calculation with its analyzer and receipt.
- A tool call and its result are a calculation.
- An artifact is a file trunk with exact reconstruction.
- A response is Laplace's content at its trust, attesting its dependence on the prompt.
- A receipt and a firmware image are their own forms.

The same prompt text in two turns is one content entity occurring twice.

No identity here is a hash of a made-up key string or carries a version suffix. The
current code derives the session handle as `Hash128.OfCanonical` of a tenant/session key
string; that is a fake identifier and violates the identity law.

The current session handle stores its growing ordered turn manifest as a Projection
physicality. Its constituent turn identities resolve to canonical Content
physicalities admitted through the governed writer. The handle must never acquire a
Content physicality under an unrelated, mutable manifest. This projection does not
claim that immutable whole-session snapshots have been admitted as canonical content.

Tenant scope identifies authorization and isolation. It is not semantic source trust.
Participant, model, tool, corpus, analyzer, and feedback sources retain distinct source
identities.

## Turn contract

A turn records:

- session and ordinal, as position (never hashed into the turn's identity);
- role/participant/witness;
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
