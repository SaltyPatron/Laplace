"""Read-only source inventory; writes only beside this script. No operations execute."""
from pathlib import Path
from atlas_paths import REPOSITORIES, TOOLS, PLANTUML, JAVA
import re, json, hashlib, subprocess, datetime, html

OUT = Path(__file__).resolve().parent
ROOT = REPOSITORIES
rows, sources, repositories = [], {}, {}

def source(repo, relative):
    p = ROOT / repo / relative
    text = p.read_text(encoding='utf-8-sig', errors='replace')
    key = repo + '/' + relative.replace('\\', '/')
    sources[key] = {'path': str(p), 'sha256': hashlib.sha256(p.read_bytes()).hexdigest(), 'repository': repo}
    return key, text

def add(family, surface, name, key, text, offset, **extra):
    line = text.count('\n', 0, offset) + 1
    rows.append(dict(id=f'{key}:{line}:{name}', family=family, surface=surface, name=name,
                     source=key, line=line, status='registered; flow not traced', **extra))

for repo in ['Laplace','Laplace-Refactor','Laplace-Native','Laplace-Engine','Laplace-postgres','Laplace-MCP','Laplace-Operations','Laplace-UCI','Laplace-Conventional','Laplace-Wiki']:
    repositories[repo] = subprocess.check_output(['git','-C',str(ROOT/repo),'rev-parse','HEAD'], text=True).strip()

key, text = source('Laplace-Engine', 'src/op.c')
for m in re.finditer(r'\{\s*"([^"]+)"\s*,\s*(cmd_\w+)\s*,\s*"([^"]+)"\s*,\s*(A_\w+)\s*,\s*([^,]+),\s*"([^"]+)"\s*\}', text):
    name, handler, purpose, args, effects, gives = m.groups()
    definitions = []
    for path in sorted((ROOT/'Laplace-Engine/src').glob('*.c')):
        body = path.read_text(encoding='utf-8', errors='replace')
        match = re.search(r'\bint\s+'+handler+r'\s*\([^;]*?\)\s*\{', body)
        if match:
            sk, _ = source('Laplace-Engine', path.relative_to(ROOT/'Laplace-Engine').as_posix())
            definitions.append({'source':sk, 'line':body.count('\n',0,match.start())+1})
    add('modular','Engine CLI',name,key,text,m.start(),handler=handler,purpose=purpose,argument_set=args,effects_declared=effects.strip(),output_declared=gives,definitions=definitions)

key, text = source('Laplace-Refactor','contracts/isa.json')
isa = json.loads(text)
for name, opcode in isa['opcodes'].items():
    add('refactor','ISA contract',name,key,text,text.index('"'+name+'"'),opcode=opcode,contract=isa.get('operation_contracts',{}).get(name,{}),status_note='Contract registration; execution and public reachability require tracing.')

key, text = source('Laplace-MCP','native/api.c')
seen=set()
for m in re.finditer(r'!strcmp\(path,\s*"([^"]+)"\)',text):
    name=m.group(1)
    if name not in seen:
        add('modular','HTTP route',name,key,text,m.start()); seen.add(name)
for m in re.finditer(r'!strcmp\(name,\s*"([^"]+)"\)\)\s*(\w+)\(',text):
    add('modular','MCP tool',m.group(1),key,text,m.start(),handler=m.group(2))

key,text=source('Laplace-MCP','laplace_mcp.py')
for m in re.finditer(r'\{"name":\s*"([^"]+)"',text):
    add('modular','Python stdio MCP tool',m.group(1),key,text,m.start(),handler=m.group(1))
key,text=source('Laplace-MCP','native/main.c')
for m in re.finditer(r'!strcmp\(argv\[1\],\s*"([^"]+)"\)',text):
    add('modular','Local lpm CLI',m.group(1),key,text,m.start())

