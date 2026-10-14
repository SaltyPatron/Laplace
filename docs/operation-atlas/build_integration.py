"""Whole-inventory navigation and cross-operation persistence boundaries.

Relationships reuse reviewed evidence; no inferred call edges or acceptance scores.
"""
from pathlib import Path
import json, html

ROOT = Path(__file__).resolve().parent
read = lambda name: json.loads((ROOT / name).read_text(encoding='utf-8'))
traces = {t['id']: t for t in read('traces.json')}
sequence = read('sequence.json')
inventory = read('operations.json')

lanes = [
    dict(name='Monorepo conversation', contract='Observation → scoped cognition → realization → witnessed consequence',
         paths=['monorepo-authority-routing','monorepo-authority-tenant','monorepo-authority-enforcement','monorepo-quote-gate','monorepo-quote-consume','monorepo-chat','monorepo-turn-scope','monorepo-forward-input','monorepo-intent-begin','monorepo-forward-round','monorepo-cognition-close','monorepo-observed-sql','monorepo-witness-queue','monorepo-turn-close','monorepo-turn-atomic','monorepo-working-set','monorepo-session-append','monorepo-session-read','monorepo-forward-proof'],
         split='Credit debit, quote consumption and usage ledger precede inference in the reviewed handlers. Working-set bulk copies can commit outside the control transaction. Empty responses skip response deposit; the HTTP proof can still append its witness event.',
         recovery='Journal replay skips the participant; the post-commit epoch read can fail after a successful commit. Caller cancellation after queue admission does not cancel the consumer write.'),
    dict(name='Monorepo code', contract='Shared cognition → grammar-constrained candidate → external verification → attributable calculated testimony',
         paths=['monorepo-code-entry','monorepo-quote-consume','monorepo-code-construct','monorepo-code-forward','monorepo-code-verify','monorepo-code-process','monorepo-working-set'],
         split='Candidate persistence precedes tool verification. The tool receipt is composed and admitted separately. Verification means syntax/selected tool success, not functional correctness.',
         recovery='Timeout kills the process tree; caller cancellation follows a different catch path with no explicit kill. Process disposal alone does not prove child termination.'),
    dict(name='Monorepo chess play', contract='Complete board and history → legal proposals → typed steering → selected move → witnessed outcome',
         paths=['monorepo-chess-ownership','monorepo-chess-runtime','monorepo-chess-read-search','monorepo-chess-explore','monorepo-chess-play','monorepo-chess-retained','monorepo-chess-buffer','monorepo-chess-deposit','monorepo-chess-training'],
         split='Authenticated play keeps requester, moves, board and analysis in a replacement JSON file. Each CommitMove saves that record before publishing memory state. Live-game evidence is buffered; completion applies it to the substrate separately.',
         recovery='Normal play recovery replays ongoing files. Explicit recording retry also accepts eligible recording/failed files, preserves result/adjudication, and rebuilds the buffer before apply. Finish still writes recording, deposits to the database, then writes completed; no shared file/database transaction exists.'),
    dict(name='Modular ingestion and standing', contract='Source structure → content identity → witnessed series → published substrate',
         paths=['mcp-ingest','engine-ingest','native-text','native-made','native-identity','engine-stage-open','engine-standings-write','native-series','engine-merge-transaction','engine-merge-leaf','engine-resolve-parts','engine-replay','engine-forget','engine-sweep'],
         split='Staging, per-leaf merge and prepared transaction parts have distinct boundaries. First stage_open clears nonempty stage tables; cached stage_ready preserves state within the process.',
         recovery='Prepared-part recovery is explicit. A fresh standalone merge can first trigger staging cleanup. Review the source-specific branches before treating a retry as resume.'),
    dict(name='Modular reads and interfaces', contract='Selected read world → bounded query/forward → transported result',
         paths=['mcp-native-dispatch','mcp-engine-process','mcp-forward-shape','mcp-search','engine-read-snapshot','engine-turn','engine-pull','uci-dispatch','uci-open','uci-relay','uci-verify','uci-serve'],
         split='Engine db_read exports a repeatable-read snapshot imported by its pool. Native MCP db_bridge supplies no exported snapshot, so separate statements need not read the same request world.',
         recovery='UCI verification is a handshake/depth-one response probe. It does not establish independent legal-move or knowledge acceptance.'),
    dict(name='Refactor execution and effects', contract='Authority/resource context → complete program preflight → execution receipt; staged effect → admitted activation → commit',
         paths=['refactor-context','refactor-isa-validate','refactor-isa-execute','refactor-producer','refactor-stream-stage','refactor-activation'],
         split='ISA execution, inert stream staging, activation admission and activation commit are distinct events. Context structure validation does not itself consult an authority registry.',
         recovery='Producer resume regenerates and stages the prefix before comparing its checkpoint. Sequential sink abort calls do not demonstrate rollback. Concrete provider effects require their own evidence.'),
    dict(name='Local liblpm / Python MCP', contract='Local file admission → local content structure → search/fetch → persisted representation',
         paths=['mcp-python-stdio','lpm-index','lpm-admit','lpm-delimited','lpm-code-compose','lpm-search-fetch','lpm-save-load'],
         split='This is an independent local-memory surface. LPM1 persistence omits child-ID arrays. Search can admit query entities; skipped input may leave the previous last-record result.',
         recovery='The save path overwrites directly. Atomic replacement and complete structural reconstruction are not established by this serialization.'),
    dict(name='Conventional model workflow', contract='Inventory → measured selection → preparation → model/resource plan → training → exported artifacts',
         paths=['conventional-setup','conventional-inventory','conventional-select','conventional-tokenizer','conventional-prepare','conventional-derive-config','conventional-hardware','conventional-plan-execution','conventional-plan-modalities','conventional-runtime','conventional-train','conventional-media-decode','conventional-media-train','conventional-rl','conventional-evaluate','conventional-launch'],
         split='Preparation, planning, training checkpoints and final exports are separate effects. The aggregate launch receipt hashes planning files and reports paths, not checkpoint/corpus contents or proof of completion.',
         recovery='Media resume has a marker; text launch does not automatically resume. Configuration can retain earlier files after later failure. RL/evaluation modules define main without invoking it as module entry.'),
    dict(name='Installation and activation', contract='Selected environment → complete build/install → matching revision → activation → acceptance',
         paths=['modular-activation','uci-configuration','conventional-runner-install','conventional-configure-launch'],
         split='These are separate installation families and operating targets, not one shared installer. Release-path checks, service registration and application/database activation prove different things.',
         recovery='Read the activation/recovery branches for the selected family. Runner service/account/filesystem effects have no aggregate rollback; an installed marker does not rehash every installed file.'),
]
lanes[2]['paths'].extend(['monorepo-chess-lab-http','monorepo-chess-lab-lifecycle','monorepo-chess-lab-reingest','monorepo-chess-pgn-admission','monorepo-chess-player-profiles','monorepo-working-set'])
lanes[2]['recovery'] += ' PGN admission separately repairs missing testimony and move carriers, then verifies persisted trajectories after each chunk. A later readback or receipt-file failure does not roll back earlier database effects.'
lanes.append(dict(name='Monorepo model admission and export',
    contract='Checkpoint structure → circuit physicalities → calculated testimony; admitted evidence → recipe construction → exported consumer artifact',
    paths=['monorepo-model-ingest','monorepo-model-header','monorepo-model-circuits','monorepo-model-claims','monorepo-working-set','monorepo-foundry-http','monorepo-foundry-process','monorepo-quote-gate'],
    split='Model changes are yielded incrementally with retained snapshot envelopes. Current circuit identity includes witness and structural plane/layer/head address, differing from the content-convergence contract. Foundry HTTP returns a GGUF path after subprocess exit and file-presence checks; external semantic acceptance is separate.',
    recovery='Unsupported model branches can follow yielded header changes. Export errors do not roll back file or billing effects. Cancellation does not explicitly kill the export subprocess, and this wrapper has no temporary-directory cleanup.'))
