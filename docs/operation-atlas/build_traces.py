"""Reviewed control paths with explicit nested-call and evidence boundaries."""
from pathlib import Path
from atlas_paths import REPOSITORIES, TOOLS, PLANTUML, JAVA
import subprocess,json,html,hashlib,textwrap
OUT=Path(__file__).resolve().parent
REPOS=REPOSITORIES
traces=[]
import argparse
lock_path=OUT/'reviewed-source-lock.json'
reviewed_hashes=json.loads(lock_path.read_text(encoding='utf-8')) if lock_path.exists() else {}
arguments=argparse.ArgumentParser(description=__doc__)
arguments.add_argument('--accept-reviewed-source',action='append',default=[],metavar='REPOSITORY/PATH')
accepted_sources=set(arguments.parse_args().accept_reviewed_source)
if not lock_path.exists() and (OUT/'traces.json').exists():
    for previous in json.loads((OUT/'traces.json').read_text(encoding='utf-8')):
        for evidence in previous['evidence']:
            reviewed_hashes[evidence['path']]=evidence['sha256']
for path in accepted_sources:
    if path not in reviewed_hashes:
        raise SystemExit('Not a previously reviewed source: '+path)
for path,expected in reviewed_hashes.items():
    source=REPOS/path
    if path not in accepted_sources and (not source.is_file() or hashlib.sha256(source.read_bytes()).hexdigest()!=expected):
        raise SystemExit('Source changed; review affected diagrams before --accept-reviewed-source '+path)
def add(id,title,body,files,boundary,operations):
    evidence=[]
    for path in files:
        p=REPOS/path
        digest=hashlib.sha256(p.read_bytes()).hexdigest()
        evidence.append({'path':path,'sha256':digest})
        reviewed_hashes[path]=digest
    traces.append({'id':id,'title':title,'body':body,'evidence':evidence,'boundary':boundary,'spec_operations':operations})
