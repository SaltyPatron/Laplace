"""Separate modular native HTTP/MCP and Python stdio surfaces."""
def add_mcp_views(add):
    api='Laplace-MCP/native/api.c'
    py='Laplace-MCP/laplace_mcp.py'
    main='Laplace-MCP/native/main.c'
    add('mcp-native-dispatch','Modular MCP — native HTTP and RPC dispatch','''start
:Resolve Engine executable; map tier zero; allocate text decomposer;
:Bind configured IPv4 host/port; listen backlog 64;
while (Accept loop?) is (yes)
 :Accept one connection; read into 1 MiB request buffer;
 :Use Content-Length or content-length if present;
 :Parse method/path; remove query string;
 if (/mcp body contains jsonrpc marker?) then (yes)
  :Dispatch notification, initialize, ping, tools/list or tools/call;
  :For tool call, capture handler HTTP body and code;
  :Wrap body as text; isError when captured code is not 200;
  :Send outer HTTP 200 for tool results;
 else (REST or op alias)
  :Map /mcp op aliases to REST route;
  :GET health returns ok; GET surface returns static description;
  :POST routes dispatch ten handlers; unknown route returns 404;
 endif
 :Close connection;
endwhile (repeat)
stop
legend bottom
Native tools: search, embeddings, shape, forward, ingest, hop, text, translate, degrees, pull.
Server handles connections serially. No authentication check appears in this dispatcher.
Header/body parsing uses substring scans and fixed buffers; this is not a general JSON parser.
Health response does not probe the database or Engine.
endlegend''',[api,main], 'Native listener, dispatch, MCP wrapper, scalar parsers and Engine wrappers reviewed. Deployment access controls and protocol conformance are separate. Static /v1/surface text is not treated as the actual tools registry where they differ.',[])
    add('mcp-engine-process','Modular MCP — hop, text, translate, degrees and pull subprocesses','''start
:Extract required strings into fixed buffers;
:Require nonempty arguments not beginning with dash and shorter than 4096;
:Build argv for selected Engine command;
:Create pipe and fork; child creates process group;
:Merge child stdout and stderr into pipe; execv configured Engine;
:Parent reads up to 4 MiB minus one byte;
:Close read pipe; waitpid;
:Escape captured log; return exit code and log;
if (Exit code zero?) then (yes)
 :HTTP 200;
else (no)
 :HTTP 500;
endif
stop
legend bottom
on_engine supplies timeout_s=0: these five routes have no configured timeout here.
engine_run has a timeout branch using process-group SIGTERM, but still blocking waitpid.
No shell command is built. Native Engine semantics are linked separately.
Output capacity ends reading; truncated output is not explicitly marked in this response.
endlegend''',[api], 'Five argument wrappers and engine_run reviewed. Fixed-buffer jstr extraction can truncate before argument validation. Process invocation success, child exit zero and semantic acceptance are separate observations.',[])
    add('mcp-ingest','Modular MCP — filesystem ingest subprocess','''start
:Extract path; require leading slash;
:stat path; return 400 if absent;
:Create pipe; fork;
:Child redirects stdout/stderr and execv Engine ingest path;
:Parent reads up to 65535 log bytes;
:Close pipe; blocking waitpid;
:Escape log into another fixed 65536-byte buffer;
:Return child exit, Engine label and log;
if (Child exits zero?) then (yes)
 :HTTP 200;
else (no)
 :HTTP 500;
endif
stop''',[api], 'on_ingest reviewed. This route uses its own subprocess path, not engine_run, and has no timeout or transaction wrapper. It validates absolute path/existence, not a path-root allowlist. The invoked Engine ingest can have partial effects; those branches are linked.',[])
    add('mcp-embeddings','Modular MCP — local text identity response','''start
:Extract nonempty input into 8192-byte buffer;
:Call Native lp_text_parts with 64 constituent slots;
:Return trunk ID, tier, four fixed-point coordinate values,
Hilbert value, constituent IDs and elapsed identity time;
:HTTP 200; no database call in this handler;
stop''',[api], 'on_embeddings and name_entity reviewed. This surface emits a content/coordinate record, distinct from monorepo five-value embedding response. Parser truncation and constituent capacity belong to this API. Native decomposition is linked separately.',[])
    add('mcp-forward-shape','Modular MCP — constituent SQL forward and shape routes','''start
:Extract one input for forward, or input and other for shape;
:Name each text with up to 256 constituent slots;
:Serialize constituent IDs; reject empty or overflowing list;
:Open/reuse database connection;
if (Connection unavailable?) then (yes)
 :HTTP 503;
 stop
endif
if (Forward route?) then (yes)
 :Bind array of constituent IDs;
 :Query laplace_forward with literal 64; LIMIT 128 rows;
 :Return trunk ID, timing and aggregated observed-span rows;
else (Shape route)
 :Bind two ID lists;
 :JOIN stored entity coordinates preserving constituent ordinal;
 :Build lines; call laplace_frechet4d;
 :Return two trunk IDs, distance and timing;
endif
if (Query helper reports failure?) then (yes)
 :Attempt ROLLBACK; HTTP 500 with detail;
else (success)
 :HTTP 200;
endif
stop''',[api], 'Both handlers and their SQL strings reviewed. Forward here is the specific laplace_forward span query, not the Engine pull program or monorepo obligation-closing forward. Shape inner joins can omit missing constituent coordinates; q2 does not separately reject a SQL NULL value.',[])
    add('mcp-search','Modular MCP — trunk, constituent and attestation search','''start
:Prefer nonempty input text; otherwise require 32-hex ID;
:Name text or look up stored tier; parse optional fan;
:Read holds_above trunk with standing;
:Read trunk physicality leaf; gather trunk and holding-claim attestation ancestry;
:Obtain constituents from text parts or first stored physicality path;
while (More constituents?) is (yes)
 if (Constituent tier at least two?) then (yes)
  :Read holds_above, claim ancestry and its physicality path;
  :Decode children and look up non-codepoint child tiers;
 else (no)
  :Keep constituent/codepoint in path without holds lookup;
 endif
endwhile (no)
:Separate claims from occurrences; rank claims by lp_confidence with factor two;
:If fan positive cap returned claims and occurrences separately;
:Serialize physicality, claims, occurrences, attestations and constituent details;
:Release buffers; return HTTP 200;
stop
legend bottom
fan caps response selections after reads; it does not bound the initial database work.
Database unavailable -> 503; failed trunk physicality query -> 500.
Failed nested constituent physicality read is skipped by the local success guard.
endlegend''',[api], 'on_search orchestration reviewed. holds_above, attested, emitted nested detail and Native path decoding remain deeper boundaries. The linked db_bridge returns NULL from db_snapshot, so this service does not import the Engine command snapshot. Its separate statements need not observe one world. Bridge connection/prepare failures exit the process.',[])
    add('mcp-python-stdio','Modular MCP — independent Python stdio admit/fetch surface','''start
:Load configured liblpm; create process-local index;
if (Index unavailable?) then (yes)
 :Print tier-zero error; exit one;
 stop
endif
while (Read Content-Length framed message?) is (yes)
 :Parse JSON;
 if (Notification?) then (yes)
  :Ignore;
 elseif (initialize or tools/list?) then (yes)
  :Return protocol/server metadata or exactly admit and fetch schemas;
 elseif (tools/call?) then (yes)
  if (admit?) then (yes)
   :Index filesystem path; get last hit; fetch record summary;
   :Return ID, tier, child count, coordinate, Hilbert and path;
  elseif (fetch?) then (yes)
   :Decode exact 16-byte ID; fetch record and up to eight child IDs;
   :Return total child count plus shown child IDs;
  else (unknown)
   :Raise tool error;
  endif
  :Return content text with isError false, or catch Exception as isError true;
 else (unknown method)
  :Return method-not-found only when request ID supplied;
 endif
 :Write Content-Length response and flush;
endwhile (EOF)
stop''',[py], 'Entire Python adapter reviewed. It exposes two tools and a process-local liblpm index, separate from the native ten-tool HTTP server. Admit does not check its final lpm_fetch return code. Framing/JSON errors outside the tool-call try can terminate serving; liblpm storage and recipe internals remain nested.',[])
