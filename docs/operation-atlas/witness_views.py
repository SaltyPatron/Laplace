"""Conversation evidence lifecycle: source observations, not deployment claims."""
def add_witness_views(add):
    witness='Laplace/app/Laplace.Endpoints.OpenAICompat/TurnWitness.cs'
    closer='Laplace/app/Laplace.Substrate/Ingestion/TurnCloser.cs'
    content='Laplace/app/Laplace.Substrate/Abstractions/ConversationContent.cs'
    writer='Laplace/app/Laplace.Substrate/Crud/Npgsql/ConsensusAccumulatingWriter.cs'
    inner='Laplace/app/Laplace.Substrate/Crud/Npgsql/NpgsqlSubstrateWriter.cs'
    staging='Laplace/app/Laplace.Substrate/Crud/Npgsql/NpgsqlPhysicalityStaging.cs'
    apply='Laplace/app/Laplace.Substrate/Crud/Npgsql/NpgsqlWorkingSetApply.cs'
    session='Laplace/extension/laplace_substrate/src/conversation_session.c'
    sql='Laplace/extension/laplace_substrate/sql/functions/converse/session_turns.sql.in'
    catalog='Laplace/engine/core/src/sql_catalog.def'
    add('monorepo-session-append','Monorepo — append occurrence IDs to the session projection','''start
:Validate finite timestamp, positive grants, session and source-unit sizes;
:Charge input payload and array expansion against byte grant;
:Validate one-dimensional bytea array and 16-byte nonnull turn IDs;
if (No added IDs?) then (yes)
 :Return zero before session lock;
 stop
endif
:Require READ COMMITTED isolation;
:Connect SPI; SELECT session entity FOR UPDATE;
if (Session entity missing?) then (yes)
 :PostgreSQL ERROR;
 stop
endif
:Push latest snapshot after acquiring row lock;
:Read Projection and legacy Content manifest IDs;
if (Legacy Content manifest present?) then (yes)
 :Refuse with typed-recovery error;
 stop
endif
:Decode prior trajectory; check allocation and logical-work bounds;
:Concatenate existing occurrence IDs with supplied IDs in order;
:Read Content placement for every occurrence, preserving ordinal order;
:Require all placements and valid tiers / coordinates;
:Preserve old vertex flags; derive flags for new or unflagged turns;
:Compute canonical centroid and Hilbert index;
:Build flagged RLE trajectory and WKB;
:Upsert Projection physicality at stable session placement ID;
:Replace trajectory, centroid, Hilbert, count and timestamp;
:Clear alignment residual and source dimension on update;
:Pop snapshot; finish SPI; free stages; return total occurrences;
stop
legend bottom
Session ID is a stable mutable handle, not a hash of current history.
This function does not COMMIT; caller owns the transaction.
Supplied IDs are appended without a duplicate-removal branch.
Logical grant checks previous_count + total, not only newly added IDs.
endlegend''',[session,sql,catalog], 'Native append body, SQL signatures and catalog queries reviewed. maximum_operations is validated above the reservation but not decremented in this body; source-unit size is checked but no receipt construction consumes its value here. SQL comments describe projection witnessing not established by this body: session_observe is defined but uncalled. Centroid, trajectory and budget allocator internals remain separate.',[])
    add('monorepo-session-read','Monorepo — ordered session recall and label realization','''start
:session_turn_ids validates nonnegative limit;
if (Null session or zero limit?) then (yes)
 :Return no rows;
 stop
endif
:Validate 16-byte session; read Projection and legacy manifest IDs;
:Refuse legacy Content manifest;
:Decode Projection trajectory; emit constituent ordinals up to limit;
:session_turns retains trajectory order and distinct turn IDs;
:For Conversation_Message entities resolve second content constituent;
:For older content-only entities use their own IDs;
:Rank HAS_ROLE and AUTHORED_BY labels by effective rating;
:Use IS_INSTANCE_OF name hop when present, otherwise direct object;
:Read latest APPEARS_IN observation timestamp in this session context;
:Render ordered message content in one render_batch;
:Render distinct role and model names in another render_batch;
:Return ordinal, turn ID, role, model, observed time and surface;
stop
legend bottom
Missing manifest returns no rows; missing labels and observed times can remain NULL.
The native limit bounds emitted constituents, not initial manifest materialization.
Legacy manifests are rejected rather than silently migrated.
endlegend''',[session,sql], 'Native turn-ID reader, message_content_ids and session_turns SQL reviewed. This is source-level recall structure; trajectory decoding, consensus effective-rating law, realization and database contents remain nested. SQL comments about historical validation are not treated as a validation run performed here.',[])
    add('monorepo-working-set','Monorepo — journal, novel evidence and commit boundaries','''start
:Open control connection; resolve epoch support and leaf modulus;
:Begin transaction with laplace_apply_batch advisory lock and durability GUCs;
:Decode staged entities, physicalities and 14-column attestations;
:Reject nonreplayable evidence without consensus participant;
:Deduplicate entities and physicalities by ID;
:Group attestations by ID; sum counts and scores; latest timestamp representative;
:Reject mixed fold-replayable flags within a group;
:Read commit settings; advance optional write epoch;
if (Working-set token exists?) then (yes)
 if (Journal already contains token?) then (yes)
  if (Completion receipts supplied?) then (yes)
   :Insert completions; commit control transaction;
  else (no)
   :Rollback control transaction;
  endif
  :Return JournalReplayHit=true without participant callback;
  stop
 endif
 :Insert journal receipt in control transaction;
endif
:Probe entity, physicality and attestation presence on pooled connections;
:Apply tier-zero completion gate and empty-relation shortcuts;
:Keep absent entity and physicality IDs;
:Keep only novel attestation IDs, carrying grouped count and score;
if (Parallel COPY threshold reached?) then (yes)
 :Copy entities on separate committing connections first;
 if (Working-set token exists?) then (yes)
  :Copy physicalities on separate committing connections;
  :Copy attestations inside control transaction;
 else (no)
  :Copy physicalities and attestations in parallel committing lanes;
 endif
else (no)
 :Copy all retained rows inside control transaction;
endif
if (Participant and token both present?) then (yes)
 :Supply exactly novel grouped attestations to participant;
 :Participant folds evidence and appends conversation in control transaction;
endif
:Insert completion receipts in control transaction;
:Commit control transaction; mark acknowledgement;
:Read optional epoch baseline after COMMIT;
:Return counts, journal status and commit settings;
stop
legend bottom
Any exception in the transaction try attempts rollback and rethrows.
Previously committed parallel entity/physicality copies can remain.
Post-COMMIT epoch read can fail after the write has already committed.
Replay skips the fold and conversation callback; it is not a second occurrence.
endlegend''',[inner,staging,apply], 'ApplyStagesCoreAsync and ApplyPreparedStagesCoreAsync orchestration reviewed, including parallel-copy selection and post-commit read. COPY helpers, probe SQL, advisory-lock participation by other writers, durability GUC implementation, token derivation and native fold semantics remain nested. Default NpgsqlSubstrateWriter durability is Asynchronous; commit acknowledgement is not established as crash-durable storage here.',[])
    add('monorepo-witness-queue','Monorepo — witness admission, cancellation and acknowledgement','''actor Caller
participant TurnWitness as W
queue "Bounded channel / 4096" as Q
participant "Single-reader service" as S
participant TurnCloser as C
alt RecordResponseAsync with empty reply
 W --> Caller : completed task; no enqueue or write
else nonempty response / prompt / complete turn
 Caller -> W : RecordTurnAsync
 W -> W : reject offline, empty prompt, zero session, invalid tenant
 W -> Q : WriteAsync(item, caller cancellation)
 W -> W : await item completion with caller cancellation
 note over Caller,Q
 Cancellation after enqueue stops the caller's wait.
 It does not remove the queued item.
 end note
 S -> Q : ReadAllAsync(service cancellation)
 Q --> S : next item
 S -> C : CloseAsync(item, service cancellation)
 C --> S : deposited boolean / exception
 S -> W : finally complete item's promise with deposited
 alt deposited true and caller still waiting
  W --> Caller : success
 else deposited false and caller still waiting
  W --> Caller : SubstrateUnavailableException
 end
end
note over S,C
 Codepoint cache failure leaves service offline.
 Shutdown sets offline, completes channel, fails queued promises.
 TryEnqueueTurn is a separate nonwaiting API: false when full;
 it has no per-item caller acknowledgement.
end note''',[witness,closer], 'Caller and service cancellation have distinct lifetimes. RecordResponseAsync returning successfully is not proof of a response deposit when the reply is empty. Queue availability is not database or semantic acceptance.',[])
    add('monorepo-turn-close','Monorepo — turn close and tenant bootstrap','''start
:Reject empty prompt, zero session or invalid tenant;
if (Writer absent?) then (yes)
 :Load codepoint cache; construct accumulating PostgreSQL writer;
endif
if (Tenant absent from instance cache?) then (yes)
 :Resolve tenant prompt and response sources;
 :Apply three bootstrap changes;
 :Cache tenant scope only after apply returns;
endif
:Build turn change and ordered occurrence IDs;
if (No witnessable content?) then (yes)
 :Return false;
 stop
endif
:ApplyConversationTurnAsync;
:On normal return set Broken=false; return true;
stop
legend bottom
Cancellation is rethrown. Other caught exceptions set Broken=true and return false.
Bootstrap and turn are separate apply calls. Cache lifetime is one closer instance.
Broken does not prevent a later retry. Caller serialization is required.
endlegend''',[closer,content,writer], 'CloseAsync orchestration is reviewed. Its success follows the writer return; it does not independently inspect a commit receipt. Transaction ownership continues through the partial writer files; this is not an executable-build or durability claim.',[])
    add('monorepo-turn-content','Monorepo — content, occurrence, role and session identities','''start
:Validate phase and occurrence / participant / user identifiers;
:Require occurrence key for split phase; otherwise generate GUID key;
if (Input phase carries reply?) then (yes)
 :Throw argument exception;
 stop
endif
if (Output phase lacks reply?) then (yes)
 :Return false;
 stop
endif
:Compose prompt tree and optional reply tree; reject missing roots;
:Select response source for Output, prompt source otherwise;
:Intent unit label includes session, occurrence and split phase;
:Declare prompt and response source priors; add session entity;
if (Phase is not Output?) then (yes)
 :Stage prompt content and user occurrence;
 :Start ordered turn IDs with user occurrence;
else (Output)
 :Recompose exact user occurrence ID without staging its testimony;
 :Start empty ordered turn IDs;
endif
if (Reply present?) then (yes)
 :Stage reply content and assistant occurrence;
 :Attest assistant DEPENDS_ON exact user occurrence;
 :Append assistant occurrence to turn IDs;
endif
if (User key supplied and phase not Output?) then (yes)
 :Emit user content; attest session HAS_ATTRIBUTION user;
endif
:Build change; return change and ordered turn IDs;
stop
legend bottom
Occurrence = composed metadata branch + shared content branch.
Metadata: tenant, session hex, occurrence key, role, source hex, participant.
Staged occurrence has physicality, HAS_ROLE, APPEARS_IN and optional attribution.
Identical text can share content while retaining distinct occurrence identities.
endlegend''',[content], 'TryBuildTurnChange and StageOccurrence reviewed. ContentTierSpine, HashComposer and attestation construction remain nested semantics. Identity separation does not by itself establish independent evidence or eligibility to learn from generated responses.',[])
    add('monorepo-turn-atomic','Monorepo — conversation apply and atomic participant boundary','''start
:Require nonzero session and nonempty ordered turn IDs;
:Enter concurrency retry loop;
:ApplyCoreAsync with conversation callback;
:Reject disposal; increment inflight count;
:Observe prior fold failure; forward changes;
if (Persistent evidence and NpgsqlSubstrateWriter?) then (yes)
 :Call ApplyWorkingSetAtomicAsync with participant callback;
 :Inside supplied connection and transaction, build delta from accepted evidence;
 if (Accepted delta nonempty?) then (yes)
  :UpsertDeltaInTransactionAsync;
 endif
 :Execute conversation.append_turns with same connection and transaction;
 :Bind session, ordered IDs, built time, intent and physicality budgets;
 :Return through inner writer;
 :Update fold statistics only if not JournalReplayHit;
else (no)
 :Throw: conversation history requires atomic writer;
 stop
endif
:Decrement inflight in finally;
:Return result;
stop
legend bottom
Transient exceptions may retry according to ConcurrencyRetry policy and delay.
Callback and outer transaction ownership are separate: callback has no COMMIT.
Native append_turns, accepted-evidence selection and actual commit remain boundaries.
endlegend''',[writer,inner], 'Reviewed callback ordering: accepted-evidence fold, then session append, on one supplied transaction. The partial writer dispatches through NpgsqlPhysicalityStaging.ApplyStagesCoreAsync to NpgsqlWorkingSetApply.ApplyPreparedStagesCoreAsync; those nested branches require separate review. No build or runtime success is inferred from the intended atomic API.',[])
