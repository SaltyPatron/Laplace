"""Build specification UML separately from code-traced handler diagrams."""
from pathlib import Path
from atlas_paths import REPOSITORIES, TOOLS, PLANTUML, JAVA
import json, hashlib, subprocess, html, re
ROOT=Path(__file__).resolve().parent
SRC=(REPOSITORIES / 'Laplace-Refactor/contracts/operation-model.json')
model=json.loads(SRC.read_text(encoding='utf-8-sig'))
OUT=ROOT/'diagrams'
base='@startuml\n!pragma layout smetana\nskinparam backgroundColor #FAFCFF\nskinparam shadowing false\nskinparam defaultFontName SansSerif\nskinparam defaultFontSize 14\nskinparam ArrowColor #385978\nskinparam componentBackgroundColor #E6EFF7\nskinparam classBackgroundColor #E6EFF7\n'
diagrams={}
def add(key,title,body,evidence):
    body=re.sub(r'"[^"\n]*(?:\n[^"\n]*)*"',lambda m:m.group(0).replace('\n',r'\n'),body)
    (OUT/(key+'.puml')).write_text(base+'title '+title+'\n'+body+'\n@enduml\n',encoding='utf-8')
    diagrams[key]={'title':title,'evidence':evidence,'status':'Specification model; arrows do not establish runtime acceptance.'}
add('machine','Laplace — whole-machine responsibilities', '''
actor "People / clients" as people
cloud "Sources / worlds / tools / target runtimes" as outside
package "Universal machine — specified responsibilities" {
 component "Interfaces and sessions" as ui
 component "Authority / scope / resource envelope" as gate
 component "Grammar + recipe compiler\nTyped AST / ISA / physical plan" as compiler
 component "Observe / admit / decompose" as ingest
 database "Canonical content DAG\nOrdered occurrences + source witnesses" as canonical
 database "Typed relations / testimony\nDependence + standing / immutable epochs" as evidence
 component "Derived indexes / geometry\nRemovable perfcache lattice" as derived
 component "Cognition\nRESOLVE → COUPLE → ORIENT → ROUTE\nSCAN → COMPOSE → PROPOSE → STEER\nSELECT → REALIZE → WITNESS" as cognition
 component "Realization\nRecompose / toolchain / external effect" as realize
 component "Execution receipts\nEffects / work / why-not dispositions" as receipt
 component "Domain recipes\nText · code · image · audio · video\nChess / games · models · applications" as domains
}
people --> ui
ui --> gate : request / selected world
gate --> compiler : effective scope + finite work
outside --> ingest : exact observations
compiler --> ingest : decomposition program
ingest --> canonical : identity + ordered structure
ingest --> evidence : witnessed admission
canonical --> derived : derive / rebuild
evidence --> derived : epoch-bound calculation
compiler --> cognition : goal program
canonical --> cognition : exact structure
evidence --> cognition : scoped evidence
derived --> cognition : acceleration
cognition --> realize : selected act
realize --> outside : output / effect
outside --> ingest : observed consequence
cognition --> receipt
realize --> receipt
domains ..> compiler : shared semantics
note bottom of gate
Authority is not epistemic standing.
Compute budget is not content identity.
end note
''',['Laplace/docs/specs/37_Substrate_Operation_ISA.md','Laplace/docs/CAPABILITIES.md','Laplace-Refactor/contracts/operation-model.json'])
add('identity','Laplace — conceptual identity and evidence model','''
class Entity {\n content_id : 128-bit\n}
class Trajectory
class Occurrence {\n ordinal\n repeat_count\n}
class Recipe
class SourceTrunk <<content>>
class FileTrunk <<content>>
class Testimony {
 outcome / games / score
 qualifiers / context
}
class Claim <<n-ary composition>>
class Standing {
 rating / deviation / volatility
 witness breadth / dependence
}
class Physicality <<realization>>
class Coordinate4D
class PackedCarrier {\n identity + ordinal + run + metadata\n}
class RealizedCurve
Entity -- Trajectory : ordered constituent structure
Trajectory "1" *-- "0..*" Occurrence : owns positions
Occurrence "0..*" --> "1" Entity : shared child
Recipe ..> Trajectory : chooses decomposition
Entity <|-- Claim
Entity <|-- SourceTrunk
Entity <|-- FileTrunk
SourceTrunk --> FileTrunk : file trunks in path order
SourceTrunk --> Entity : source record
FileTrunk --> Entity : metadata and content roots
Testimony --> SourceTrunk : attributable witness
Testimony --> Claim : confirmation / draw / refutation
Claim --> Entity : ordered arguments
Standing --> Claim : keyed by claim
Standing ..> Testimony : fold attributable evidence
Entity -- Physicality : typed realization
Physicality --> Coordinate4D : placement
Physicality --> Trajectory : constituent manifest
Occurrence --> PackedCarrier : packed representation
PackedCarrier --> Entity : unpack complete child identity
RealizedCurve ..> PackedCarrier : unpack and order
RealizedCurve --> Coordinate4D : resolve child placements
note bottom of Entity
Identity depends on content / ordered child IDs.
Recipe, tier, source and authority are not hash salt.
Shared children are not exclusively owned by parents.
end note
note bottom of SourceTrunk
The source trunk is the witness, not each file or annotator.
Containment provenance is the target; attestation rows remain
until a real-data prototype preserves their answers.
end note
note bottom of Testimony
Observation alone does not assert a semantic claim.
Repetition and shared lineage do not establish independence.
end note
note bottom of PackedCarrier
Packed binary64 fields are not geometric positions.
Geometry does not establish meaning or truth.
end note
''',['Laplace/docs/INVENTION.md','Laplace/docs/INVENTIONS.md','Laplace/docs/specs/08_Record_vs_Calculate_Spec.txt','Laplace/docs/specs/37_Substrate_Operation_ISA.md'])
add('request-cycle','Laplace — scoped request and consequence cycle','''
start
:Observe exact request root;
:Establish effective authority / world / epochs;
if (Authority and resources sufficient?) then (yes)
 :Compile recipe / ISA and bounded physical plan;
 :Estimate and reserve machine work;
 repeat
  :OP0 RESOLVE;
  :OP10 COUPLE within permitted scope;
  :OP1 ORIENT;
  :OP2 ROUTE;
  :OP3 SCAN;
  :OP4 COMPOSE;
  :OP5 PROPOSE;
  :OP6 STEER;
  :OP7 SELECT;
  if (Act available?) then (yes)
   :OP8 REALIZE;
   if (Actual outcome calls for witnessing?) then (yes)
    :OP9 WITNESS observed consequence;
   endif
   :Update state and execution receipt;
  else (no)
   :Typed unknown / ambiguity / why-not disposition;
  endif
 repeat while (Another bounded act required?) is (yes) not (no)
 :Reconcile actual work / unused reservation;
 :Return result, provenance and receipt;
else (no)
 :Deny / abstain / require stronger authority;
endif
stop
note right
Stable opcode numbers are not execution order.
Omission requires a satisfied precondition and trace.
Reads do not silently assert testimony.
end note
''',['Laplace/docs/specs/37_Substrate_Operation_ISA.md','Laplace/docs/CAPABILITIES.md'])
add('families','Laplace — three independently owned implementation families','''
package "Monorepo / laplace-mono" {
 component "Laplace\nApplication + substrate extension\nOwn setup / deploy" as mono
}
package "Refactor / laplace-refactor" {
 component "Laplace-Refactor\nContracts + native framework\nManaged / PostgreSQL bindings\nOwn setup / activation" as refactor
}
package "Modular / laplace" {
 component "Laplace-Operations\nInstallation ownership" as ops
 component "Laplace-Native" as native
 component "Laplace-Engine" as engine
 component "Laplace-postgres" as pg
 component "Laplace-MCP" as mcp
 ops ..> engine : install
 ops ..> pg : install
 ops ..> mcp : install
 engine ..> native : compiled native code
 pg ..> native : compiled native code
 mcp ..> native : compiled native code
 engine --> pg : libpq
 mcp --> pg : query paths
 mcp --> engine : selected child-process paths
}
component "Laplace-UCI\nLocal / SSH / WSS engine connector" as uci
component "Laplace-Conventional\nIndependent training pipeline" as conventional
note bottom of uci
Runs the configured UCI engine endpoint.
The connector does not establish which family supplies it.
end note
note bottom of mono
Identity implementation conflict recorded:
legacy hash prefix vs content-only governing law.
Do not infer cross-family identity equivalence.
end note
note bottom of refactor
Integration, activation, world admission,
seed completion and release are separate states.
end note
''',['workspace README','Laplace/engine/core/src/hash128.c','Laplace-Refactor/contracts/operation-model.json','Laplace-Engine/src','Laplace-MCP/native/api.c'])
for name,steps in model['runtime_cycles'].items():
    body='start\nrepeat\n'+'\n'.join(':'+s+';' for s in steps)+'\nrepeat while (Further authorized work / evidence?) is (yes) not (no)\nstop'
    add('cycle-'+name,name.replace('_',' ').title()+' — specified cycle',body,['Laplace-Refactor/contracts/operation-model.json#/runtime_cycles/'+name])
