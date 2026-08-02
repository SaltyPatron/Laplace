# W9 — Discourse memory: read the witnessed turns back into orientation

**Issue:** #759 · **Plan:** `COMPLETION_PLAN.md` R7 / Phase 5 · **Related:**
#658, W7 (`infer` bias heads)

---

## 1. Why this exists

The system deposits every conversational turn as witnessed, folded content — and
then orients the next turn on a **single scalar**. Ask "Who was Napoleon?" then
"Where was he born?" and the second turn has no idea who *he* is.

The deposit half is real and working. The read half does not exist.

## 2. How it works today

### 2.1 The entire session surface

```
session_topics                     converse/session_topics.sql.in:1-7
  UNLOGGED TABLE (session_id bytea, ord int, prompt text,
                  resolved_id bytea, asked_at timestamptz,
                  PRIMARY KEY (session_id, ord))

session_record_prompt(session, prompt, resolved) → void
                                   variant/session_record_prompt.sql.in:1-16
  INSERT with ord = COALESCE(max(ord),0)+1   ← no ON CONFLICT (see risks)

session_last_resolved(session) → bytea
                                   variant/session_last_resolved.sql.in:1-11
  ORDER BY ord DESC LIMIT 1 WHERE resolved_id IS NOT NULL   ← ONE id
```

Readers, exhaustively:

| Caller | Reads |
|---|---|
| `chat.sql.in` | `ctx := session_last_resolved(p_session)`, used only as `IF topic IS NULL THEN topic := ctx` |
| `chat.sql.in` (elaborate) | `count(*) FROM session_topics WHERE resolved_id = topic` — a **turn-depth odometer** |
| `recall.c:1361-1372` | `SELECT session_last_resolved($1)` via SPI |

**The `prompt` text column has no reader anywhere.** It is write-only.

`recall_session` (`recall.c:1313-1411`) synthesizes a session id from
`MyProcPid` when none is given (`:1338-1346`) — a per-backend pseudo-session
that is **not an entity**. Any turn-history read must tolerate that miss.

### 2.2 The turn record that already exists and is never read

`ConversationContent.TryBuildTurnChange` (`:126-190`) emits exactly four
attestations per turn:

| Subject | Relation | Object | context_id |
|---|---|---|---|
| `promptRoot` | `APPEARS_IN` | `sessionId` | `sessionId` |
| `replyRoot` | `APPEARS_IN` | `sessionId` | `sessionId` |
| `promptRoot` | `PRECEDES` | `replyRoot` | `sessionId` |
| `sessionId` | `HAS_ATTRIBUTION` | `userRoot` | NULL (once per session) |

`promptRoot`/`replyRoot` are ordinary text-DAG content roots — **the same id
space `word_id`/`prompt_state` live in**, one tier up. Trust classes are
deliberately low (`UserPromptContent`/`ResponseContent`) so self-witnessing
cannot outshout curated sources.

**The join key already exists.** `SubstrateTools.SessionBytes` (`:216-219`) is
`ConversationContent.SessionId(tenant, key).ToBytes()` — the same bytes passed
as `p_session`. So **`session_topics.session_id` *is* the `Conversation_Session`
entity id.** No new key is needed.

Session id mint: `Hash128.OfCanonical("substrate/conversation/session/{tenant}/{key}/v1")`
(`SubstrateCanonicalIds.cs:44-49,98-99`) — tenant is inside the hash, so a
session key cannot cross tenants, and reads scoped by `context_id` are
tenant-safe by construction.

`TurnCloser` (`TurnCloser.cs:71-134`) is the single close sequence — floor gate,
writer, tenant scope, attribute-once, build, apply — called by MCP
(`SubstrateTools.cs:600-604`), OpenAI-compat (`TurnWitness.cs:81`), and CLI
(`QueryCommands.cs:250`), and pinned as the only sequence by
`ConversationProvenanceGateTests.cs:70`.

## 3. How it should work

### 3.1 Retrieval — the function that does not exist

Turn ordering must come from `attestations`, not `consensus`: `consensus` has no
timestamp or `context_id` usable for ordering, while `attestations` carries
`context_id`, `last_observed_at`, and `source_id`
(`schema/tables/attestations.sql.in:16-25`), indexed by
`attestations_context_btree`.

```sql
-- proposed: session_turns(p_session bytea, p_limit int DEFAULT 8)
SELECT a.subject_id, a.last_observed_at
FROM laplace.attestations a
WHERE a.context_id = p_session
  AND a.type_id = relation_type_id('APPEARS_IN')
ORDER BY a.last_observed_at DESC
LIMIT p_limit
```

**Distinguishing prompt from reply:** use the `PRECEDES` edge (prompt is
subject, reply is object — `ConversationContent.cs:176-178`). It is exact and
tenant-free. Do **not** use `source_id`, which requires knowing the tenant — and
the tenant is inside a hash, not recoverable from the session id.

