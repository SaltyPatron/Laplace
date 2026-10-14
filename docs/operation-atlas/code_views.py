"""Code construction through shared forward, grammar composition and tool witnesses."""
def add_code_views(add):
    root='Laplace/app/Laplace.Endpoints.OpenAICompat/'
    def view(id,title,body,files,note):
        add('monorepo-code-'+id,'Monorepo — '+title,body,[root+f for f in files],note,[])
    view('entry','code HTTP and chat selector','''start
if (POST /v1/chat/completions?) then (yes)
 :Buffer and parse JSON; reset request stream;
 if (Malformed JSON or not code model or code_language absent?) then (yes)
  :Continue ordinary chat pipeline;
  stop
 endif
 :Reject streaming and unsupported nonempty controls;
 :Require messages with string role/content and only user roles;
 :Require exactly one nonblank user message;
else (POST /v1/code/completions)
 :Decode typed request; require exact code model and nonblank prompt;
endif
:Normalize language alias and require installed Tree-sitter grammar;
:Validate step/window/top-k/attempt ranges and finite nonnegative temperature;
:Apply chat.completions quote gate; consume quote and record usage before construction;
:Call CodePlayerService.GenerateAsync;
if (Verified?) then (yes)
 :Return code response or assistant chat choice;
else (no)
 :503 when toolchain unavailable; otherwise 422;
 :Dedicated code response retains attempt receipts;
 :Chat response returns error diagnostic;
endif
stop''',['CodeModelChatMiddleware.cs','CodeEndpoints.cs','CodePlayerService.cs'],'Both transport bodies reviewed. Installed grammar does not imply an installed verifier. Chat permits additional blank user messages but exactly one nonblank one. JSON parse errors fall through; valid non-object JSON/property-type errors are not all caught by its JsonException catch. Code construction receives no tenant/session argument from these calls; billing resolution remains separate.')
    view('construct','shared forward, candidate admission and tool testimony','''start
:Normalize grammar modality; clamp attempts to 1..8;
:Create consensus writer; apply CodePlayer and ToolchainWitness bootstraps;
while (Attempt remains?) is (yes)
 :Call shared generation.forward_text with original prompt and prior candidate root feedback;
 if (No nonblank candidate?) then (yes)
  :Return unresolved or exhausted with earlier attempt receipts;
  stop
 endif
 :Require candidate UTF-8 <= 2 MiB;
 :Parse native source AST for diagnostics;
 :Compose candidate again under grammar recipe; add structural witnesses;
 :Collect DEFINES subjects contextualized by candidate root;
 :Persist candidate change before tool verification;
 :Run configured toolchain verifier;
 :Compose canonical tool receipt and tool identity content;
 :Attest HAS_RESULT for candidate and each definition,
confirming only when tool.Verified;
 :Persist tool-witness change; append attempt receipt;
 if (Tool unavailable or verified?) then (yes)
  :Return corresponding failure or success;
  stop
 endif
 :Clear feedback; retain this candidate root for next shared-forward attempt;
endwhile (exhausted)
:Return toolchain_rejected_all_candidates;
stop''',['CodePlayerService.cs','CodeToolchain.cs'],'Complete service reviewed. Bootstrap, candidate apply and tool-witness apply are separate writes; later failure retains earlier effects. Retry feedback contains only the latest candidate root, whose persisted result testimony can be discovered by the shared program. Syntax parsing, grammar composition internals and source-prior law remain separately inspected boundaries. No code generation or toolchain execution was performed for the atlas.')
    add('monorepo-code-forward','Monorepo — code candidate through shared forward SQL','''start
:Open substrate connection;
:Bind prompt, steps, stride, spread, top-k and feedback root array;
:SELECT from generation.forward_text with null seed, fixed 4/16 parameters
and ordered feedback as prior_frontier;
:Order result by step;
:Concatenate nonnull entity text; retain whether any rows occurred;
:Return null for no rows, otherwise assembled text;
stop''',['Laplace/app/Laplace.Substrate/Crud/Npgsql/NpgsqlSubstrateReads.CodePlayer.cs'],'Entire ForwardCodeAsync adapter reviewed. It does not select a language-specific model or source family; requested modality is not supplied to this SQL call. The code service separately uses modality for grammar composition and verification. Native shared forward and realization paths remain linked rather than inferred from the adapter comment.',[])
    view('verify','verification definition and configured tools','''start
:Require nonblank source and modality;
if (JSON?) then (yes)
 :Use System.Text.Json parsing; no subprocess;
else (other)
 :Resolve finite configured compiler/syntax-check invocation;
 if (No configured verifier?) then (yes)
  :Return unavailable, exit 127;
  stop
 endif
 :Create unique temporary directory; write candidate source;
 :When needed write bootstrap project and run preparation;
 if (Bootstrap unavailable, timed out or nonzero exit?) then (yes)
  :Return failed bootstrap receipt;
 else (ready)
  :Run verifier invocation;
 endif
 :Finally attempt recursive temporary-directory cleanup; swallow cleanup error;
endif
:Verified = tool available AND Tree-sitter syntax complete
AND no timeout AND exit code zero;
:Include AST diagnostics; normalize and truncate diagnostic strings;
:Serialize laplace.code-toolchain/v2 receipt;
stop''',['CodeToolchain.cs'],'Complete verifier and finite tool map reviewed: Python, C/C++, JavaScript/TypeScript, Rust, Go, Bash, Ruby, PHP, Java, C#, CUDA, Fortran, Swift, Zig, LLVM, ISPC, NASM and assembly, plus in-process JSON. Julia/Kotlin aliases can have a grammar but no verifier here. C# restore and build are separate process calls; Java disables annotation processing. Verified means these syntax/compile checks, not behavioral test success.')
    view('process','tool process, timeout and caller cancellation','''start
:Build ProcessStartInfo with argument list, no shell/window and redirected output;
:Set npm no-auto-confirm and dotnet telemetry/no-logo flags;
:Start tool in temporary directory;
if (Win32 start error or null process?) then (yes)
 :Return unavailable, exit 127;
 stop
endif
:Start complete stdout/stderr reads;
:Wait with caller-linked token and 30-second timeout;
if (Cancellation caused only by timeout?) then (yes)
 :Attempt kill entire process tree;
 :Await both output streams; return timed-out exit 124;
elseif (Caller cancellation?) then (yes)
 :Exception propagates; this branch does not explicitly kill process;
 :Dispose process wrapper; outer verifier attempts directory cleanup;
 stop
else (normal exit)
 :Await both output streams; capture exit code;
endif
:Normalize diagnostics; trim each to 16384 characters after full read;
stop''',['CodeToolchain.cs'],'Entire RunAsync reviewed. DiagnosticLimit bounds returned strings, not memory consumed by ReadToEndAsync. Timeout kill errors are swallowed and stream waits have no separate bound. Caller cancellation bypasses the timeout-only kill catch; disposing Process is not explicit termination. No OS isolation or memory quota is configured in this process wrapper.')