stages=model['stages']; ids={s['id']:'s'+str(i) for i,s in enumerate(stages)}
body=[]
for s in stages:
    body.append('component "'+s['id']+'\\n['+s.get('implementation',{}).get('state','unspecified')+']" as '+ids[s['id']])
for s in stages:
    for dep in s.get('depends_on',[]):
        if dep in ids: body.append(ids[s['id']]+' ..> '+ids[dep]+' : requires')
body.append('note "Capability dependencies and ownership.\\nNOT runtime execution order or acceptance proof." as warning')
add('capability-dependencies','Laplace Refactor — complete declared capability dependency graph','\n'.join(body),['Laplace-Refactor/contracts/operation-model.json#/stages'])
for s in stages:
    key='stage-'+s['id'].replace('.','-')
    body=['component "'+s['id']+'" as operation']
    for direction,field in [('in','consumes'),('out','produces')]:
        for i,value in enumerate(s.get(field,[])):
            alias=direction+str(i)
            body+=['rectangle "'+value.replace('"',"'")+'" as '+alias,alias+' --> operation' if direction=='in' else 'operation --> '+alias]
    add(key,s['id']+' — declared input/output contract','\n'.join(body),['Laplace-Refactor/contracts/operation-model.json#/stages/'+s['id']])
from whole_system_views import add_whole_system_views
add_whole_system_views(add)
machine={'source':str(SRC),'sha256':hashlib.sha256(SRC.read_bytes()).hexdigest(),'semantics':model['graph_semantics'],'stages':stages,'diagrams':diagrams}
(ROOT/'machine.json').write_text(json.dumps(machine,indent=2),encoding='utf-8')
java=JAVA
subprocess.run([java,'-Djava.awt.headless=true','-jar',str(PLANTUML),'-tsvg',str(OUT/'*.puml')],check=True)
print(json.dumps({'diagrams':len(diagrams),'declared_stages':len(stages),'output':'machine.json'}))