for repo, base, family in [('Laplace','extension/laplace_substrate/sql','monorepo'),('Laplace-postgres','sql','modular')]:
    paths = sorted((ROOT/repo/base).rglob('*.sql*'))
    if repo=='Laplace-postgres': paths=[ROOT/repo/'sql/laplace--1.12.sql']
    for p in paths:
        if '/tests/' in p.as_posix() or '/archive/' in p.as_posix(): continue
        key,text=source(repo,p.relative_to(ROOT/repo).as_posix())
        for m in re.finditer(r'\bCREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+([\w."${}]+)\s*\(',text,re.I):
            add(family,'SQL function declaration',m.group(1),key,text,m.start(),status_note='Declaration inventory; overloads and generated/template duplicates retained separately.')

for p in sorted((ROOT/'Laplace/app').rglob('*.cs')):
    if any(x in p.parts for x in ['obj','bin']) or 'Tests' in str(p): continue
    t=p.read_text(encoding='utf-8-sig',errors='replace')
    matches=list(re.finditer(r'\b(MapGet|MapPost|MapPut|MapDelete|MapPatch|MapMethods)\s*\(\s*"([^"]+)"',t))
    matches+=list(re.finditer(r'\b(new\s+Command)\s*\(\s*"([^"]+)"',t))
    if matches:
        key,text=source('Laplace',p.relative_to(ROOT/'Laplace').as_posix())
        for m in matches:
            add('monorepo','C# '+m.group(1),m.group(2),key,text,m.start(),status_note='Literal registration candidate; nested route prefixes and command parents not resolved yet.')

key,text=source('Laplace-UCI','src/Laplace.Uci/Program.cs')
for name in ['catalog','connect','verify','serve']:
    add('uci','connector CLI',name,key,text,text.index('"'+name+'"'))
for m in re.finditer(r'\bapp\.(MapGet|Map)\("([^"]+)"',text):
    add('uci','HTTP '+m.group(1),m.group(2),key,text,m.start())

key,text=source('Laplace-Native','include/laplace/laplace.h')
for m in re.finditer(r'\bLP_API\s+[^;{}]*?\b(lp_\w+)\s*\([^;{}]*\)\s*;',text):
    add('modular','Native public header',m.group(1),key,text,m.start(),handler=m.group(1),status_note='Canonical public-header declaration; caller contracts, effects and native branches require review.')

key,text=source('Laplace-Conventional','src/laplace_conventional/cli.py')
for m in re.finditer(r'sub\.add_parser\("([^"]+)"\)',text):
    add('conventional','Python CLI',m.group(1),key,text,m.start(),status_note='Argparse registration; independent conventional pipeline, not native cognition.')