add('installed-op','Monorepo — installed operation invocation',r'''
actor Caller
participant "POST /v1/op" as HTTP
participant "Substrate client" as Client
participant "InstalledOpInvoker" as Invoke
database "Installed PostgreSQL catalog" as Catalog
database "Selected PostgreSQL datasource" as DB
Caller -> HTTP : name / named args / row cap / timeout
alt missing name
 HTTP --> Caller : 400 invalid_request_error
else platform operation requires unavailable permission
 HTTP --> Caller : 403 platform_permission_required
else negative timeout
 HTTP --> Caller : 400 invalid_request_error
else request valid at HTTP boundary
 HTTP -> Client : InvokeOpAsync
 Client -> Client : IsWritable(name) selects writable or read-only datasource
 Client -> Invoke : InvokeAsync(selected datasource, arguments)
 Invoke -> Catalog : ApiCatalogAsync(name) through ops.api
 Catalog --> Invoke : installed matching signatures and kinds
 note over Catalog
 ops.api reads pg_proc / pg_namespace.
 SQL source declarations are not the installed catalog.
 end note
 alt no exact installed name
  Invoke --> Client : error + suggested names
 else zero matching overloads or ambiguity
  Invoke --> Client : error + signatures
 else unnamed parameters with named arguments
  Invoke --> Client : error
 else exactly one usable overload
  Invoke -> Invoke : quote catalog identifiers / bind named values
  alt procedure
   Invoke -> DB : CALL with declared casts + timeout + cancellation
   DB --> Invoke : completion or exception
   Invoke --> Client : called / kind / progress-log locator
  else non-procedure kind
   Invoke -> DB : SELECT with LIMIT rowCap + 1
   DB --> Invoke : rows
   Invoke -> Invoke : normalize values / retain cap / mark truncation
   Invoke --> Client : rows + truncated_at
  end
 end
 alt PostgreSQL error outside SQLSTATE classes 08 / 53 / 57 / 58
  Client --> HTTP : OpResult.Error with SQLSTATE and message
 else availability SQLSTATE / NpgsqlException / TimeoutException
  Client --> HTTP : SubstrateUnavailableException
 else result
  Client --> HTTP : OpResult
 end
 alt result.Error
  HTTP --> Caller : 400 invalid_request_error
 else SubstrateUnavailableException
  HTTP --> Caller : 503 substrate_unavailable
 else result
  HTTP --> Caller : op.result + rows + truncated_at
 end
end
note over Client
Six operation names are writable; catalog membership alone
does not authorize mutation. SQL operation bodies are separate.
end note
''',['Laplace/app/Laplace.Endpoints.OpenAICompat/EndpointMappings.Op.cs','Laplace/app/Laplace.Endpoints.OpenAICompat/SubstrateClient.cs','Laplace/app/Laplace.Substrate/Crud/Npgsql/InstalledOpInvoker.cs','Laplace/extension/laplace_substrate/sql/functions/ops/api.sql.in'],
'HTTP, datasource selection, SQLSTATE mapping and invoker bodies reviewed. Authentication middleware, signature/value helper details and individual SQL bodies remain nested. Cancellation is propagated by the HTTP path; exceptions outside the shown catches are not assigned an invented response. No invocation executed.', [])
add('engine-ingest','Modular Engine — ingest dispatch, batches and publication',r'''
start
:Parse ingest options;
if (Highway mode?) then (yes)
 if (Incompatible arguments?) then (yes)
  :Return 2;
  stop
 endif
 :ingest_highway;
 :Return nested result;
 stop
endif
:Validate claims/entities files and source selection;
if (Invalid selection?) then (yes)
 :Return 2 or missing source 1;
 stop
endif
:Load recipes and source order;
if (All arguments are source names, or no selected files?) then (yes)
 :ingest_every with dependency closure / recipe order;
 :Return nested result;
 stop
endif
:Enumerate files; resolve recipe and source;
:Report uncovered files; order by trust and reference dependency;
if (Plan only?) then (yes)
 :Print mapping; return 0;
 stop
endif
:Open contexts; initialize source-wide fold;
if (Loading?) then (yes)
 :Check files_recorded using stored OS metadata;
endif
while (More file batches?) is (yes)
 :Respect dependency-depth barrier and batch byte budget;
 if (Oversized streamable file?) then (yes)
  :Two-pass stretch path: calculate trunk then record stretches;
  :Check known trunk / missing descendants; collect source fold;
 else (ordinary batch)
  :Decompose unskipped unknown files with OpenMP tasks;
  :Recompose bytes and compare; curated recipe skips byte comparison;
  :Accumulate mismatch count / show claims or entities if requested;
  if (Loading?) then (yes)
   :Mark staging; ingest_known; fold_events;
   :Wait prior loader before writing next batch;
   if (Windows or final batch?) then (yes)
    :load synchronously;
   else (non-Windows with next batch)
    :Fork loader; parent proceeds to next decomposition;
   endif
  endif
 endif
 if (Loader / pipe / fork reports failure?) then (yes)
  :Return 1; prior commits may remain;
  stop
 endif
 :Free batch events and reset working node table;
endwhile (no)
:Wait final loader; report recomposition and incomplete parts;
if (No-load / claims / entities?) then (yes)
 :Return mismatch ? 1 : 0;
 stop
endif
if (No mismatch and fold nonempty?) then (yes)
 :standings_write once per source claim series;
endif
:Report conflicting records / claims without provenance;
if (One whole source, no mismatch, source record exists?) then (yes)
 :Compose and load source trunk; retain witness ID / trust / lineage;
else (no)
 :Report absence of source witness trunk;
endif
if (No mismatch?) then (yes)
 :merge staged state;
 if (Source witness trunk available?) then (yes)
  :INSERT witness if absent;
 endif
endif
:Return mismatch ? 1 : 0;
stop
legend
load / standings_write / source_trunk / merge remain nested operations.
Failures in those operations return 1. This is not one atomic source transaction.
Metadata precheck conflicts with Wiki 11.2 trunk-first deduplication.
endlegend
''',['Laplace-Engine/src/ingest.c'],
'cmd_ingest top-level path reviewed. Oversized-file internals and shared load/merge/fold/native operations are summarized calls, not independently verified traces. Recomposition mismatch is detected after earlier batches may have staged state. No ingestion executed.', ['9.9','11.2','11.3','11.8','11.9','11.10','13.1'])
add('engine-turn','Modular Engine — turn, emission and persistence',r'''
start
:Parse connection, firmware, user, session, seed, read-only flag;
if (Prompt absent?) then (yes)
 :Return 2;
 stop
endif
:Load firmware / tier0; create composition contexts and database reader;
:Compose exact prompt, user and session handle;
:Read prior turns; order by recorded position; collect discourse;
if (Not --read?) then (yes)
 :load prompt content under user witness;
 if (load fails?) then (yes)
  :Return 1;
  stop
 endif
endif
:resolve_roles; initialize occurrences and trajectory;
:COUPLE occurrences, whole prompt, discourse;
:ORIENT nearest curves; determine remaining obligations;
:orient joint bindings and ambiguity; choose route centres;
:SCAN bounded centres; form chain answers;
if (Ambiguous joint orientation?) then (yes)
 :Set ambiguous disposition and emission budget to zero;
else (no)
 :Use firmware emission budget;
endif
while (Emission steps remain?) is (yes)
 :PROPOSE observed continuations and chain answers;
 if (No proposals or proposals ground no open obligation?) then (yes)
  :Set unresolved disposition;
  break
 endif
 :STEER / SELECT by grounds, continuity, confidence, hub;
 :Optional seeded selection within firmware top_within;
 :REALIZE selected entity to text;
 :Append selected ID to bounded emitted/trajectory arrays;
 :Clear supported obligations; COUPLE emitted constituent;
 if (Non-follow act reaches firmware sufficiency?) then (yes)
  :Set complete / remaining-within-bound disposition;
  break
 endif
endwhile (no)
:Report text and disposition; emission-budget status if spent;
if (Not --read and nonempty output?) then (yes)
 :Compose response and turn;
 :Create [session, turn] and [response, prompt] claim events;
 :load under user and Laplace witnesses;
 if (load fails?) then (yes)
  :Return 1;
  stop
 endif
else (no)
 :Print read-only or nothing-emitted witness disposition;
endif
:Report round trips; free field / bindings / reader / connection;
:Return 0;
stop
legend
Source bounds include MAXOCC 256, emitted 512, trajectory 4096.
Named stages do not prove universal ISA conformance.
Prompt admission precedes response; no whole-turn rollback established.
Bookkeeping claim events must be reconciled with conflict C1.
endlegend
''',['Laplace-Engine/src/program.c'],
'cmd_turn orchestration reviewed. Detailed couple/orient/scan/native ranking and database load are nested boundaries. Ambiguity disposition and candidate limits require direct drill-down; this diagram does not claim general semantic capability. No turn executed.', ['20.1','20.2','20.3','20.4','20.5','20.6','20.7','20.8','20.9','20.10','20.11','21.1','22.6'])
add('engine-replay','Modular Engine — replay standings from containment',r'''
start
:Parse --write / --tsv / --voices / --files;
:Map tier0; connect; create reader;
:SELECT witness IDs and trust;
:Calculate source rank; sort witnesses by rank and ID;
if (TSV requested and open fails?) then (yes)
 :Return 1;
 stop
endif
while (More witness trunks?) is (yes)
 :walk_down; propagate occurrence counts; tally_under;
 while (More positive-game claim tallies?) is (yes)
  :Initialize stock standing if absent;
  :lp_attest_series(trust, games, mean score, floor);
  :Accumulate matches / print optional TSV;
 endwhile (no)
 :Optionally output voice and file tallies;
 :Free witness DAG and tallies;
endwhile (no)
:Read existing consensus in sets of up to 50000;
:Compare rating / deviation / volatility bitwise and match count;
:Count same, different and missing BEFORE writing;
if (--write and replayed set nonempty?) then (yes)
 :BEGIN; create temporary replayed table;
 :Binary COPY replayed standings;
 :UPDATE existing consensus; INSERT missing consensus;
 :COMMIT;
endif
:Print comparison; close TSV, maps, reader and connection;
:Return 1 if pre-write different or missing, otherwise 0;
stop
legend
--write is a mutation; default compares only.
A return of 1 after --write can report the pre-write mismatch.
This function does not delete extra consensus rows outside the replayed set.
endlegend
''',['Laplace-Engine/src/provenance.c'],
'cmd_replay and stands_compare reviewed, including write transaction and return condition. walk_down/tally_under/rank_of and the native series solver remain nested. The diagram describes source behavior, not successful runtime reconstruction.', [])
add('engine-forget','Modular Engine — remove witnesses and replay affected standing',r'''
start
:Parse witness selectors, --trunk and --except;
if (No selectors and not --except?) then (yes)
 :Return 2;
 stop
endif
:Map tier0; connect worker connections;
:Parse trunk IDs or decompose source names;
if (Invalid explicit ID?) then (yes)
 :Return 2;
 stop
endif
:Read witnesses; compare trunk ID or first source-record constituent;
:Apply --except inversion to selected set;
if (No matching witnesses?) then (yes)
 :Return 1;
 stop
endif
:Collect distinct claims under selected source trunks;
:DELETE selected witness rows;
:sweep seeded with removed trunks;
:standings_replay on touched claims;
:Read remaining testimony in sets; order by claim / source rank / trunk ID;
:Play surviving positive-game witness series;
:Compare and write rebuilt standings through stands_compare;
:DELETE consensus for touched claims with no surviving witness;
:Close connections and free touched set; return 0;
stop
legend
Destructive operation; diagram generation does not execute it.
Witness deletion, sweep and replay are not enclosed in one transaction here.
Shared content eligibility is decided by sweep's reference counts and roots.
endlegend
''',['Laplace-Engine/src/forget.c','Laplace-Engine/src/provenance.c'],
'cmd_forget and standings_replay reviewed. SQL transport failures can terminate inside helpers; no automatic rollback/retry is invented. sweep is detailed separately; low-level path decoding and SQL transport remain nested.', [])
add('engine-sweep','Modular Engine — reference-count sweep and dry run',r'''
start
:Parse connection / workers / --dry; reject extra args with return 2;
:Map tier0 and open connections;
:Discover populated entity / physicality partitions;
:Read paths above tier0 in parallel; count holders by child ID;
:Mark witness trunks and lineages as roots;
if (Standalone sweep?) then (yes)
 :Keep non-curated file roots;
 :Select entities with no holder and no root;
else (forget supplies seed)
 :Select seed entities that are not roots;
endif
while (Current removal set nonempty?) is (yes)
 :Read selected paths; decrement child holder counts;
 :Queue newly unheld non-root entities;
 if (Not dry?) then (yes)
  :DELETE consensus for selected IDs;
  :DELETE selected physicality rows by partition;
  :DELETE selected entity rows by partition;
 endif
 :Report level; advance removal set;
endwhile (no)
:Free count maps; report total;
:Close connections; return 0;
stop
legend
--dry calculates cascade without issuing the deletion statements.
This source does not wrap the whole cascade in a single transaction.
The tier0 paths are excluded from the scanned entity set.
endlegend
''',['Laplace-Engine/src/forget.c'],
'cmd_sweep and sweep orchestration reviewed. row_path/root classification, path decoding and over_ids transport are nested helpers; the source navigator exposes them. No sweep or deletion executed.', [])
add('native-identity','Modular Native — content identity and reference composition',r'''
start
if (Which public operation?) then (lp_id_codepoint)
 :Encode codepoint with lp_utf8_put;
 :lp_hash16 UTF-8 bytes;
 :Return 128-bit ID;
 stop
elseif (Operation?) then (lp_id_compose)
 if (Exactly one child?) then (yes)
  :Copy child ID;
 else (zero or many)
  :lp_hash16 ordered contiguous child IDs;
 endif
 :Return ID;
 stop
else (lp_ref_compose)
 if (Exactly one child?) then (yes)
  :Return entire child reference;
  stop
 endif
 :Initialize zero reference and selected tier;
 if (No children?) then (yes)
  :Return zero reference;
  stop
 endif
 if (At most 64 children?) then (yes)
  :Gather IDs; sum each coordinate; lp_hash16;
 else (more)
  :Stream groups of up to 64 ordered IDs into BLAKE3;
  :Accumulate child coordinates; finalize first 16 bytes;
 endif
 :lp_coord_mean from accumulated coordinates;
 :Return identity, coordinate and tier;
 stop
endif
legend
No recipe, tier, source, coordinate or domain prefix is hashed here.
Empty lp_id_compose hashes empty bytes; empty lp_ref_compose returns zeros.
These entry points therefore do not have identical empty-input behavior.
One-child reference composition preserves the child's tier too.
endlegend
''',['Laplace-Native/src/identity.c','Laplace-Native/src/compose.c'],
'lp_id_codepoint, lp_id_compose, lp_hash16 and lp_ref_compose reviewed. UTF-8 encoding, fixed-point accumulation/mean and BLAKE3 implementation remain nested. No digest fixtures executed; this establishes the actual hash inputs and branches.', ['11.5','11.6'])
add('native-series','Modular Native — trust-bounded witness series',r'''
start
:lp_attest_series(rating, trust, games, mean score, floor);
if (Trust zero or games zero?) then (yes)
 :Return unchanged;
 stop
endif
:Clamp score to [0,1];
if (Negative trust?) then (yes)
 :Flip score; use absolute trust;
endif
:Clamp trust at 1;
:Effective score = 0.5 + trust * (score - 0.5);
:Convert prior rating and deviation to scaled coordinates;
:Bracket posterior mode between prior and effective-score logit,
also bounded by games * prior_variance;
while (Bisection iterations below 2000?) is (yes)
 :Compute midpoint;
 if (Midpoint equals or exceeds a bracket endpoint?) then (yes)
  break
 endif
 :Evaluate posterior slope;
 if (Slope positive?) then (yes)
  :Move lower endpoint;
 else (no)
  :Move upper endpoint;
 endif
endwhile (no)
:Calculate mode and posterior deviation;
:Apply lp_series_floor(trust, floor);
:Cap new deviation at prior deviation;
:Write rating and deviation; leave volatility unchanged;
stop
legend
This is lp_attest_series, not the separate lp_attest scalar matchup.
lp_attest still converts trust to opponent deviation.
Witness-dependence grouping is not performed inside this solver.
The diagram does not establish validity for NaN or invalid prior states.
endlegend
''',['Laplace-Native/src/consensus.c'],
'Series solver and floor helper reviewed from the current native source. This distinguishes the current bounded-score series path from the scalar lp_attest path described in historical conflict C4. Caller-side dependence, initialization and input validation remain separate.', ['13.2'])
add('modular-activation','Operations — modular installation, activation and recovery',r'''
start
:Select host manifest; parse install / deploy / check / select;
if (Select-only or absent manifest?) then (yes)
 :Return selected path or no-declaration result;
 stop
endif
:Validate host, paths, identities, allocation and required inputs;
if (Check-only?) then (yes)
 :Return validated-input result;
 stop
endif
:Require root for install and non-root for recurring deploy;
:Acquire host lock;
if (Install?) then (yes)
 :prepare installation;
else (deploy)
 :Compare installed declaration; require installed unit and restart watcher;
endif
:Create receipt directory; package selected release;
:Validate release is a non-symlink under releases;
:Snapshot prior link / unit / environment / service states;
if (Install?) then (yes)
 :peer_check and candidate probe BEFORE service changes;
endif
:Enter activation recovery boundary;
if (Install?) then (yes)
 :Write unit and environment; verify unit; daemon-reload;
 :Stop previously active legacy service;
endif
:Switch current release link;
if (Install?) then (yes)
 :Enable / start / restart modular service;
else (deploy)
 :Touch restart signal;
endif
:Poll for active service with a new InvocationID;
:Check unit contents; run deployment.py verify into runtime receipt;
if (Install and legacy service was active?) then (yes)
 :Disable legacy service;
endif
if (Activation or verification raises?) then (yes)
 :Restore previous release link or remove newly created link;
 if (Install?) then (yes)
  :Restore unit/environment; daemon-reload;
  :Restore old active service and legacy service where applicable;
 else (previous release exists)
  :Touch restart signal for prior release;
 endif
 :Re-raise original failure unless recovery itself raises;
 stop
endif
if (Install?) then (yes)
 :Install restart watcher; optionally install operator client;
endif
:Write acceptance.json referencing runtime.json;
stop
legend
Candidate probe is before the recovery try block.
Restart-watcher/client setup occurs after activate returns;
failures there are outside activate's rollback boundary.
No installation or service operation was run for this atlas.
endlegend
''',['Laplace-Operations/installation/modular.py'],
'main and activate control paths reviewed. prepare/package/grants/probe/verify/client installation and host-lock internals remain nested; the source navigator exposes their bodies. Recovery actions are attempts and can themselves fail. This diagram does not certify any installed host.', [])
from engine_views import add_engine_views
add_engine_views(add)
from unicode_views import add_unicode_views
add_unicode_views(add)
from model_views import add_model_views
add_model_views(add)
from native_views import add_native_views
add_native_views(add)
from database_views import add_database_views
add_database_views(add)
from uci_views import add_uci_views
add_uci_views(add)
from monorepo_views import add_monorepo_views
add_monorepo_views(add)
from monorepo_native_views import add_monorepo_native_views
add_monorepo_native_views(add)
from witness_views import add_witness_views
add_witness_views(add)
from cognition_views import add_cognition_views
add_cognition_views(add)
from refactor_lifecycle_views import add_refactor_lifecycle_views
add_refactor_lifecycle_views(add)
from family_boundary_views import add_family_boundary_views
add_family_boundary_views(add)
from mcp_views import add_mcp_views
add_mcp_views(add)
from lpm_views import add_lpm_views
add_lpm_views(add)
from conventional_views import add_conventional_views
add_conventional_views(add)
from authority_views import add_authority_views
add_authority_views(add)
from billing_views import add_billing_views
add_billing_views(add)
from intent_views import add_intent_views
add_intent_views(add)
from evidence_read_views import add_evidence_read_views
add_evidence_read_views(add)
from code_views import add_code_views
add_code_views(add)
from chess_views import add_chess_views
add_chess_views(add)
reconciliation=json.loads((OUT/'reconciliation.json').read_text(encoding='utf-8'))
for t in traces:
    t['spec_operations']=list(dict.fromkeys(t['spec_operations']+reconciliation['additional_operation_links'].get(t['id'],[])))
    t['spec_link_meaning']=reconciliation['link_meaning']
