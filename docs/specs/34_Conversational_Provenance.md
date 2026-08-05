# 34 — Conversational Provenance: Tenant, User, Session

Date: 2026-07-23. Status: living spec, binding, annotate-on-supersede.
Companion to 11 (chess provenance — the pattern this transplants), 15 (Gödel/OODA
loop — the lane this completes), 16 (tier-correct attestation).

## 0. The one law

A conversational turn is witnessed EXACTLY like a chess ply: content is global and
deduped, evidence carries full provenance, consensus folds shared knowledge.
The three identities and where each lives:

| Identity | Substrate slot | Mint |
|---|---|---|
| **Tenant** | `source_id` on every turn evidence row | `source_id('UserPrompt@{tenant}')` / `source_id('Response@{tenant}')` |
| **Session** | `context_id` on every turn evidence row + a `Conversation_Session` context entity | `canonical_id('substrate/conversation/session/{tenant}/{key}/v1')` |
| **User** (within tenant) | `HAS_ATTRIBUTION` edge on the session entity | user key as ordinary content |

Because attestation id = blake3(subject | type | object | **source** | context),
two tenants asserting the same fact are distinct evidence rows BY CONSTRUCTION —
provenance is never mashed, no entity-resolution pass, no filter logic to get wrong.
Because the subjects (prompt/reply content roots) are global content ids, the same
exchange still corroborates at shared consensus cells — isolated AND acting as a
whole, the same mechanism chess uses for moves (doc 11).

C# authority: `ConversationContent` (app/Laplace.Substrate/Abstractions/ConversationContent.cs).
Ids mint ONLY through `SubstrateCanonicalIds` / `Hash128.OfCanonical` — the SHA256
session hack is dead and gated against (ConversationProvenanceGateTests).

## 1. Sources and trust

- Per-tenant sources `UserPrompt@{tenant}`, `Response@{tenant}`; single-segment
  names (canonical-key law rejects `/` in segments), byte-identical to SQL
  `laplace.source_id(...)` so operator scoping from psql is trivial.
- Trust classes are the BASE conversational classes (`UserPromptContent`,
  `ResponseContent`): the tenant changes WHO witnesses, never what KIND of witness.
- Witness weight = `RelationTypeRank × SourceTrust × tenantTrust` — the third
  factor is the tenant/user trust multiplier (default **1.0, neutral**; values are
  operator policy, never invented in code). It flows into `witness_phi` → opponent
  RD: trust stays inside the rating math, identity stays outside it.
- Registered lazily on a tenant's first turn (`BuildTenantBootstrapChanges`):
  both sources + `HAS_TRUST_CLASS` + declared relations {APPEARS_IN, PRECEDES,
  HAS_ATTRIBUTION} (family-expanded — the HAS_POS law) +
  `(source HAS_ATTRIBUTION tenantNameRoot)`. Rows idempotent; testimony refold
  bounded to process restarts (same class as every decomposer bootstrap).
- Tenant/session/user identifiers are canonical-key segments and attacker-
  controlled on the wire: strict charset `[A-Za-z0-9._@-]{1,128}` (`\z`-anchored),
  400 otherwise. Load-bearing for key integrity.

## 2. The turn — ≤4 evidence rows, no re-witness grind

One turn = ONE `SubstrateChange` = ONE apply (the accumulating writer's φ-per-cell
invariant; cross-tenant turns are never batched). Content lands via the ordinary
text DAG mint (Pillar 3a untouched: content emits NO attestations). The turn-level
testimony, all with `context_id = sessionId`:

1. `(promptRoot APPEARS_IN session)` — witnessed by `UserPrompt@{tenant}`.
2. `(replyRoot APPEARS_IN session)` — witnessed by `Response@{tenant}`.
   Membership rows are single-witness by nature (chess AppendGameMeta parity).
   That is CORRECT record-lane behavior — do not "clean it up".
3. `(promptRoot PRECEDES replyRoot)` — witnessed by `Response@{tenant}`: the
   corroborating cell. The same Q→A across sessions and tenants folds at ONE
   consensus cell while evidence rows keep per-tenant/per-session provenance —
   the chess MOVE-edge lesson. Turn-level only; per-token chains stay deleted.
4. `(session HAS_ATTRIBUTION userRoot)` — once per session, only when the client
   supplies the OpenAI-standard `user` field.

Repeated turns re-witness (rows dedup by content address, testimony folds again) —
a repeated utterance IS another witness, exactly like every play of a chess move.

## 3. The wire protocol — state lives in the substrate

- Client sends only the NEW turn. Resent history is ignored by construction
  (server consumes the last user message only). The KV/context-window apparatus
  has no analogue here: the walk reads a persistent graph.
- Continuation token = the session KEY (never raw id bytes — the server always
  re-mints `SessionId(tenant, key)`, and tenant-in-the-key makes cross-tenant
  session forgery structurally impossible). Sources: `session` body field, else
  `X-Laplace-Session` request header, else server-minted `s-{guid}`. Returned on
  EVERY response: `X-Laplace-Session` header (stream + non-stream) + chat
  `metadata.session`.
- The same key mints the byte-identical session id at every surface (API, MCP
  `mcp-local` tenant, tests) — one id law, no per-surface session stores.
  `recall_session`/`session_topics` receive this id as their opaque carry key;
  the extension needed ZERO changes.
- Model routing is exact-id via `ModelCatalog` (unknown model → 400
  `unknown_model`). Empty consensus returns empty content + `reply_rows: 0` —
  no canned assistant prose, ever.

## 4. Isolation reads — scoped pour, default off

Default read = global consensus (act as a whole). Opt-in `scope: "tenant"`
(converse lane only) re-folds the tenant's own witnessed world via
`laplace.scoped_consensus(ARRAY[promptSource, responseSource])` materialized as
`pg_temp.consensus`, which shadows `laplace.consensus` for every unqualified read
on that connection — the Build-A-Bear scoped-pour mechanism, reused verbatim.
One connection per request; the pool reset (DISCARD ALL) drops the shadow.

KNOWN LIMIT: `generate_walk.c` schema-qualifies `laplace.consensus`, so the
GENERATE lane cannot be tenant-scoped by the shadow today. Scoped reads are
converse-lane only until that read is unqualified.

## 5. Acceptance test (live-verified target shape)

Deposit two turns for tenants A and B with the same exchange, then:

```sql
-- per-tenant evidence, session on every row
SELECT count(*) FROM laplace.attestations
WHERE source_id = laplace.source_id('UserPrompt@' || :tenantA)
  AND context_id = :sessionA;                      -- ≥ 1

-- distinct evidence rows per tenant (provenance unmashed)
-- same (subject,type,object), different attestation ids

-- the corroborating cell folded, readable by the next walk
SELECT rating, rd, witness_count FROM laplace.consensus
WHERE subject_id = :promptRoot AND type_id = laplace.relation_type_id('PRECEDES')
  AND object_id = :replyRoot;                      -- witness_count ≥ 2 (A and B)

-- isolation: A's scoped world contains A's cell, not B's private one
CREATE TEMP TABLE consensus AS
  SELECT * FROM laplace.scoped_consensus(ARRAY[laplace.source_id('UserPrompt@' || :tenantA),
                                               laplace.source_id('Response@' || :tenantA)]);
```

Pinned by `ConversationProvenanceGateTests` (architecture, always-on) and
`ConversationProvenanceLiveTests` (Tier=db, the queries above through the real
writer spine and `recall_session`).
