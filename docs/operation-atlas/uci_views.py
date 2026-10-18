"""Connector lifecycle traced from its three C# source files."""
def add_uci_views(add):
    def view(name,title,body,files,note):
        add('uci-'+name,'UCI connector — '+title,'start\n'+body+'\nstop',['Laplace-UCI/src/Laplace.Uci/'+f for f in files],note+' No engine process or connector endpoint was started.',[])
    view('dispatch','configuration and command dispatch','''
:Read command, default help;
if (help?) then (yes)
 :Print usage; return 0;
 stop
endif
:Resolve configuration path: argument, environment, then application config;
:Read and validate complete configuration;
:Create lifetime token; Ctrl-C requests cancellation;
switch (command)
case (catalog)
 :Print profile labels, implementation, environment, knowledge and transport;
case (connect)
 :Resolve named profile; open connection;
 :Create strict UTF8 stdin/stdout text transport;
 :Relay both directions; asynchronously dispose engine;
case (verify)
 :Resolve named profile; open connection;
 :Run handshake/start-position probe; print result; dispose engine;
case (serve)
 :Validate server token and loopback listen address;
 :Run HTTP/WebSocket service;
case (other)
 :Throw unknown-command error;
endswitch
:On normal completion, return 0;
legend
Outer catch reports exception type/message to stderr and returns 1.
Catalog descriptions are configured metadata, not verified knowledge inventories.
endlegend''',['Program.cs'],'Configuration is validated before non-help command dispatch, including catalog. The connector chooses a configured engine; it does not imply that engine belongs to the modular family.')
    view('configuration','profile and release validation','''
:Deserialize JSON; reject unmapped members and empty configuration;
:Require version 1, sessions 1..128, line bytes 1024..1048576;
while (More profiles?) is (yes)
 :Validate profile ID, identity labels and timeout 1..120;
 switch (transport)
 case (process or ssh)
  if (Published release selected?) then (yes)
   :Require process transport and no explicit executable;
   :Require absolute release/selection paths and traversal-free relative executable;
   :Read 64-hex revision; resolve executable and require file exists;
  else (no)
   :Require nonempty executable;
  endif
  if (ssh?) then (yes)
   :Require host alias without leading dash or whitespace;
   :Reject environment files, variables and working directory;
  endif
  :Validate variable names and NUL exclusions;
 case (websocket)
  :Reject published release and variable overrides;
  :Require absolute wss, or loopback ws endpoint;
  :Reject endpoint userinfo, query and fragment; require token-file path;
 case (other)
  :Throw unsupported transport;
  stop
 endswitch
endwhile (no)
:Return validated configuration;
legend
Every failed validation throws; file/JSON errors also propagate.
Token reads are separate: trim, require at least 32 characters and no whitespace.
endlegend''',['Configuration.cs'],'The release resolver validates naming/path structure and file presence; it does not hash the executable to verify the selected revision. Profile metadata is supplied by configuration.')
    view('open','transport opening','''
if (WebSocket profile?) then (yes)
 :Create ClientWebSocket;
 :Read token; set Bearer header and 15-second keepalive interval/timeout;
 :Link cancellation and configured connection deadline;
 :Connect to configured endpoint;
 if (Exception?) then (yes)
  :Dispose socket; rethrow;
  stop
 endif
 :Return bounded WebSocket connection;
else (process or ssh)
 :Construct ProcessConnection;
 :Disable shell execution; redirect stdin/stdout/stderr; hide window;
 if (SSH?) then (yes)
  :Set batch mode, strict host-key checking and connect timeout;
  :Pass configured host; quote configured executable/arguments for remote exec;
 else (local process)
  :Resolve selected executable; append argument list; set working directory;
  :Load environment files in order;
  :Apply variable overrides or removals;
 endif
 :Start process; fail if no process returned;
 :Wrap stdout/stdin as bounded text connection;
 :Pump diagnostic stderr to connector stderr;
 :Return process connection;
endif''',['Connections.cs','Configuration.cs'],'Process/SSH constructor does not take the OpenAsync cancellation token. SSH timeout is passed to ssh; WebSocket uses a linked deadline. HTTP callers select a configured profile, not arbitrary executable arguments.')
    view('relay','two-way relay and process shutdown','''
:Create linked lifetime token;
fork
 :Receive left; send right, repeatedly;
 :Stop pump on EOF, forwarded quit, cancellation or exception;
fork again
 :Receive right; send left, repeatedly;
 :Stop pump on EOF, forwarded quit, cancellation or exception;
end merge
:Task.WhenAny observes first completed pump;
:Cancel linked lifetime; await first pump;
:Finally await both pumps; suppress lifetime cancellation there;
:Relay caller disposes owned engine connection;
if (Owned connection is process?) then (yes)
 if (Process still running?) then (yes)
  :Try writing quit and close stdin; suppress IOException from that attempt;
  :Wait up to three seconds;
  if (Timeout?) then (yes)
   :Kill entire process tree; await exit;
  endif
 endif
 :Await stderr pump; finally dispose process;
else (WebSocket)
 :Abort and dispose socket;
endif
legend
Process receive throws when EOF is observed after a nonzero exit.
WebSocket disposal aborts and disposes the socket, without a close handshake here.
TextConnection disposal itself is a no-op; stream ownership stays with its caller.
endlegend''',['Connections.cs'],'The merge bar denotes the first-completion race, not waiting for both pumps before cancellation. Other exceptions propagate; disposal can itself fail. The diagram combines relay with the owning caller’s subsequent disposal.')
    view('framing','line and WebSocket framing','''
if (Sending?) then (yes)
 :Reject embedded CR/LF and UTF8 byte count above configured limit;
 if (Text connection?) then (yes)
  :Write line and flush with cancellation;
 else (WebSocket)
  :Send one text message containing UTF8 bytes;
 endif
else (receiving)
 if (Text connection?) then (yes)
  :Read chunks of at most 4096 characters;
  :Accumulate through newline; reject character count above byte limit;
  :At newline or nonempty EOF, trim trailing CR;
  :Validate CR/LF exclusion and actual UTF8 byte count;
  :Return line; empty EOF returns null;
 else (WebSocket)
  repeat
   :Receive fragment of at most min(limit, 4096) bytes;
   :Close frame returns null;
   :Reject non-text or accumulated bytes above limit;
   :Append bytes;
  repeat while (Not EndOfMessage?) is (yes) not (complete)
  :Decode strict UTF8; validate line; return it;
 endif
endif
legend
Invalid data throws InvalidDataException; strict UTF8 decoding can throw.
Cancellation and transport exceptions propagate to the owning caller.
endlegend''',['Connections.cs'],'Text receive checks characters while accumulating and exact UTF8 bytes at completion. WebSocket enforces the byte bound while accumulating. This is transport framing, not chess move validation.')
    view('verify','engine verification probe','''
:Open configured engine;
:Start 120-second lifetime deadline;
:Send uci; receive until uciok;
:Send isready; receive until readyok;
:Send ucinewgame; send isready; receive until readyok;
:Send position startpos; send go depth 1;
:Receive until bestmove;
if (Last retained line exactly bestmove 0000 or bestmove (none)?) then (yes)
 :Throw no-move error;
else (no)
 :Print profile metadata, UTC time and retained protocol lines;
endif
:Dispose engine;
legend
Until reports info-string diagnostics to stderr and retains at most 256 selected lines.
Selected lines include engine ID, best move, providers, database identity and knowledge identity.
EOF before expected response throws; timeout cancels the shared lifetime token.
The outer command catch prints errors and returns 1.
endlegend''',['Program.cs','Connections.cs'],'The timeout starts after connection opening. This is a handshake and minimal response probe: the wrapper does not independently validate move legality, chess strength or declared knowledge content. Null-move rejection is an exact string comparison.')
    view('serve','authenticated WebSocket session','''
:Validate token file; require loopback listen URI;
:Start service; create MaxSessions semaphore;
if (Request is health/live?) then (yes)
 :Return service name and live=true;
 stop
endif
:For /uci/profile, hash presented bearer and current token with SHA256;
if (Fixed-time hash comparison fails?) then (yes)
 :HTTP 401;
 stop
endif
if (Not WebSocket upgrade?) then (yes)
 :HTTP 400;
 stop
endif
if (Unknown profile or transport is not process?) then (yes)
 :HTTP 404;
 stop
endif
if (No session slot immediately available?) then (yes)
 :HTTP 429;
 stop
endif
:Open configured local process;
:Accept WebSocket; relay both directions with request cancellation;
:Dispose peer/socket and engine on scope exit;
if (Caught IO/cancellation/WebSocket exception?) then (yes)
 :Log exception type; abort HTTP context;
endif
:Finally release session slot;
legend
Other exceptions propagate through ASP.NET while finally still releases the slot.
Token read and slot acquisition occur before this try/finally boundary.
The health route does not start or verify an engine.
endlegend''',['Program.cs','Connections.cs','Configuration.cs'],'Authentication is at the connector boundary. Serve permits only configured process profiles, while CLI connect also supports SSH and WebSocket. HTTPS ingress is external to this loopback service; its implementation is not established by this diagram.')