source_symbols=json.loads((OUT/'source-map.json').read_text(encoding='utf-8'))['symbols'] if (OUT/'source-map.json').is_file() else []
nested_paths={
 'monorepo-model-ingest':['monorepo-model-header','monorepo-model-circuits','monorepo-working-set'],
 'monorepo-model-circuits':['monorepo-model-claims','monorepo-working-set'],
 'monorepo-foundry-http':['monorepo-foundry-process','monorepo-quote-gate','monorepo-quote-consume'],
 'engine-tier0':['engine-unicode-collation'],
 'monorepo-unicode-admission':['monorepo-unicode-snapshot','monorepo-unicode-xml','monorepo-working-set'],
 'monorepo-unicode-snapshot':['monorepo-unicode-xml'],
 'monorepo-authority-routing':['monorepo-authority-proxy','monorepo-authority-enforcement','monorepo-authority-billing','monorepo-chat','monorepo-completions'],
 'monorepo-authority-enforcement':['monorepo-authority-tenant','monorepo-authority-platform'],
 'monorepo-authority-tenant':['monorepo-authority-keys','monorepo-turn-scope'],
 'monorepo-authority-platform':['installed-op'],
 'monorepo-code-entry':['monorepo-code-construct','monorepo-quote-gate'],
 'monorepo-code-construct':['monorepo-code-forward','monorepo-code-verify','monorepo-working-set'],
 'monorepo-code-forward':['monorepo-forward-input'],
 'monorepo-code-verify':['monorepo-code-process'],
 'monorepo-chess-ownership':['monorepo-authority-tenant','monorepo-chess-runtime','monorepo-chess-play','monorepo-chess-lab-http'],
 'monorepo-chess-read-search':['monorepo-chess-explore','monorepo-chess-training'],
 'monorepo-chess-play':['monorepo-chess-buffer','monorepo-chess-deposit','monorepo-chess-retained'],
 'monorepo-chess-deposit':['monorepo-working-set'],
 'monorepo-chess-lab-http':['monorepo-chess-lab-lifecycle','monorepo-chess-lab-reingest'],
 'monorepo-chess-lab-lifecycle':['monorepo-chess-lab-reingest','monorepo-chess-retained'],
 'monorepo-chess-lab-reingest':['monorepo-chess-pgn-admission','monorepo-chess-runtime'],
 'monorepo-chess-pgn-admission':['monorepo-working-set','monorepo-chess-deposit','monorepo-chess-player-profiles'],
 'monorepo-intent-begin':['monorepo-intent-continuations','monorepo-intent-geometry','monorepo-intent-couple','monorepo-intent-compile'],
 'monorepo-intent-compile':['monorepo-evidence-observations','monorepo-evidence-consensus','monorepo-cognition-create'],
 'monorepo-evidence-consensus':['monorepo-evidence-leaf','monorepo-evidence-planes'],
 'monorepo-shape-discover':['monorepo-shape-contract'],
 'monorepo-shape-contract':['monorepo-shape-instantiate','monorepo-cognition-create'],
 'monorepo-authority-billing':['monorepo-quote-preflight','monorepo-quote-gate'],
 'monorepo-quote-gate':['monorepo-credit-debit','monorepo-quote-consume'],
 'monorepo-quote-consume':['monorepo-chat','monorepo-completions','monorepo-embeddings'],
 'conventional-inventory':['conventional-select'],
 'conventional-select':['conventional-tokenizer','conventional-plan-modalities'],
 'conventional-tokenizer':['conventional-prepare'],
 'conventional-prepare':['conventional-derive-config'],
 'conventional-derive-config':['conventional-plan-execution'],
 'conventional-hardware':['conventional-plan-execution','conventional-plan-modalities'],
 'conventional-plan-execution':['conventional-train'],
 'conventional-train':['conventional-runtime','conventional-models','conventional-evaluate','conventional-rl'],
 'conventional-launch':['conventional-train','conventional-media-train'],
 'conventional-configure-launch':['conventional-inventory','conventional-select','conventional-tokenizer','conventional-prepare','conventional-derive-config','conventional-hardware','conventional-plan-execution','conventional-plan-modalities'],
 'conventional-setup':['conventional-runtime','conventional-configure-launch'],
 'conventional-runner-install':['conventional-setup','conventional-launch'],
 'conventional-plan-modalities':['conventional-media-train'],
 'conventional-media-train':['conventional-media-decode','conventional-runtime','conventional-models'],
 'mcp-python-stdio':['lpm-index','lpm-admit','lpm-search-fetch'],
 'lpm-index':['lpm-admit'],
 'lpm-admit':['lpm-delimited','lpm-code-compose','native-text','native-identity'],
 'lpm-search-fetch':['native-text','lpm-save-load'],
 'mcp-native-dispatch':['mcp-engine-process','mcp-ingest','mcp-embeddings','mcp-forward-shape','mcp-search','mcp-python-stdio'],
 'mcp-engine-process':['engine-hop','engine-text','engine-translate','engine-degrees','engine-pull'],
 'mcp-ingest':['engine-ingest'],
 'mcp-embeddings':['native-text'],
 'mcp-forward-shape':['native-text'],
 'mcp-search':['native-text','engine-read-snapshot'],
 'family-observation-effects':['monorepo-chat','monorepo-turn-content','monorepo-working-set','monorepo-cognition-close','engine-turn','engine-pull','native-series','refactor-context','refactor-isa-execute','refactor-activation'],
 'refactor-isa-execute':['refactor-isa-validate','refactor-context'],
 'refactor-isa-validate':['refactor-context'],
 'refactor-stream-stage':['refactor-context','refactor-activation'],
 'refactor-producer':['refactor-context','refactor-stream-stage','refactor-activation'],
 'refactor-activation':['refactor-context'],
 'engine-text':['native-text','native-made','native-utf8'],
 'native-text':['native-made','native-utf8'],
 'native-made':['native-factor','native-identity'],
 'native-factor':['native-identity'],
 'engine-ingest':['native-text','engine-standings-write','engine-merge-transaction'],
 'engine-replay':['native-series'],
 'engine-forget':['engine-sweep','engine-replay'],
 'engine-turn':['native-text','engine-read-snapshot'],
 'engine-pull':['native-text','engine-read-snapshot'],
 'engine-hop':['engine-read-snapshot'],
 'engine-translate':['engine-read-snapshot'],
 'engine-degrees':['engine-read-snapshot'],
 'engine-fills':['engine-read-snapshot'],
 'engine-pgn':['engine-read-snapshot'],
 'engine-standings-write':['engine-stage-open','native-series'],
 'engine-merge':['engine-merge-transaction'],
 'engine-merge-transaction':['engine-stage-open','engine-resolve-parts','engine-merge-leaf'],
 'uci-dispatch':['uci-configuration','uci-open','uci-relay','uci-verify','uci-serve'],
 'uci-open':['uci-configuration','uci-framing'],
 'uci-relay':['uci-framing'],
 'uci-verify':['uci-open','uci-framing','uci-relay'],
 'uci-serve':['uci-configuration','uci-open','uci-relay'],
 'monorepo-chat':['monorepo-turn-scope','monorepo-observed-sql','monorepo-forward-proof','monorepo-witness-queue'],
 'monorepo-completions':['monorepo-turn-scope','monorepo-witness-queue'],
 'monorepo-forward-proof':['monorepo-witness-queue'],
 'monorepo-witness-queue':['monorepo-turn-close'],
 'monorepo-turn-close':['monorepo-turn-content','monorepo-turn-atomic'],
 'monorepo-turn-atomic':['monorepo-working-set','monorepo-session-append'],
 'monorepo-session-append':['monorepo-session-read'],
 'monorepo-observed-sql':['monorepo-forward-input','monorepo-session-read'],
 'monorepo-forward-input':['monorepo-forward-setup'],
 'monorepo-forward-setup':['monorepo-intent-begin','monorepo-intent-couple','monorepo-intent-compile','monorepo-shape-discover','monorepo-forward-round','monorepo-cognition-create'],
 'monorepo-forward-round':['monorepo-forward-comparator','monorepo-forward-election'],
 'monorepo-forward-election':['monorepo-forward-round','monorepo-forward-terminal','monorepo-cognition-close'],
 'monorepo-forward-terminal':['monorepo-cognition-identities'],
 'monorepo-cognition-create':['monorepo-cognition-close','monorepo-cognition-identities'],
 'monorepo-cognition-close':['monorepo-cognition-identities'],
 'engine-deploy':['engine-status']
}
for t in traces:
    t['nested_views']=nested_paths.get(t['id'],[])
    t['source_links']=[{'name':s['name'],'url':'source.html#'+s['id']} for s in source_symbols if s['repo']+'/'+s['path'] in [e['path'] for e in t['evidence']] and (t['id'].startswith(('uci-','monorepo-cognition-','mcp-','lpm-','conventional-')) or s['name'].startswith('cmd_') or s['name'] in ['laplace_isa_validate','laplace_isa_execute','laplace_isa_receipt_validate','laplace_framework_context_validate','laplace_framework_context_fingerprint','laplace_pg_read_execution_context','laplace_framework_admit_staged_stream','laplace_framework_commit_admitted_stream','laplace_framework_abort_admitted_stream','stage_canonical_stream_impl','run_producer_impl','pg_laplace_session_append_turns','pg_laplace_session_turn_ids','RecordTurnAsync','RecordPromptAsync','RecordResponseAsync','TryEnqueueTurn','ExecuteAsync','CloseAsync','TryBuildTurnChange','StageOccurrence','ApplyConversationTurnAsync','ApplyCoreAsync','ApplyWorkingSetAtomicAsync','ApplyManyInternalAsync','ApplyStagesCoreAsync','ApplyPreparedStagesCoreAsync','InvokeAsync','InvokeOpAsync','MapOpEndpoints','lp_id_codepoint','lp_id_compose','lp_ref_compose','lp_attest_series','lp_attest','main','activate','lp_text_decompose','lp_text_parts','made','lp_factor','lp_utf8_next','MapOpenAiCompatEndpoints','ResolveTurnScopeAsync','BuildForwardProof','ForwardTurnObservedStreamAsync','ForwardTurnObservedStepsAsync','forward_prompt','walk_continuations','candidate_can_output','candidate_compare','emit_terminal','pg_laplace_forward_trace','entity_named','db_read','pool_open','fold_events','standings_write','standings_part','stand_get','merge','merge_leaf','resolve_parts'])]
