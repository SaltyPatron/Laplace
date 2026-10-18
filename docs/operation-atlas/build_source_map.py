"""Read-only syntax navigator alongside existing operation catalogs, not their replacement.

Calls are lexical references with candidate definitions, never asserted runtime dispatch.
Every excluded tracked source path and parse error is retained in the extraction receipt.
"""
from pathlib import Path
from atlas_paths import REPOSITORIES, TOOLS, PLANTUML, JAVA
import sys,json,subprocess,hashlib,re,importlib,collections
OUT=Path(__file__).resolve().parent
sys.path.insert(0,str(TOOLS/'parser-libs'))
from tree_sitter import Language,Parser
ROOT=REPOSITORIES
REPOSITORIES=['Laplace','Laplace-Refactor','Laplace-Native','Laplace-Engine','Laplace-postgres','Laplace-MCP','Laplace-Operations','Laplace-UCI','Laplace-Conventional']
LANG={'.c':'c','.h':'cpp','.cc':'cpp','.cpp':'cpp','.hpp':'cpp','.cs':'c_sharp','.py':'python','.js':'javascript','.mjs':'javascript','.ts':'typescript','.tsx':'tsx','.sh':'bash'}
parsers={};languages={};modules={}
for lang in ['c','cpp','c_sharp','python','javascript','typescript','tsx','bash']:
    mod=importlib.import_module('tree_sitter_'+('typescript' if lang=='tsx' else lang))
    factory=getattr(mod,'language_'+lang) if lang in ('typescript','tsx') else mod.language
    modules[lang]=mod;languages[lang]=Language(factory());parsers[lang]=Parser(languages[lang])
FUNCTIONS={'function_definition','function_declaration','method_declaration','constructor_declaration','local_function_statement','method_definition','arrow_function','lambda_expression','anonymous_method_expression'}
CALLS={'call_expression','invocation_expression','call','command'}
CONTROL={'if_statement','for_statement','for_in_statement','foreach_statement','for_each_statement','while_statement','do_statement','switch_statement','switch_expression','try_statement','catch_clause','finally_clause','return_statement','throw_statement','raise_statement','break_statement','continue_statement','goto_statement','yield_statement','if_expression','conditional_expression','case_statement','switch_section','elif_clause','else_clause','except_clause','with_statement','if','for','while','case'}
CONTAINERS={'compound_statement','block','statement_block','declaration_list','else_clause','elif_clause','catch_clause','finally_clause','except_clause','preproc_if','preproc_ifdef','preproc_else'}
files=[];symbols=[];skipped=[];revisions={};worktrees={};outdir=OUT/'source-data';outdir.mkdir(exist_ok=True)
def walk(node):
    yield node
    for child in node.named_children:yield from walk(child)
def text(node,raw):return raw[node.start_byte:node.end_byte].decode('utf-8',errors='replace') if node else ''
def fname(node,raw):
    n=node.child_by_field_name('name')
    if n:return text(n,raw)
    dec=node.child_by_field_name('declarator')
    if dec:
        while dec.child_by_field_name('declarator'):dec=dec.child_by_field_name('declarator')
        return text(dec,raw)
    parent=node.parent
    if parent:
        n=parent.child_by_field_name('name') or parent.child_by_field_name('left')
        if n:return text(n,raw)
    return '<anonymous@'+str(node.start_point.row+1)+'>'
def structure(node,raw):
    result=[]
    for n in node.named_children:
        if n.type in FUNCTIONS:continue
        if n.type in CONTROL:
            condition=n.child_by_field_name('condition') or n.child_by_field_name('value')
            body=n.child_by_field_name('body') or n.child_by_field_name('consequence')
            label=text(condition,raw) if condition else text(n,raw).split('\n')[0]
            if body and not condition:label=raw[n.start_byte:body.start_byte].decode('utf-8',errors='replace').strip()
            result.append({'kind':n.type,'line':n.start_point.row+1,'end_line':n.end_point.row+1,'label':label,'children':structure(n,raw)})
        elif n.type in CONTAINERS:result.extend(structure(n,raw))
        elif n.type in ('expression_statement','declaration','local_declaration_statement','assignment','augmented_assignment','expression_list','lexical_declaration','variable_declaration'):
            result.append({'kind':n.type,'line':n.start_point.row+1,'end_line':n.end_point.row+1,'label':text(n,raw),'children':[]})
        else:result.extend(structure(n,raw))
    return result
