"""Validate generated references, source freshness and export availability."""
from pathlib import Path
from atlas_paths import REPOSITORIES, TOOLS, PLANTUML, JAVA
import json, re, hashlib, datetime
from urllib.parse import unquote, urlsplit
ROOT=Path(__file__).resolve().parent
REPOS=REPOSITORIES
errors=[]
traces=json.loads((ROOT/'traces.json').read_text(encoding='utf-8'))
sequence=json.loads((ROOT/'sequence.json').read_text(encoding='utf-8'))
inventory=json.loads((ROOT/'operations.json').read_text(encoding='utf-8'))
trace_ids={t['id'] for t in traces}
op_ids={o['id'] for s in sequence['stages'] for o in s['operations']}
source_ids={s['id'] for s in json.loads((ROOT/'source-map.json').read_text(encoding='utf-8'))['symbols']}
for t in traces:
    for e in t['evidence']:
        p=REPOS/e['path']
        if not p.is_file() or hashlib.sha256(p.read_bytes()).hexdigest()!=e['sha256']:
            errors.append('Stale source evidence: '+e['path'])
    for op in t['spec_operations']:
        if op not in op_ids: errors.append('Unknown specification operation: '+op)
    for child in t['nested_views']:
        if child not in trace_ids: errors.append('Unknown nested trace: '+child)
    for link in t['source_links']:
        if link['url'].split('#')[-1] not in source_ids:errors.append('Unknown source symbol: '+link['url'])
engine=[r for r in inventory['operations'] if r['surface']=='Engine CLI']
for r in engine:
    if 'engine-'+r['name'] not in trace_ids:errors.append('Missing Engine handler diagram: '+r['name'])
for key,obs in sequence['reconciliation']['observations'].items():
    if not any(c['title'].startswith(key+'.') for c in sequence['conflicts']):errors.append('Unknown conflict: '+key)
    for o in obs:
        for t in o['traces']:
            if t not in trace_ids:errors.append('Unknown conflict trace: '+t)
exports=0
for folder in ['diagrams','sequence-diagrams','trace-diagrams','isa-diagrams']:
    for p in (ROOT/folder).glob('*.puml'):
        exports+=1
        for ext in ['svg','png']:
            q=p.with_suffix('.'+ext)
            if not q.is_file():errors.append('Missing export: '+str(q.relative_to(ROOT)))
        svg=p.with_suffix('.svg')
        if svg.exists() and 'Syntax Error' in svg.read_text(encoding='utf-8'):errors.append('UML syntax: '+p.name)
for page in ROOT.glob('*.html'):
    for target in re.findall(r'href="([^"]+)"',page.read_text(encoding='utf-8')):
        u=urlsplit(target)
        if u.scheme or u.netloc or not u.path:continue
        if '__' in u.path or '+' in u.path:continue  # unexpanded template text
        if not (ROOT/unquote(u.path)).exists():errors.append('Missing local link: '+page.name+' -> '+u.path)
receipt={'checked_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
         'scope':'Artifact references, evidence hashes and export presence; no runtime behavior or semantic completeness asserted.',
         'engine_registered_handlers':len(engine),'reviewed_paths':len(traces),'export_sets':exports,
         'errors':sorted(set(errors))}
(ROOT/'artifact-check.json').write_text(json.dumps(receipt,indent=2),encoding='utf-8')
print(json.dumps(receipt))
raise SystemExit(bool(errors))