for lane in lanes:
    for path in lane['paths']:
        assert path in traces, path
    lane['evidence'] = sorted({e['path'] for p in lane['paths'] for e in traces[p]['evidence']})

rows=[]
for stage in sequence['stages']:
    for op in stage['operations']:
        links=[{'name':t['title'],'url':'traces.html#'+t['id']} for t in traces.values() if op['id'] in t['spec_operations']]
        rows.append(dict(kind='Specification',family='Wiki',name=op['id']+' '+op['title'],group=stage['title'],url='sequence.html#stage-'+stage['id'],links=links,status='Source comparison links; not implementation or acceptance proof' if links else 'No explicit operation-to-trace mapping recorded'))
for op in inventory['operations']:
    links=[link for link in op.get('source_bodies',[]) if link['url'].startswith('traces.html#')]
    rows.append(dict(kind='Registration',family=op['family'],name=op['name'],group=op['surface'],url=(op.get('source_bodies') or [{'url':'operations.html'}])[0]['url'],links=links,status=op.get('status','Source registration only')))
data=dict(lanes=lanes,rows=rows,counts={'specification_operations':sum(len(s['operations']) for s in sequence['stages']),'source_registrations':len(inventory['operations'])},relationship='Lane lists are lifecycle navigation and boundary comparisons, not inferred call graphs. No row asserts runtime acceptance.')
(ROOT/'integration.json').write_text(json.dumps(data,indent=2,ensure_ascii=False),encoding='utf-8')
print(json.dumps({'lanes':len(lanes),'inventory_entries':len(rows)}))