for repo in REPOSITORIES:
    print('Scanning',repo,flush=True)
    base=ROOT/repo
    revisions[repo]=subprocess.check_output(['git','-C',str(base),'rev-parse','HEAD'],text=True).strip()
    changed=set(subprocess.check_output(['git','-C',str(base),'diff','HEAD','--name-only','-z']).decode('utf-8').split('\0'))
    untracked=set(subprocess.check_output(['git','-C',str(base),'ls-files','--others','--exclude-standard','-z']).decode('utf-8').split('\0'))
    changed|=untracked;changed.discard('');worktrees[repo]=sorted(changed)
    paths=subprocess.check_output(['git','-C',str(base),'ls-files','--cached','--others','--exclude-standard','-z']).decode('utf-8').split('\0')
    for rel in filter(None,paths):
        p=base/rel;ext=p.suffix.lower()
        if ext not in LANG:
            if ext in ('.sql','.ps1','.in','.razor','.vue','.svelte'):skipped.append({'path':repo+'/'+rel,'reason':'language/template not parsed; retain separate catalog/source review'})
            continue
        if any(part in {'third_party','third-party','vendor','node_modules','obj','bin','dist'} for part in p.relative_to(base).parts):
            skipped.append({'path':repo+'/'+rel,'reason':'dependency/build tree excluded from first-party syntax extraction'});continue
        if not p.is_file():skipped.append({'path':repo+'/'+rel,'reason':'tracked path absent'});continue
        (OUT/'source-map-current.txt').write_text(repo+'/'+rel,encoding='utf-8')
        raw=p.read_bytes();tree=parsers[LANG[ext]].parse(raw)
        fileid=hashlib.sha256((repo+'/'+rel).encode()).hexdigest()[:20]
        record={'id':fileid,'repo':repo,'path':rel,'sha256':hashlib.sha256(raw).hexdigest(),'language':LANG[ext],'parse_error':tree.root_node.has_error,'definitions':[]}
        details=[]
        for node in walk(tree.root_node):
            if node.type not in FUNCTIONS:continue
            name=fname(node,raw);line=node.start_point.row+1
            symbol={'id':fileid+'-'+str(node.start_byte),'name':name,'repo':repo,'path':rel,'line':line,'end_line':node.end_point.row+1,'file':fileid,'kind':node.type,'parse_error':node.has_error,'working_tree_changed':rel in changed}
            calls=[]
            def inside(n):
                for c in n.named_children:
                    if c.type in FUNCTIONS:continue
                    yield c
                    yield from inside(c)
            for n in inside(node):
                if n.type in CALLS:
                    target=n.child_by_field_name('function') or n.child_by_field_name('name')
                    if target:calls.append({'name':text(target,raw),'line':n.start_point.row+1})
            body=node.child_by_field_name('body') or node
            details.append({**symbol,'calls':calls,'structure':structure(body,raw),'source':text(node,raw)})
            symbols.append(symbol);record['definitions'].append(symbol['id'])
        files.append(record)
        if details:(outdir/(fileid+'.js')).write_text('window.SOURCE_FILES['+json.dumps(fileid)+']='+json.dumps(details,ensure_ascii=False).replace('</','<\\/')+';',encoding='utf-8')
    print(repo, sum(s['repo']==repo for s in symbols), 'definitions',flush=True)
report={'method':'tree-sitter 0.25.2 syntax extraction; no runtime call resolution, no preprocessor configuration, no semantic/effect proof','revisions':revisions,'working_tree_changed_paths':worktrees,'files':files,'symbols':symbols,'excluded':skipped,'limitations':['SQL and PowerShell retain separate source/catalog coverage.','Lexical call names may resolve to zero, one or many candidates; even a unique spelling is not proven binding.','Function pointers, virtual dispatch, macros, generated code and runtime-loaded programs require further tracing.','Control tree preserves syntax nesting; it is not a complete control-flow graph and does not establish execution order across functions.','Tests and helpers are searchable definitions, not product capability counts.']}
(OUT/'source-map.json').write_text(json.dumps(report,ensure_ascii=False),encoding='utf-8')
print(json.dumps({'files':len(files),'definitions':len(symbols),'files_with_parse_errors':sum(f['parse_error'] for f in files),'excluded_paths':len(skipped)}))
