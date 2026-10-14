from pathlib import Path
import json
from datetime import datetime,timezone
p=Path(__file__).resolve().parent
m=json.loads((p/'manifest.json').read_text(encoding='utf-8'))
m['updated_utc']=datetime.now(timezone.utc).isoformat()
m['evidence_layers']['reviewed_orchestration_paths']=len(json.loads((p/'traces.json').read_text(encoding='utf-8')))
m['export_sets']=sum(len(list((p/f).glob('*.puml'))) for f in ['diagrams','sequence-diagrams','trace-diagrams','isa-diagrams'])
m['build_order']=['rebuild.py', 'web/scripts/package-architecture.mjs', 'npm run build', 'npm run test:architecture']
m['entry']='/explore/architecture'
m['views']={
    'system':'/explore/architecture',
    'existing_operation_spine':'/explore/architecture?section=operations',
    'reviewed_paths':'/explore/architecture?kind=Source+trace',
    'registered_entries':'/explore/architecture?section=registrations',
    'source_navigation':'/explore/architecture?section=sources',
    'conflicts':'/explore/architecture?section=conflicts',
    'publication_assets':'/explore/architecture?kind=Analytic+figure',
}
m['publication_figures']=3
m['publication_evidence']='publication-evidence.json'
m['publication_measurements_reproduced']=False
if 'model_views.py' not in m['review_modules']:m['review_modules'].append('model_views.py')
entry='Complete ModelDecomposer, ModelCheckpoint, ModelCoordinates and ModelTokenEdgeETL: iterator/partial-output boundaries, header versus payload identity, source-scoped circuit identity, snapshot-envelope retention and significant-testimony pages. Complete Foundry HTTP, compile wrapper and export subprocess lifecycle; CLI construction and native contraction remain separate.'
if entry not in m['reviewed_boundaries']:m['reviewed_boundaries'].append(entry)
if 'unicode_views.py' not in m['review_modules']:m['review_modules'].append('unicode_views.py')
entry='Modular tier0.c; monorepo native UCD XML and Unicode snapshot implementation; managed Unicode floor preparation, admission barriers and semantic-pass lifecycle. Independent artifact parsers and full recipe interpreter remain nested; no runtime Unicode conformance test performed.'
if entry not in m['reviewed_boundaries']:m['reviewed_boundaries'].append(entry)
entry='Conventional runtime, media decode/train, all local model constructors, callable RL/evaluation bodies and missing module main guards; all six setup/configuration/training/runner shell scripts.'
if entry not in m['reviewed_boundaries']:m['reviewed_boundaries'].append(entry)
entry='Monorepo request middleware order; fixed-origin Refactor proxy; tenant/workspace resolver; route enforcement; API-key lifecycle and stores; platform permission/role transaction; billing account ownership middleware.'
if entry not in m['reviewed_boundaries']:m['reviewed_boundaries'].append(entry)
if 'authority_views.py' not in m['review_modules']:m['review_modules'].append('authority_views.py')
entry='Monorepo quote preflight, execution gate, credit debit, pre-inference consumption and separate ledger writes; PostgreSQL resource ownership equation. External billing provider and installed-catalog acceptance remain open.'
if entry not in m['reviewed_boundaries']:m['reviewed_boundaries'].append(entry)
if 'billing_views.py' not in m['review_modules']:m['review_modules'].append('billing_views.py')
entry='Complete prompt_intent.h and PostgreSQL task_shape.c: exact root/occurrence bindings, structural/geometry planes, naming hop, witnessed explicit contract, task-shape discovery/support/typed slot combinations and projected request identity. Native matcher/codecs and typed read internals remain separate.'
if entry not in m['reviewed_boundaries']:m['reviewed_boundaries'].append(entry)
if 'intent_views.py' not in m['review_modules']:m['review_modules'].append('intent_views.py')
entry='Complete observation_read.c, consensus_scan.c and consensus_plane_scan.h plus observation SQL: exact witness cells/occurrence roles/qualifiers; root ACL/RLS, active snapshot, partition topology, complete keyed fallback and independent typed rank cutoffs.'
if entry not in m['reviewed_boundaries']:m['reviewed_boundaries'].append(entry)
if 'evidence_read_views.py' not in m['review_modules']:m['review_modules'].append('evidence_read_views.py')
entry='Monorepo code chat/dedicated HTTP entry, CodePlayerService, ForwardCodeAsync SQL adapter and complete CodeToolchain verification/process helpers, including persistence order and timeout/cancellation differences. Grammar admission and native realization remain separate.'
if entry not in m['reviewed_boundaries']:m['reviewed_boundaries'].append(entry)
if 'code_views.py' not in m['review_modules']:m['review_modules'].append('code_views.py')
entry='Monorepo chess HTTP routes and ownership, complete ChessRuntimeService and ChessEngineService, retained ChessPlayOperation, live-host buffering/completion/play recovery boundaries. Detailed graph builders, search, rules, lab jobs and learning internals remain nested.'
if entry not in m['reviewed_boundaries']:m['reviewed_boundaries'].append(entry)
if 'chess_views.py' not in m['review_modules']:m['review_modules'].append('chess_views.py')
entry='Complete ChessLabService, retained artifact ingestion, ChessLabStorage, ChessPgnIngestor and ChessPgnChunk: process-local jobs, file receipt lifecycle, serialized shared-writer admission, per-chunk structure repair and exact readback, provider-profile testimony. Decomposer/calculated owners/measurement implementation remain nested.'
if entry not in m['reviewed_boundaries']:m['reviewed_boundaries'].append(entry)
m['next_review_boundaries']=[s.replace('Monorepo prompt_intent task/compiler helpers, exact typed scan/provenance, source-dependence, quote/tenant authority and resource admission','Monorepo query_evidence provenance/source-dependence, native task codecs/matchers and concrete resource reservation/accounting').replace('Conventional provider model constructors/runtime, media decode/train, RL/evaluation and launch scripts; liblpm remaining containers/realization and CLI check/bench paths','Conventional workflows and external provider acceptance; liblpm remaining containers/realization and CLI check/bench paths') for s in m['next_review_boundaries']]
(p/'manifest.json').write_text(json.dumps(m,indent=2,ensure_ascii=False),encoding='utf-8')
print(json.dumps({'reviewed_paths':m['evidence_layers']['reviewed_orchestration_paths'],'uml_exports':m['export_sets'],'publication_figures':m['publication_figures']}))