directory=OUT/'trace-diagrams';directory.mkdir(exist_ok=True)
for t in traces:
    status='Source-reviewed orchestration. Nested semantics remain open; no runtime acceptance claimed. '+t['boundary']
    footer='\nfooter\n'+'\n'.join(textwrap.wrap(status,90))+'\nend footer\n'
    evidence=''.join("' Evidence: "+e['path']+' SHA256 '+e['sha256']+'\n' for e in t['evidence'])
    (directory/(t['id']+'.puml')).write_text('@startuml\n'+evidence+'skinparam backgroundColor #FAFCFF\nskinparam shadowing false\nskinparam defaultFontName SansSerif\ntitle '+t['title']+'\n'+t['body']+footer+'\n@enduml\n',encoding='utf-8')
for t in traces:
    if t['id'].startswith(('monorepo-authority-','monorepo-intent-','monorepo-shape-','monorepo-evidence-','monorepo-quote-','monorepo-credit-','monorepo-resource-','monorepo-code-','monorepo-chess-')):
        paths={e['path'] for e in t['evidence']}
        t['source_links']=[{'name':s['name'],'url':'source.html#'+s['id']} for s in source_symbols if s['repo']+'/'+s['path'] in paths]
java=JAVA
subprocess.run([java,'-Djava.awt.headless=true','-jar',str(PLANTUML),'-tsvg',str(directory/'*.puml')],check=True)
for t in traces:
    if 'Syntax Error' in (directory/(t['id']+'.svg')).read_text(encoding='utf-8'):
        raise RuntimeError(t['id'])
(OUT/'traces.json').write_text(json.dumps(traces,indent=2),encoding='utf-8')
lock_path.write_text(json.dumps(reviewed_hashes,indent=2,sort_keys=True),encoding='utf-8')
print(json.dumps({'reviewed_orchestrations':len(traces),'output':'traces.json'}))
