"""Monorepo HTTP-to-SQL observations, kept separate from modular runtime."""
def add_monorepo_views(add):
    endpoint='Laplace/app/Laplace.Endpoints.OpenAICompat/EndpointMappings.Inference.cs'
    add('monorepo-completions','Monorepo — completions endpoint','''start
:Validate JSON, served completions model and nonempty prompt;
:Resolve turn scope; require available witness;
:Set session header;
:Default steps 64, temperature 0.7, window 5, topK 10;
:Reject unsupported top_p/topic_boost/stop;
:Require steps/topK 1..4096, window 1..64, finite positive temperature;
if (Validation fails?) then (yes)
 :Return corresponding request or witness-availability error;
 stop
endif
:Require quote;
if (Gate disallows?) then (yes)
 :Return 402;
 stop
endif
:Consume quote if present; catalog conversation; RecordPromptAsync;
if (Streaming?) then (yes)
 :Begin SSE;
 :Consume ForwardTurnStreamAsync and append/send each token;
 :Optionally expose token Mu in logprobs field;
 :RecordResponseAsync with accumulated string; send stop chunk;
 :On non-cancellation exception in this try, send stream_failed;
 :Send done; return empty HTTP result;
else (no)
 :Collect ForwardTurnStreamAsync tokens;
 :Concatenate text; RecordResponseAsync;
 :Return JSON, optional Mu array and optional quote receipt;
endif
stop''',[endpoint],'Unlike chat, this wrapper rejects temperature zero and applies explicit upper limits. It casts token.Mu into fields named logprobs; probability interpretation is not established by this mapping. Quote/prompt effects precede generation; no combined rollback appears here. Stream cancellation is not handled by the non-cancellation catch.',[])
    add('monorepo-embeddings','Monorepo — embedding response construction','''start
:Validate JSON and served embedding model; resolve includeMeaning flag;
:Read string or string-array input;
:Drop nonstring/whitespace-only elements; trim retained strings;
if (No retained inputs or validation fails?) then (yes)
 :Return corresponding request error;
 stop
endif
:Require quote;
if (Gate disallows?) then (yes)
 :Return 402;
 stop
endif
:Consume quote if present;
while (More input groups?) is (yes)
 :Launch at most eight EmbeddingAsync tasks with meaningLimit 10;
 :Await tasks in input order and retain result order;
endwhile (no)
while (More results?) is (yes)
 if (Form resolved?) then (yes)
  :Emit five-value vector X, Y, Z, M, Radius and form provenance;
 else (no)
  :Emit empty vector and resolved=false;
 endif
 :Attach entity ID and optional meaning neighbors;
endwhile (no)
:Compute usage as whitespace-separated word counts, minimum one per retained input;
:Return embedding list;
stop''',[endpoint],'The response vector here is five form values, not evidence that a conventional learned embedding model was run. Input trimming/filtering and usage counting belong to this HTTP contract. Concurrency eight is a literal wrapper limit. Exceptions from embedding tasks propagate; other tasks in the same group may already be running. Native form and meaning retrieval remain nested.',[])
    add('monorepo-forward-input','Monorepo — native forward input and seed','''start
if (Invocation-context argument supplied?) then (yes)
 :Require non-null 16-byte context, otherwise PostgreSQL ERROR;
endif
if (Prompt null or empty?) then (yes)
 :Initialize materialized set result; return no rows;
 stop
endif
:Default hops to 2 and fanout to 8;
if (Either negative?) then (yes)
 :PostgreSQL ERROR;
 stop
endif
:laplace_prompt_input derives prompt context and seeds;
:Collect non-null semantic seeds through deduplicating frontier helper;
if (Prior discourse supplied?) then (yes)
 :Validate ID array; preserve ordered discourse separately from seeds;
endif
:Construct 12-argument native walk call;
:Pass context, step/stride/spread/topK and seed arguments;
if (Seed omitted?) then (yes)
 :Convert exact observation bytes to UTF8;
 :BLAKE3 hash; interpret first eight digest bytes in network order;
 :Preserve those bits as signed seed; free converted bytes if allocated;
else (explicit)
 :Preserve explicit seed, including zero or negative;
endif
:Pass semantic frontier, fanout and output-relation projection;
:Invoke walk_continuations with prompt input, hops, trace flag,
invocation context and separate discourse array;
:Return native result;
stop''',['Laplace/extension/laplace_substrate/src/trajectory_generate.c'],
      'forward_prompt and the pg_laplace_forward_trace wrapper were reviewed. walk_continuations orchestration is expanded in related views. laplace_prompt_input, frontier helper, array validation and nested typed-state semantics remain deeper boundaries. The omitted-seed law differs from modular pull’s time-derived default; family behavior must not be merged.', ['20.1','20.9'])
    add('monorepo-chat','Monorepo — chat request and response branches','''start
:Read JSON; validate model, messages and newest nonempty user prompt;
:Reject unsupported parameters and invalid lengths, temperature or option combinations;
:Resolve tenant/user/session scope; resolve operator language;
:Validate tenant-scope request and optional installed shape;
if (Validation/scope fails?) then (yes)
 :Return corresponding request error;
 stop
endif
if (Turn witness unavailable?) then (yes)
 :Return 503 witness_unavailable;
 stop
endif
:Require quote through billing service;
if (Gate disallows?) then (yes)
 :Return 402 with quote/service detail;
 stop
endif
:Consume supplied quote if present;
:Catalog authenticated user's conversation;
:Set session response header; resolve conversation source IDs;
:RecordPromptAsync; retain occurrence key;
if (No inspection options and not tenant-scoped?) then (yes)
 if (Stream requested?) then (yes)
  :Begin SSE; send assistant role;
  :Consume ForwardTurnObservedStreamAsync;
  :Retain all events; append/send only emit events with non-null surface;
  :RecordResponseAsync with accumulated text;
  :BuildForwardProof from same events; send terminal chunk;
  :On non-cancellation exception in stream try, send stream_failed error;
  :Send done and return empty HTTP result;
 else (no)
  :Consume same observed event stream into events and emitted text;
  :RecordResponseAsync, passing null for empty content;
  :Build proof and performance; return JSON completion;
 endif
else (inspection or tenant scope)
 if (Tenant scope?) then (yes)
  :ConverseTenantScopedAsync using tenant prompt/response source IDs;
 else (global inspection)
  :ConverseAsync;
 endif
 :Join reply rows with newline; empty rows give empty content;
 :RecordResponseAsync, null when no rows;
 if (Stream requested?) then (yes)
  :Begin SSE after query and witnessing;
  :Send role, reply rows, terminal performance, done;
 else (no)
  :Return JSON with per-row provenance and optional witness total;
 endif
endif
stop''',[endpoint],'This handler ignores resent earlier messages when choosing the new observation; prior discourse comes through the session. Quote consumption, catalog writes and prompt witnessing precede generation, with no combined rollback shown here. Non-cancellation errors are caught locally only inside the direct observed-stream try. Scope, billing, witness, inference, realization and middleware semantics are separate nested boundaries.', ['20.1','20.10','20.11','21.1','22.6'])
    add('monorepo-turn-scope','Monorepo — tenant and conversation scope','''start
:Resolve tenant context through ITenantResolver;
if (Tenant identifier invalid?) then (yes)
 :Return invalid_tenant error;
 stop
endif
:Choose session key: body, then X-Laplace-Session, then fresh s-GUID;
if (Session identifier invalid?) then (yes)
 :Return invalid_session error;
 stop
endif
if (Tenant claims contain parsable user_id GUID?) then (yes)
 :Replace supplied user key with authenticated GUID;
else (no)
 if (Supplied user identifier invalid?) then (yes)
  :Return invalid_user error;
  stop
 endif
endif
:Derive SessionId from tenant and session key; return TurnScope;
stop
legend
CatalogConversationAsync writes only for authenticated user GUIDs.
Its title collapses whitespace and truncates to 160 characters before UpsertConversationAsync.
These checks are identifiers and scope plumbing, not the tenant resolver's authentication proof.
endlegend''',[endpoint],'ConversationContent identity derivation, tenant resolver trust, store authorization and session ownership enforcement remain nested. The source wrapper alone cannot establish the whole access-control policy.',[])
    add('monorepo-observed-sql','Monorepo — observed forward SQL and realization','''start
:C# opens Npgsql connection and catalogued query with cancellation;
:Bind prompt, session, steps, stride, spread and topK;
:converse.forward_turn_observed materializes session occurrences;
:Batch resolve message content IDs; compute prompt_tree root;
:Find newest session occurrence matching current prompt content;
:Build ordered history of prior turn IDs and their distinct content IDs;
:Exclude that newest matching current-input occurrence;
:Materialize one generation.forward_program invocation over prompt and history;
:Select latest terminal complete/unresolved/ambiguous/budget_exhausted row;
if (Terminal completion and semantic_act_id present?) then (yes)
 :Collect emit identities and separators ordered by step/round;
 :Realize exact identities in inferred prompt language;
 :Render separator identities and join per emitted item;
else (no)
 :No realized emit surfaces from this gate;
endif
:Return all execution events with available surfaces;
:Add realize events for successfully joined emit rows;
:Catalog query adds labels/hex IDs and projects 37 columns;
:C# maps rows to ForwardObservedEvent and yields;
:Dispose reader, command and connection on exit;
stop
legend
SQL source binds generation.forward_program to native pg_laplace_forward_trace.
Prior-frontier is a legacy ABI name for ordered discourse, not prompt semantic seeds.
Native execution, prompt admission and realization internals remain separate.
endlegend''',[
      'Laplace/app/Laplace.Endpoints.OpenAICompat/SubstrateClient.ForwardObserved.cs',
      'Laplace/app/Laplace.Substrate/Crud/Npgsql/NpgsqlSubstrateReads.ForwardObserved.cs',
      'Laplace/engine/core/src/sql_catalog.def',
      'Laplace/extension/laplace_substrate/sql/functions/generation/walk_text.sql.in',
      'Laplace/extension/laplace_substrate/sql/functions/generation/walk_continuations.sql.in'],
      'Materialized SQL execution precedes the returned event projection; an HTTP streaming interface does not prove incremental native execution. The outer catalog ordering distinguishes route/emit/other and does not preserve the full inner event-stage ordering among ties. Nullable numerical columns are often mapped to zero by the C# row reader. Source declarations have not been compared to a live installed catalog.', ['20.1','20.10','20.11'])
    add('monorepo-forward-proof','Monorepo — response proof assembly','''start
:Receive events from the invocation that produced the reply;
:Select last complete/unresolved/ambiguous/budget_exhausted event;
if (No terminal event?) then (yes)
 :Return null proof;
 stop
endif
:Project resolve/couple/orient/route/propose/scan/compose/steer/select/emit/realize events;
:Retain event dimensions, supports and routing rounds;
if (Caller reports response witnessed?) then (yes)
 :Append synthetic witness trace event using terminal context;
endif
:Build proof with session, prompt occurrence key, program ID and semantic-act ID;
:Include fingerprint, completion/disposition, obligations, output count and prior discourse;
:Return proof;
stop''',[endpoint],'The witness event is appended by managed response-proof assembly after the caller’s RecordResponseAsync returns. It is not another native forward execution. This wrapper neither verifies the fingerprint nor proves durable replay from all consumed evidence; RecordResponseAsync skips the write for empty replies, so the synthetic event alone does not establish a response deposit. Native receipt semantics remain separate.', ['20.11'])