### 3.2 Injection — two seams, not equivalent

**C-b — topic fallback ladder (shallow, ship first).** Replace
`IF topic IS NULL THEN topic := ctx` in `chat.sql.in` and
`spi_resolve_topic(prompt, context, ctx_null)` in `recall.c:1382` with a
*ranked* prior-turn context: pass the top-k turn roots' elected topics as
`bind.ctx_ids`. The plumbing for an id array already exists —
`recall.c:1383` threads a `bytea[]` into the response builders (`:405-427`).
Additive, bisectable, no change to the election.

**C-a — prior turns as election candidates (deep, ship second).** Add an
optional `p_prior_ids bytea[]` to `prompt_coherence`; prior-turn content ids
join `syn_h` as pseudo-candidates at a reserved `ord` (e.g. `-1`, excluded from
the peer-bit assignment at `:138-139,366-367`), so `pc_scan_edges` credits
`coherence` for edges reaching them. **This is the smallest change that makes
"Who was he?" work** — the pronoun's candidate senses gain mass from the prior
turn's entities, which is precisely the single-content-word limit that no
within-prompt pairwise statistic can fix.

**Critical constraint for C-a:** prior-turn credit must reach `coherence`
**without** entering `total_mass`, or specificity — a ratio over the candidate's
own mass — becomes uninterpretable. `pc_scan_edges:219-221` already gates
`total_mass` on `forward` only; add the same gating on `ord >= 0`.

**Cheapest landing spot of all:** `infer()`'s bias head is already "every sense
of every non-topic token." Prior-turn entities are the natural *second* bias
head and need no new mechanism — union them into `bias`. That composes with W8's
C port for free.

## 4. What to consider

| Decision | Recommendation |
|---|---|
| C-a vs C-b first | **C-b.** One new SQL function plus two call sites; measure; then C-a behind a parameter, with the full measured corpus (glacier/dog/napoleon/car/rain) re-run because C-a changes every election. |
| Ordering source | `attestations.last_observed_at`, not `session_topics.ord` — see durability below. |
| Prompt vs reply | the `PRECEDES` edge, never `source_id`. |
| How many turns | start small (k≈4–8). Every prior turn is mass added to the election; more context is not monotonically better. |
| Retire `session_topics`? | Once turn reads come from `attestations`, the carry table is redundant except as the elaborate-odometer. Consider retiring it rather than maintaining two records of the same fact. |

## 5. Where to look

| Concern | File |
|---|---|
| Session table + carry | `converse/session_topics.sql.in:1-7`, `variant/session_record_prompt.sql.in:1-16`, `variant/session_last_resolved.sql.in:1-11` |
| The only readers | `converse/chat.sql.in` (ctx + depth), `src/recall.c:1361-1372,1382-1396` |
| Turn deposit | `ConversationContent.cs:62-70,126-190`, `TurnCloser.cs:71-134` |
| Session id mint | `SubstrateCanonicalIds.cs:44-49,98-99` |
| Join-key alignment | `SubstrateTools.cs:216-219` |
| Index for retrieval | `sql/indexes/attestations_context_btree.sql.in`, `schema/tables/attestations.sql.in:16-25` |
| Election internals for C-a | `src/prompt_coherence.c:138-139,219-221,366-367` |
| Gate pinning the close | `ConversationProvenanceGateTests.cs:70` |

## 6. Acceptance

1. `session_turns(<session id>)` returns the prompt and reply roots of every
   deposited turn, newest first, prompt/reply distinguishable.
2. Two-turn probe: `chat('Who was Napoleon?', s)` then
   `chat('Where was he born?', s)` — the second turn's elected topic is the
   Napoleon synset, not `he` or `born`.
3. The `MyProcPid` fallback path returns today's answers (zero turns, graceful).
4. No regression on the five measured single-content-word prompts.
5. Turn history survives a crash — reads come from `consensus`/`attestations`,
   not from the `UNLOGGED` table.

## 7. Risks

- **`session_topics` is `UNLOGGED`.** After a crash, `session_last_resolved`
  returns NULL for every session while the witnessed turn record survives
  intact. That asymmetry is an argument for moving carry onto the durable record
  entirely.
- **`session_record_prompt` has a PK race** (`:8-14`): concurrent turns on one
  session throw, with no `ON CONFLICT`. `TurnWitness` serializes via a
  single-reader channel; `chat()` itself does not.
- **The self-witnessing loop closes.** Turn deposits fold into the same
  consensus the next orientation reads. Injecting prior turns as bias makes the
  loop closed: the system reinforces what it already said. Low trust classes
  bound this, but it needs an explicit measurement — **probe whether repeating
  one wrong answer 20 times shifts the election.** If it does, that is a finding
  about the fold, not just about this feature.
- **Tenant safety** holds for `context_id`-scoped reads (tenant is in the hash)
  and does **not** hold for `source_id`-scoped ones. Any source-scoped variant
  must go through `scoped_consensus`.
