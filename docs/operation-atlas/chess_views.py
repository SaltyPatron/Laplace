"""Chess interfaces, ownership, host lifetime and service orchestration."""
def add_chess_views(add):
    endpoint='Laplace/app/Laplace.Endpoints.OpenAICompat/EndpointMappings.Chess.cs'
    runtime='Laplace/app/Laplace.Endpoints.OpenAICompat/ChessRuntimeService.cs'
    service='Laplace/app/Laplace.Chess/Service/ChessEngineService.cs'
    def view(id,title,body,files,note):
        add('monorepo-chess-'+id,'Monorepo chess — '+title,body,files,note,[])
    live='Laplace/app/Laplace.Chess/Service/ChessLiveGameHost.cs'
    retained='Laplace/app/Laplace.Chess/Service/ChessPlayOperation.cs'
    lab='Laplace/app/Laplace.Chess/Service/ChessLabService.cs'
    lab_ingest='Laplace/app/Laplace.Chess/Service/ChessLabService.Ingestion.cs'
    pgn='Laplace/app/Laplace.Chess/Service/ChessPgnIngestor.cs'
    chunk='Laplace/app/Laplace.Chess/Service/ChessPgnChunk.cs'
    view('lab-lifecycle','job ownership, retained files and cleanup','''start
:On StartJob prune terminal history and old orphan workspaces;
:Count Running jobs; reject if count reaches four;
:Create process-local job, 4096 DropOldest event channel and cancellation source;
:Publish Running under slot lock; launch worker;
if (Requester supplied?) then (yes)
 :Write operation.json containing operation, kind, requester, start time and host;
 :Register file in in-memory artifact dictionary;
endif
:Dispatch selected runner;
:On exception/cancellation finish Failed/Cancelled;
:Finish updates job state; publishes done; completes channel and terminal;
:Stop requests cancellation; it does not await worker completion;
:Next start retains newest 32 terminal jobs, except active ingests;
:Delete eligible removed workspaces;
:Delete unknown canonical-GUID workspaces older than 24 hours;
stop''',[lab],'Complete ChessLabService reviewed. Capacity count and insertion are not one lock, so four is not an atomic concurrency limit. Event readers share a consuming channel, not a broadcast log. Job registry is process-local; operation.json is not loaded into it at startup here. Cleanup rejects reparse points but uses directory last-write time and no durable job registry. The play subdirectory is not a canonical GUID, so this cleanup does not retain/prune play-session files. Runner completion/cleanup remains runner-specific.')
    view('lab-reingest','retained PGN admission and separate receipt publication','''start
:Find terminal job under lock; increment ActiveIngests;
:Acquire per-job ingestion gate; locate retained games.pgn;
if (Completed Cutechess job with existing experiment?) then (yes)
 :Allocate new ingest receipt name before validation;
 :Read experiment; hash its UTF8 text; initialize recording measurement;
 :Bind PGN to retained experiment;
endif
:Attach shared host writer; ingest PGN through common ingestor;
if (Measurement exists?) then (yes)
 :Verify PGN unchanged and experiment still matches;
 :Mark measurement completed;
endif
:Determine fresh/mixed/replay/repaired/existing-or-repaired disposition;
:Prepare return result;
:Finally, when receipt allocated, serialize .pending then move to final JSON;
:Register receipt artifact; prune old ingestion receipts to 32;
:Always release gate if acquired and decrement ActiveIngests;
stop''',[lab_ingest,lab,pgn],'Complete retained ingestion method and pruning reviewed. Post-ingest input verification and receipt serialization happen after database effects; neither rolls them back. Receipt writing in finally has no caller cancellation token and can throw, overriding a pending return or exception. No explicit Flush(true) is used for these receipts. PGN/experiment measurement is conditional, not universal to every job. Replay/no-write acceptance depends on nested measurement code; names alone do not establish it.')
    view('pgn-admission','PGN source, shared admission, repair and exact readback','''start
:Owned ingestor creates synchronous writer, or attached ingestor borrows live-host writer;
:Bootstrap source owners and canonical names;
:Parse optional experiment; acquire process-wide ingestion semaphore;
:Read/validate/parse to next full parse window or final remainder;
:Skip parser-rejected games; count rejections when measured;
while (Nonempty parsed window available?) is (yes)
 :Probe recording-witness novelty once for this window;
 while (Window games remain?) is (yes)
  :Compose complete games into Record/Analyze/Repair/RepairAnalyze builders;
  :Close after staged/modelled bytes cross share, or exact replay game count;
  :Materialize novel record and calculated changes;
  :For existing games probe exact testimony and persisted move trajectory;
  :Suppress existing testimony; retain structural repair when trajectory differs;
  :Add missing experiment testimony;
  if (No-writer verification and changes present?) then (yes)
   :Throw before ApplyMany; finally dispose owned stages/builders and release gate;
   stop
  endif
  :ApplyMany when changes present;
  :Read back exact start position and ordered move IDs unless no-writer mode;
  :Update observed cache; perform measurement-specific exact readback/scope checks;
  :Flush corpus evidence if enabled; only then remove committed novelty;
  :Dispose owned native stages and builders;
 endwhile (done)
 :When required, verify full window witness, rules, result and hydrated moves;
 :Read/validate/parse next window or final remainder, skipping rejected games;
endwhile (done)
:Return parsed/novel/applied/repaired counts; release semaphore in finally;
stop''',[pgn,chunk],'Complete ChessPgnIngestor and ChessPgnChunk reviewed. Admission is per composed chunk, not one transaction for the whole file. Byte closure happens after an entire game; this estimate is not a hard peak-memory bound. Shared-host producers do not all hold this semaphore. Existing games still run composition and calculated owners before filtering missing testimony. Exact readback can fail after ApplyMany committed; retries depend on witness/structure repair. Applied equals novel count in this return path, not total changes or repaired games. Complete-source unmeasured mode additionally hydrates rules/result/moves after each parse window. Deep decomposer, measurement and common-writer operators remain separate evidence.')
    view('player-profiles','provider facts and cross-provider identity testimony','''start
:Empty input returns; otherwise acquire process-wide ingestion gate;
:Bootstrap distinct provider sources; register canonical-name union;
while (Profile remains?) is (yes)
 :Validate FIDE identifier when applicable;
 :Select source/trust; derive player from FIDE display name or online provider ID;
 :Compose aliases, provider identifier, biography, title, federation, links and ratings;
endwhile (done)
if (Exactly one FIDE profile supplied?) then (yes)
 :For every supplied online profile, compose association context;
 :Attest CORRESPONDS_TO online player to FIDE player under online source;
endif
:Build changes; probe attestation IDs grouped by relation type;
:Suppress already-present testimony; mark changes CountsAsUnit false;
:ApplyMany through shared writer; return profile/player/link counts;
:Release gate in finally;
stop''',[pgn],'Provider facts are separately witnessed. This method interprets co-supplying exactly one FIDE profile and online profiles as explicit association; it does not independently validate that each online profile is the same person. Matching names alone do not create the association branch. Counts report supplied/planned profiles and links, not proven novel identities. The presence probe and subsequent shared-writer apply are separate operations; cross-process race handling belongs to the writer.')
    view('buffer','live game observations before deposit','''start
:Create ingest datasource, synchronous writer and consensus accumulator;
:Bootstrap chess sources; register canonical names;
:OpenGame stores an ephemeral session under supplied event handle;
while (Ply/clock/analysis arrives?) is (yes)
 :Find open session; acquire shared write gate;
 if (Ply?) then (yes)
  :Resolve legal UCI/SAN from pre-move board;
  :If unresolved, search unique legal move producing supplied post-board;
  :Append move, resolved ply, move identity and position identities;
 elseif (Nonnegative clock?) then (yes)
  :Store remaining milliseconds by ply;
 else (analysis)
  :Store supplied analysis by ply;
 endif
 :Release gate; no database apply in these methods;
endwhile (done)
:CompleteGame performs later deposition;
:Abandoning captured scope removes only its exact session, without result write;
stop''',[live],'Reviewed live-host creation, open/capture/abandon and record methods. RecordPlyAsync accepts a ply argument but appends rather than indexing/deduplicating by it. When a supplied UCI/SAN resolves, ResolveMove does not additionally verify that supplied toKey equals that move result. Caller paths generate their own toKey, while lower-level direct callers remain a separate validation boundary.')
    view('deposit','completed game composition and witness apply','''start
:Find open game; acquire shared write gate;
:Create change builder with chess source priors;
if (Position sequence nonempty?) then (yes)
 :Compose ordered line from start position and moves;
 :Calculate playing occurrence identity; add entity and PLAYS_LINE testimony;
 :Emit move nodes and exact line trajectory; require start identity agrees;
 :Append move outcomes, line result, player identities/relations and player results;
 :Append metadata, clocks and retained ply analysis;
 :Derive witnessed-game analysis and transition testimony;
 :Add transition marker and position-outcome trajectory;
 :If tablebase prober exists, derive its game evidence;
endif
:Build change; await consensus writer ApplyAsync;
:Mark transition cache observed; invalidate learned evaluator;
:Increment completed count; remove open game; return playing identity;
:Finally release write gate;
stop''',[live],'Reviewed CompleteGameAsync orchestration including recovery change 6cbd9cc63. The playing entity is added on each composition; the old EntityEmitted flag is removed. Builder and native intent stages are disposed. No transaction spans database apply and subsequent cache/counter/session updates. Detailed ChessGraph/ChessAnalyze/outcome/tablebase builders remain nested; synchronous writer configuration is not crash-recovery acceptance.')
    view('retained','play file, replay and completion recovery boundary','''start
if (Save or CommitMove?) then (yes)
 :Serialize requester, context, ordered moves, analysis and state to unique temporary file;
 :WriteThrough; Flush to disk; move over retained JSON path;
 :Then publish updated operation and in-memory move state;
elseif (Restore missing live session?) then (yes)
 :Read retained operation under recovery lock;
 :Require ongoing, or explicit resumeRecording with CanRetryRecording;
 :Replay every move legally from stored/default start board;
 :Require final replay FEN agrees with retained FEN;
 :Recreate play session and, when recording, live-game buffers and analysis;
else (Finish)
 :Restore session if needed;
 :For retry require original retained result and adjudication agree;
 if (Recording enabled?) then (yes)
  :Retain adjudication; save state recording / in_progress;
  :Rebuild game buffer from retained moves and analysis before apply;
  :CompleteGameAsync deposits substrate change;
  :Retain returned playing ID;
 endif
 :Save completed state with recorded/empty/disabled disposition;
 :On exception save failed state and rethrow;
 :On success remove live play session;
endif
stop''',[live,retained],'Reviewed recovery change 6cbd9cc63. CanRetryRecording requires recording enabled, recording/failed state, retained adjudication and recognized result. Explicit recording retry reconstructs the buffer and preserves outcome; normal play recovery remains ongoing-only. File replacement and database deposition still have no shared transaction. Retry idempotence after an acknowledged or uncertain commit depends on the shared writer; this diagram does not assert runtime acceptance. An empty game can complete without nonzero playing identity.')
    view('ownership','play-session and lab ownership','''start
:Resolve request tenant;
if (Play action?) then (yes)
 :Require browser user ID or API-key prefix principal;
 :Read retained operation; require exact requester equality;
 :Completed finish returns its retained operation;
 :Require ongoing, or finish with retryable recording;
 :Get live runtime and session; require exact requester equality;
 :Acquire session gate;
 :Reread retained operation; recheck completed/ongoing/retry eligibility;
 :Recheck same live session instance still registered;
 if (Expected FEN supplied and differs from live session board?) then (yes)
  :Return 409 play_position_changed;
 else (current)
  :Run move/bestmove/finish or explicit recording retry;
 endif
 :Finally release session gate;
else (lab access)
 :Require nonblank tenant equals job requester tenant;
 :Allow same-workspace job listing, reads, stop, artifacts or ingest;
endif
stop''',[endpoint,runtime,service],'Endpoint recovery change 6cbd9cc63 reviewed. Play uses exact OperationPrincipal equality; lab access uses tenant equality. Browser principals retain provider, API-key principals use display prefix, and header/dev tenants produce no RequestPrincipal. Retained state is now reread inside the session gate; completed finish is idempotent at HTTP level. POST /chess/play/{sessionId}/recording/retry requires a retryable retained recording. Shared-writer retry semantics remain a separate boundary.')
    view('runtime','lazy shared live-host lifetime','''start
:StartAsync creates lifetime cancellation source; no database initialization;
if (GetAsync?) then (yes)
 :Require started and uncanceled lifetime;
 :Under lock clear faulted/canceled initialization;
 :Share one InitializeAsync task using lifetime token;
 :Caller awaits shared task with its own cancellation;
 :Publish created host only if lifetime remains active;
 :If canceled before publication, initializer disposes its own host;
elseif (StopAsync?) then (yes)
 :Cancel lifetime; detach initialization task;
 :Await initialization with stop token; swallow errors;
 :Atomically detach and dispose published host;
else (DisposeAsync)
 :Detach lifetime and initialization; cancel lifetime;
 :Await initialization without separate wait timeout; swallow errors;
 :Dispose published host and cancellation source;
endif
stop''',[runtime],'Entire runtime wrapper reviewed. Caller cancellation stops its wait, not the shared initialization. Failed initialization is retried by a later caller. Host creation, witness writer and host disposal semantics remain nested. No runtime was started for this atlas.')
    view('read-search','legal moves, position history and search','''start
:Lazily initialize modality engine under gate;
:Use configured read-only datasource, else obtain live-host datasource;
:Load perfcache; create shared modality engine; schedule learned-PST refresh;
if (Legal moves?) then (yes)
 :Parse FEN; detect terminal/check; score legal candidates through modality engine;
 :Return moves ordered by effective standing;
elseif (Apply move or best move with nonempty history?) then (yes)
 :Replay moves from standard initial position; stop at first illegal move;
 :Use replayed state instead of supplied FEN;
else (no history)
 :Parse supplied FEN;
endif
if (Search requested and state nonterminal?) then (yes)
 :Rent/create Search with optional substrate root bias/board evaluator and tablebase probe;
 :Think synchronously with depth clamped 1..12;
 :Extract best move/PV; apply move; detect terminal and motifs;
 :Return engine to pool; format side-to-move and white scores;
endif
:Return response; terminal evaluation uses draw zero or win +/-30000;
stop''',[endpoint,service],'Complete service read/search orchestration reviewed. Search internals, legal rules and substrate evaluators remain separate. Request cancellation is used for initialization but is not passed to these synchronous Think calls. Nonempty history ignores FEN and truncates replay at an invalid move. BestMoveAsync ignores its temperature argument; HTTP bestmove also does not forward its Temperature field.')
    view('explore','legal continuation and player evidence comparison','''start
:Initialize read engine; parse FEN and enumerate legal moves;
:Under compose lock compute canonical position and legal child IDs;
:Read MOVE standings with candidate limit at least legal move count;
:If player supplied, separately read player move statistics;
:Keep only rows whose child matches a legal move;
:Attach standing/RD/witnesses and player games/score;
:Add player-only legal continuations with zero standing fields;
:Rank by witness count without player, else player games;
:Then rank by effective standing; take requested limit;
:Return original FEN, player label and move rows;
stop''',[endpoint,service],'Complete ExploreAsync reviewed. Consensus and player-stat reads are separate tasks without a shared transaction here. Legal move generation constrains returned rows. Player-only rows carry zero aggregate-standing fields, not fabricated consensus. Canonical player alias resolution and concrete SQL helpers remain nested.')
    view('play','interactive moves and separate recording effects','''start
:Start play: initialize engine and live host; create session with requester;
:Human canonical player ID remains null; Laplace player ID supplied for black;
:Replay optional initial legal moves, stopping at first illegal input;
:Append move list and, when recording, buffer each ply in live host;
:Set session state; save retained operation;
while (Authorized serialized play action?) is (yes)
 :Use live session state; validate legal requested move or search best move;
 :Calculate next state, outcome and motif/search analysis;
:CommitMove saves operation file and updates session before live-host recording calls;
 if (Recording enabled?) then (yes)
  :Record ply; then record analysis;
 endif
 if (Terminal or explicit finish?) then (yes)
  :Ask live host to finish with outcome/adjudication;
 endif
endwhile (done)
:Return state/receipt;
stop''',[endpoint,service,live,retained],'Complete ChessEngineService play methods reviewed and live-host recording boundary traced. Per-ply recording calls update memory buffers; database deposition happens at finish. Retained operation-file saves and later game deposition are separate effects. Explicit finish maps unknown status strings to draw. HTTP default records games; authenticated account identity is not silently promoted to canonical chess-player identity.')
    view('training','self-play loops and learning boundary','''start
if (StartTraining?) then (yes)
 :Under statistics lock reject already-running task;
 :Set temperature/weight/max plies; start background task with own token;
endif
:Initialize read engine and write host;
while (Not canceled and game target remains?) is (yes)
 if (Strong self-play?) then (yes)
  :Random legal opening; then depth-limited Search;
  :Record from/to state keys and mover;
  :Max plies yields adjudicated draw;
  :Project final outcome to each mover edge;
 else (ordinary)
  :PlayGameAsync with random generator, temperature and max plies;
 endif
 :Await LearnGameAsync(edges, adjudicated);
 :Only afterward update game/win/draw statistics and progress callback;
 :Background loop logs noncancellation failures and retries;
endwhile (done)
:StopTraining requests cancellation without awaiting completion;
:Dispose awaits background training then disposes initialization gates;
stop''',[endpoint,service],'Complete service training methods reviewed. Strong self-play checks cancellation between games but does not pass it into its per-move Think calls. Background noncancellation failures have no backoff in this loop. maxGames <= 0 means unbounded background training. Existing cached write host is reused even when StartTraining changes _trainWeight. LearnGameAsync semantics remain nested; game count is incremented only after that call returns.')
    view('lab-http','lab jobs, streams and retained artifacts','''start
if (Catalog or preview?) then (yes)
 :Read installed connection profiles; expose public metadata;
 :Preview selects optional Laplace / Stockfish profile by name and implementation;
 :Validate credential-free WSS endpoint and installed absolute connector/config paths;
 :Reject local Syzygy override for remote Stockfish;
 :Build arguments and probe configured connector presence;
elseif (Start job?) then (yes)
 :Parse job kind/config; reject managed LichessBot job with 409;
 :Validate Cutechess-specific configuration and installed profile selection when applicable;
 :Require browser/API-key requester; call lab.StartJob;
else (existing job)
 :Require same tenant ownership; otherwise 404;
 if (Events?) then (yes)
  :Stream EventReader channel as flushed SSE;
 elseif (Terminal?) then (yes)
  :Stream terminal lines after sequence; disable proxy buffering;
 elseif (Transcript download?) then (yes)
  :Serve retained transcript file if present; otherwise ring snapshot text;
 elseif (Named artifact?) then (yes)
  :Look up name in job artifact dictionary; require existing file; serve by extension;
 elseif (Ingest retained PGN?) then (yes)
  :Require games.pgn; reject Pending/Running job;
  :Call IngestArtifactAsync and return result;
 else (read/stop/list)
  :Return or stop owned job; list filters to workspace;
 endif
endif
stop''',[endpoint,'Laplace/app/Laplace.Chess/Service/ChessEngineConnections.cs'],'Complete lab HTTP routes and installed-profile selector reviewed. EventReader and replaying terminal are different transport sources; broadcast/replay guarantees depend on service implementation. Artifact paths come from retained job metadata, not arbitrary requested filesystem paths. Profile selection accepts installed names, not caller-supplied endpoints or credentials. The probe checks connector file presence, not worker reachability, authentication or match acceptance. Start validates the selection but passes original config to StartJob; the downstream match runner remains a separate boundary. Lichess legacy start/stop return 410 directing to shared selection controls.')