coverage = [
 {'scope':'Modular Engine CLI','enumeration':'25 OPS entries parsed from canonical registry','depth':'Top-level handler flows available across reviewed paths and the text flow. Nested callees and failure semantics remain under review.'},
 {'scope':'Refactor ISA','enumeration':'All 14 opcodes in selected isa.json parsed','depth':'Execute-wrapper branches drawn with validator/source links. Whole-program validation and nested packet/native semantics remain separate.'},
 {'scope':'Monorepo SQL','enumeration':'CREATE FUNCTION declarations under substrate SQL tree','depth':'Template duplicates/overloads retained; generated installed catalog not reconciled.'},
 {'scope':'Monorepo API and CLI','enumeration':'Literal C# routes and Command constructors','depth':'Partial extractor; dynamic registration, grouped prefixes, MCP tools and native operators remain.'},
 {'scope':'Modular MCP/HTTP','enumeration':'Native ten-tool API, Python two-tool stdio and local lpm CLI registrations kept distinct','depth':'Native transport, child processes, SQL query wrappers and search traced; local index admission/composition/search/save/load traced. Nested kernels and protocol acceptance remain separate.'},
 {'scope':'UCI','enumeration':'Four CLI commands and two HTTP registrations','depth':'Dispatch, configuration, release resolution, transport, framing, relay, shutdown, verification and service paths reviewed. Engine internals and external ingress remain separate.'},
 {'scope':'Native public API','enumeration':'146 LP_API header declarations','depth':'Declaration inventory and source links; identity and attestation-series paths reviewed. Most nested kernels still need semantic tracing.'},
 {'scope':'Conventional','enumeration':'Eight argparse command registrations','depth':'All eight orchestration paths plus language-model training traced; media decoding/training, provider internals, evaluation and launch scripts remain separate.'},
 {'scope':'Operations installation','enumeration':'Syntax inventory; install/deploy activation path reviewed','depth':'Candidate probe, switching and recovery boundary drawn. Nested preparation, grants, verification and clients remain.'},
 {'scope':'Refactor packet suboperations, web interactions, model/media/code/chess/governance/discovery','enumeration':'Cross-cutting coverage incomplete','depth':'In scope. Source symbol counts and registration counts are not a completion denominator.'}
]
if (OUT/'source-map.json').is_file():
    symbols=json.loads((OUT/'source-map.json').read_text(encoding='utf-8'))['symbols']
    for row in rows:
        row['source_bodies']=[{'name':s['name'],'url':'source.html#'+s['id']} for s in symbols if row.get('handler')==s['name'] and any(d['source']==s['repo']+'/'+s['path'] and d['line']==s['line'] for d in row.get('definitions',[]))]
        if row['surface']=='Native public header':
            row['source_bodies']=[{'name':s['name'],'url':'source.html#'+s['id']} for s in symbols if s['repo']=='Laplace-Native' and s['name']==row['name']]
        if row['source'].startswith('Laplace-MCP/'):
            if row['surface']=='Python stdio MCP tool': target='mcp-python-stdio'
            elif row['surface']=='Local lpm CLI':
                target={'admit':'lpm-index','fetch':'lpm-search-fetch','index':'lpm-save-load','search':'lpm-search-fetch','serve':'mcp-native-dispatch'}.get(row['name'])
            else:
                short=row['name'].split('/')[-1]
                target={'embeddings':'mcp-embeddings','search':'mcp-search','forward':'mcp-forward-shape','shape':'mcp-forward-shape','ingest':'mcp-ingest','hop':'mcp-engine-process','text':'mcp-engine-process','translate':'mcp-engine-process','degrees':'mcp-engine-process','pull':'mcp-engine-process'}.get(short,'mcp-native-dispatch')
            if target:
                row['source_bodies'].insert(0,{'name':'Reviewed implementation UML','url':'traces.html#'+target})
                row['status']='related implementation path reviewed; nested semantics and acceptance remain separate'
        if row['family']=='conventional':
            row['source_bodies'].insert(0,{'name':'Reviewed command UML','url':'traces.html#conventional-'+row['name']})
            row['status']='command orchestration reviewed; external providers and acceptance remain separate'
        if row['surface']=='Engine CLI' and (OUT/'trace-diagrams'/('engine-'+row['name']+'.svg')).is_file():
            row['source_bodies'].insert(0,{'name':'Reviewed handler UML','url':'traces.html#engine-'+row['name']})
            row['status']='handler orchestration reviewed; nested semantics and acceptance remain separate'
        if row['family']=='uci':
            target={'catalog':'dispatch','connect':'dispatch','verify':'verify','serve':'serve','/health/live':'serve','/uci/{profile}':'serve'}[row['name']]
            row['source_bodies'].insert(0,{'name':'Reviewed connector UML','url':'traces.html#uci-'+target})
            row['status']='connector path reviewed; configured engine behavior remains separate'
        if row['family']=='monorepo' and row['name'] in ['/v1/chat/completions','/v1/completions','/v1/embeddings']:
            target={'/v1/chat/completions':'chat','/v1/completions':'completions','/v1/embeddings':'embeddings'}[row['name']]
            row['source_bodies'].insert(0,{'name':'Reviewed endpoint UML','url':'traces.html#monorepo-'+target})
            row['status']='endpoint orchestration reviewed; nested services and acceptance remain separate'
        if row['surface']=='ISA contract':
            row['source_bodies']=[{'name':s['name'],'url':'source.html#'+s['id']} for s in symbols if s['repo']=='Laplace-Refactor' and s['name'] in ['validate_'+row['name'],'execute_'+row['name']]]
            row['source_bodies'].insert(0,{'name':'Execute-wrapper UML','url':'isa.html#'+row['name']})
            row['source_bodies'].insert(0,{'name':'Program lifecycle UML','url':'traces.html#refactor-isa-execute'})
            row['status']='execute wrapper traced; nested semantics and acceptance remain separate'
