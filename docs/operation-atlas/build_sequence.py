"""Project the existing Wiki operation structure; never invent operation contracts."""
from pathlib import Path
from atlas_paths import REPOSITORIES, TOOLS, PLANTUML, JAVA
import re,json,hashlib,subprocess,html
OUT=Path(__file__).resolve().parent
WIKI=(REPOSITORIES / 'Laplace-Wiki')
revision=subprocess.check_output(['git','-C',str(WIKI),'rev-parse','HEAD'],text=True).strip()
catalog=(WIKI/'README.md').read_text(encoding='utf-8-sig')
listed=re.findall(r'^\s*- \[[^\]]+\]\(([^)#]+\.md)\)',catalog,re.M)
pages=[]
for rel in dict.fromkeys(listed):
    path=WIKI/rel
    if not path.is_file(): continue
    body=path.read_text(encoding='utf-8-sig')
    pages.append({'path':rel,'title':body.splitlines()[0].lstrip('# '),'sha256':hashlib.sha256(path.read_bytes()).hexdigest(),'bytes':path.stat().st_size,'text':body})
stages=[];conflicts=[]
for page in pages:
    if not page['path'].startswith('Sequence/') or page['path'].endswith('README.md'):continue
    text=page['text']; operations=[]
    if page['path'].endswith('Conflicts.md'):
        for m in re.finditer(r'^### (.+)\n([\s\S]*?)(?=^### |\Z)',text,re.M):
            conflicts.append({'title':m[1],'text':m[2].strip(),'line':text.count('\n',0,m.start())+1,'path':page['path']})
        continue
    for m in re.finditer(r'^### (\d+\.\d+) (.+)\n([\s\S]*?)(?=^#{1,3} |\Z)',text,re.M):
        fields={}
        for f in re.finditer(r'^- \*\*(In|Do|Out|From):\*\* ([\s\S]*?)(?=^- \*\*|\Z)',m[3],re.M):fields[f[1]]=f[2].strip()
        operations.append({'id':m[1],'title':m[2],'fields':fields,'missing_fields':[k for k in ['In','Do','Out','From'] if k not in fields],'text':m[3].strip(),'line':text.count('\n',0,m.start())+1,'path':page['path'],
                           'references':sorted(set(re.findall(r'\b\d{1,2}\.\d+\b',m[3])))})
    title=page['title']; num=int(re.match(r'(\d+)\.',title)[1])
    stages.append({'id':str(num),'title':title,'path':page['path'],'operations':operations,'introduction':text.split('### ')[0]})
stages.sort(key=lambda s:int(s['id']))
allids={o['id'] for s in stages for o in s['operations']}
for s in stages:
    for o in s['operations']:o['references']=[r for r in o['references'] if r in allids and r!=o['id']]
data={'revision':revision,'source':'Laplace-Wiki','stages':stages,'conflicts':conflicts,'pages':pages,
      'semantics':'Exact source projection. Numbered order comes from Wiki Sequence; references are references, not inferred execution edges. Refactor dependencies are separate. Source projection does not prove implementation or reconcile contradictions.'}
data['reconciliation']=json.loads((OUT/'reconciliation.json').read_text(encoding='utf-8'))
data['reviewed_paths']=[{'id':t['id'],'title':t['title'],'operations':t['spec_operations'],'boundary':t['boundary']} for t in json.loads((OUT/'traces.json').read_text(encoding='utf-8'))]
(OUT/'sequence.json').write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
diagram_dir=OUT/'sequence-diagrams';diagram_dir.mkdir(exist_ok=True)
for s in stages:
    lines=['@startuml','skinparam backgroundColor #FAFCFF','skinparam shadowing false','skinparam defaultFontName SansSerif','skinparam activityBackgroundColor #E6EFF7','title '+s['title']+' — Wiki specified order','start']
    for o in s['operations']:
        label=html.escape(o['id']+' '+o['title']).replace(';',',')
        lines.append(':'+label+';')
    lines+=['stop','legend','Source: Laplace-Wiki/'+s['path'],'Revision: '+revision[:12],'Sequential projection of numbered source operations.','Branches, iteration and obligations are in the linked contracts.','Not a code trace or runtime acceptance result.','endlegend','@enduml']
    (diagram_dir/(s['id']+'.puml')).write_text('\n'.join(lines),encoding='utf-8')
java=JAVA
subprocess.run([java,'-Djava.awt.headless=true','-jar',str(PLANTUML),'-tsvg',str(diagram_dir/'*.puml')],check=True)
print(json.dumps({'stages':len(stages),'operations':sum(len(s['operations']) for s in stages),'conflicts':len(conflicts)}))
