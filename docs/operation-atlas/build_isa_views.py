"""Refactor opcode wrappers, following the existing ISA contract and reviewed bodies."""
from pathlib import Path
from atlas_paths import REPOSITORIES, TOOLS, PLANTUML, JAVA
import json,subprocess,html,hashlib
OUT=Path(__file__).resolve().parent;REPO=(REPOSITORIES / 'Laplace-Refactor')
contract=json.loads((REPO/'contracts/isa.json').read_text())
symbols=json.loads((OUT/'source-map.json').read_text(encoding='utf-8'))['symbols']
views={}
def put(name,body,notes):views[name]={'body':body,'notes':notes}
for name,ctype,callee,scalar in [
 ('identity_codepoint_batch','codepoint and ID','laplace_identity_codepoint_batch','laplace_identity_codepoint'),
 ('highway_coordinate_calculate_batch','highway key and coordinate','laplace_highway_coordinate_calculate_batch','laplace_highway_coordinate_calculate')]:
 put(name,f'''if (Exact strides, aligned buffers and count fits size_t?) then (yes)
 :Call {callee};
 if (Native status not OK?) then (yes)
  :Return EXECUTION_FAILED;
  stop
 endif
else (no)
 while (More input elements?) is (yes)
  :Copy strided input to local {ctype} operand;
  :Call {scalar};
  if (Native status not OK?) then (yes)
   :Return EXECUTION_FAILED;
   stop
  endif
  :Copy result into strided output;
 endwhile (no)
endif
:Set output.count = input.count;''','The fallback is explicitly a scalar loop. This source behavior is distinct from the governing batch-first requirement. Buffer mutations can precede a later failure.')
put('trajectory_composition_decode_batch','''
:Set logical ordinal = 1;
while (More carriers?) is (yes)
 :Copy carrier from strided input;
 :laplace_trajectory_composition_decode_one(carrier, logical ordinal);
 if (Decode status not OK?) then (yes)
  :Return EXECUTION_FAILED;
  stop
 endif
 :Copy occurrence to strided output;
 :Advance logical ordinal by occurrence.run_length;
endwhile (no)
:Set output.count = input.count;''','The wrapper decodes one carrier at a time; it does not infer a coordinate from packed fields.')
put('highway_registry_materialize_batch','''
while (More input elements by count?) is (yes)
 :laplace_highway_registry_materialize(program.context);
 if (Native status not OK?) then (yes)
  :Return EXECUTION_FAILED;
  stop
 endif
 :Copy receipt to output element;
endwhile (no)
:Set output.count = input.count;''','The execute wrapper uses the input count and shared context; its loop does not read the input element value.')
for name,callee in [('evidence_record_testimony_batch','laplace_evidence_record_testimony_batch'),('source_profile_validate_batch','laplace_source_profile_validate_batch'),('world_admission_close_batch','laplace_world_admission_close_batch')]:
 put(name,f'''
if (Input stride or alignment requires a copy?) then (yes)
 :Calculate contiguous input bytes;
 if (Bytes exceed context memory grant?) then (yes)
  :Return RESOURCE_INSUFFICIENT;
  stop
 endif
 :Allocate contiguous input;
 if (Allocation fails?) then (yes)
  :Return RESOURCE_INSUFFICIENT;
  stop
 endif
 :Copy strided input records;
endif
:Clear native receipt and error;
:Call {callee} once over records;
:Free temporary input;
if (Native status not OK?) then (yes)
 :Return EXECUTION_FAILED;
 stop
endif
:Copy native receipt to output[0];
:Set output.count = 1;''','Whole-program validation runs before dispatch. Native semantic processing and its own effects remain a separate nested operation.')
put('evidence_calculate_standing_batch','''
if (Input stride or alignment requires a copy?) then (yes)
 :Allocate contiguous input;
 if (Allocation fails?) then (yes)
  :Return RESOURCE_INSUFFICIENT;
  stop
 endif
 :Copy standing inputs;
endif
:Clear result and error;
:laplace_standing_calculate_period_batch;
:Free temporary input;
if (Native resource failure or overflow?) then (yes)
 :Return RESOURCE_INSUFFICIENT;
 stop
elseif (Other non-OK status?) then (yes)
 :Return EXECUTION_FAILED;
 stop
endif
:Copy one result to output[0];
:Set output.count = 1;''','This wrapper does not itself compare temporary allocation size to the context grant; validator and native working-set checks must be examined separately.')
for name,callee,cardinality,error_map in [
 ('evidence_record_lineage_batch','laplace_evidence_record_lineage_batch','native returned count','cycle -> DEPENDENCE_CYCLE; native resource failure -> RESOURCE_INSUFFICIENT'),
 ('reference_topology_resolve_batch','laplace_reference_topology_resolve_batch','input.count','native memory failure -> RESOURCE_INSUFFICIENT'),
 ('reference_mapping_resolve_batch','laplace_reference_mapping_resolve_batch','input.count','native memory failure -> RESOURCE_INSUFFICIENT')]:
 overflow='''if (Temporary-byte addition overflows?) then (yes)
  :Free temporary input; return RESOURCE_INSUFFICIENT;
  stop
 endif''' if name!='evidence_record_lineage_batch' else ''
 put(name,f'''
:Use existing aligned contiguous buffers where available;
if (Input requires a copy?) then (yes)
 :Calculate temporary input bytes; allocate input buffer;
 if (Allocation fails?) then (yes)
  :Return RESOURCE_INSUFFICIENT;
  stop
 endif
 :Copy strided input;
endif
if (Output requires a copy?) then (yes)
 {overflow}
 :Account temporary output; allocate contiguous output;
 if (Allocation fails?) then (yes)
  :Free temporary buffers; return RESOURCE_INSUFFICIENT;
  stop
 endif
endif
if (Temporary bytes exceed memory grant?) then (yes)
 :Free temporary buffers; return RESOURCE_INSUFFICIENT;
 stop
endif
:Clear native receipt/error; call {callee};
if (Native status not OK?) then (yes)
 :Free temporary buffers;
 :Map typed native error; otherwise EXECUTION_FAILED;
 stop
endif
if (Temporary output used?) then (yes)
 :Scatter output records back to strided storage;
endif
:Free temporary buffers;
:Set output.count = {cardinality};''',error_map+'. Lineage passes the remaining memory grant to its native call. Allocation/copy helpers and native solver are separate drill-downs; output cardinality differs by operation.')
put('stock_recipe_compile_catalog_batch','''
:Compute working bytes for recipes and perfcache planes;
if (Input requires contiguous copy?) then (yes)
 if (Working-byte addition overflows?) then (yes)
  :Return RESOURCE_INSUFFICIENT;
  stop
 endif
 :Add copy bytes; allocate input buffer;
 if (Allocation fails?) then (yes)
  :Return RESOURCE_INSUFFICIENT;
  stop
 endif
 :Copy catalog items;
endif
if (Working bytes exceed memory grant?) then (yes)
 :Free temporary input; return RESOURCE_INSUFFICIENT;
 stop
endif
:laplace_stock_recipe_compile_catalog_items;
:Free temporary input;
if (Native resource failure or overflow?) then (yes)
 :Return RESOURCE_INSUFFICIENT;
 stop
elseif (Other non-OK status?) then (yes)
 :Return EXECUTION_FAILED;
 stop
endif
:Copy one catalog receipt; set output.count = 1;''','Working-set formula and checks are wrapper behavior; this does not establish that every source grammar or recipe is supported.')
put('cognition_solve_packet','''
if (Program, instruction or values absent?) then (yes)
 :Return INVALID_ARGUMENT;
 stop
endif
:laplace_cognition_packet_execute_words(request, result capacity);
if (Native result-capacity status?) then (yes)
 :Return RESULT_CAPACITY_INSUFFICIENT;
 stop
elseif (Other non-OK status?) then (yes)
 :Return EXECUTION_FAILED;
 stop
endif
:Set output.count = native result word count;''','The preceding validator checks packet shape, required result words, matching packet/execution context fingerprint and request+result memory budget. The packet’s suboperations are not expanded in this wrapper diagram.')
put('universal_ast_apply_packet','''
if (Isolated mutation-test build disables AST execution?) then (yes)
 :Return INPUT_OUT_OF_RANGE;
 stop
endif
if (Program, instruction or values absent?) then (yes)
 :Return INVALID_ARGUMENT;
 stop
endif
:laplace_universal_ast_packet_validate_words;
if (Native memory failure?) then (yes)
 :Return RESOURCE_INSUFFICIENT;
 stop
elseif (Other non-OK status?) then (yes)
 :Return INPUT_OUT_OF_RANGE;
 stop
endif
:Encode AST packet receipt;
:Set output.count = LAPLACE_UNIVERSAL_AST_RECEIPT_WORDS;''','Despite the opcode name “apply”, this wrapper calls packet_validate_words and encodes a receipt. The diagram does not infer a canonical-state mutation from the name.')
assert set(views)==set(contract['opcodes'])
folder=OUT/'isa-diagrams';folder.mkdir(exist_ok=True)
for name,v in views.items():
    v['opcode']=contract['opcodes'][name];v['contract']=contract['operation_contracts'][name]
    v['handlers']=[s for s in symbols if s['repo']=='Laplace-Refactor' and s['name'] in ['validate_'+name,'execute_'+name]]
    assert any(s['name']=='execute_'+name for s in v['handlers']),name
    v['evidence']=[{'path':p,'sha256':hashlib.sha256((REPO/p).read_bytes()).hexdigest()} for p in sorted({s['path'] for s in v['handlers']})]
    puml='@startuml\nskinparam backgroundColor #FAFCFF\nskinparam shadowing false\nskinparam defaultFontName SansSerif\ntitle Refactor ISA — '+name+'\nstart\n:Entry after whole-program validation;\n'+v['body']+'\n:Return ISA_OK;\nstop\nlegend\nExecute-wrapper source trace.\nValidation and native semantic callees are separate boundaries.\nNo runtime acceptance asserted.\nendlegend\n@enduml\n'
    (folder/(name+'.puml')).write_text(puml,encoding='utf-8')
java=JAVA
subprocess.run([java,'-Djava.awt.headless=true','-jar',str(PLANTUML),'-tsvg',str(folder/'*.puml')],check=True)
(OUT/'isa-views.json').write_text(json.dumps(views,indent=2),encoding='utf-8')
print(json.dumps({'opcodes':len(views),'missing_execution_wrappers':0}))