data={'generated_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'kind':'source-registration inventory, not runtime acceptance','repositories':repositories,'coverage':coverage,'sources':sources,'operations':rows}
(OUT/'operations.json').write_text(json.dumps(data,indent=2,ensure_ascii=False),encoding='utf-8')

flows=json.loads((OUT/'flows.json').read_text(encoding='utf-8'))
for f in flows:
    for e in f['evidence']:
        ep=Path(e['path']); rel=ep.relative_to(ROOT); source(rel.parts[0],Path(*rel.parts[1:]).as_posix())
for f in flows:
    for r in rows:
        if r['family']==f['family'] and r['name']==f['operation'] and r['surface']==f['surface']:
            r['flow_id']=f['id'];r['status']='handler flow mapped; nested calls pending'
(OUT/'operations.json').write_text(json.dumps(data,indent=2,ensure_ascii=False),encoding='utf-8')

# Deterministic vector diagrams, with explicit decision edges and lane positions.
def svg(flow):
    nodes=flow['nodes']; width=1120; height=max(n['y'] for n in nodes)+110
    byid={n['id']:n for n in nodes}
    out=[f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" role="img" aria-label="{html.escape(flow["title"])}">', '<defs><marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" fill="#62758a"/></marker></defs>']
    for a,b,label in flow['edges']:
        p,q=byid[a],byid[b]; x1,y1=p['x']+150,p['y']+62;x2,y2=q['x']+150,q['y']
        if p['y']==q['y']: x1,y1=p['x']+300,p['y']+31;x2,y2=q['x'],q['y']+31
        mid=(y1+y2)/2
        if q['y'] < p['y']:
            x1,y1=p['x']+300,p['y']+31;x2,y2=q['x']+300,q['y']+31
            path=f'M{x1} {y1} L{x1+70} {y1} L{x1+70} {y2} L{x2} {y2}'
        else:path=f'M{x1} {y1} L{x1} {mid} L{x2} {mid} L{x2} {y2}'
        out.append(f'<path d="{path}" fill="none" stroke="#62758a" stroke-width="1.6" marker-end="url(#arrow)"/>')
        if label:out.append(f'<text x="{(x1+x2)/2+7}" y="{mid-7}" font-family="sans-serif" font-size="13" fill="#244966">{html.escape(label)}</text>')
    for n in nodes:
        fill={'decision':'#fff0d8','failure':'#ffe5e4','write':'#e9e2ff','end':'#dcefe7'}.get(n.get('kind'),'#e5edf4')
        out.append(f'<rect x="{n["x"]}" y="{n["y"]}" width="300" height="62" rx="8" fill="{fill}" stroke="#98a9b8"/>')
        lines=n['label'].split('|')
        for i,line in enumerate(lines):out.append(f'<text x="{n["x"]+150}" y="{n["y"]+25+i*19}" text-anchor="middle" font-family="sans-serif" font-size="14" fill="#14293c">{html.escape(line)}</text>')
    out.append('</svg>'); return ''.join(out)

(OUT/'diagrams').mkdir(exist_ok=True)
for f in flows:(OUT/'diagrams'/f'{f["id"]}.svg').write_text(svg(f),encoding='utf-8')
print(json.dumps({'registrations':len(rows),'flows':len(flows),'sources':len(sources)}))
