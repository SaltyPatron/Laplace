"""Attributable observations and folded consensus are separate reads."""
def add_evidence_read_views(add):
    root='Laplace/extension/laplace_substrate/src/'
    def view(id,title,body,files,note):
        add('monorepo-evidence-'+id,'Monorepo — '+title,body,[root+f for f in files]+['Laplace/engine/core/src/sql_catalog.def'],note,[])
    view('observations','attributable witness read and occurrence roles','''start
:Validate one-dimensional bytea identity filters and 16-byte IDs;
:Require operands and callback; roles 0..3;
if (No roles or any supplied set empty?) then (yes)
 :Return no rows;
 stop
endif
:Connect nested SPI; create temporary memory context;
:Deduplicate operand probes while retaining linked lists of every original ordinal;
if (Exact cells supplied?) then (yes)
 :Deduplicate subject/type/object triples;
 :Query only exact attestation cells, with optional source filter;
else (endpoint query)
 :Query selected subject/object roles with optional source/type filters;
endif
:Stream each row through typed DestReceiver; require identity/outcome/count;
:Preserve nullable object/source/context and 32-byte qualifier mask;
:For matching subject call visitor for every operand occurrence with role 1;
:For matching object call visitor for every operand occurrence with role 2;
:Reject a fetched row outside supplied operand roles;
:Release temporary memory and nested SPI;
stop''',['observation_read.c','observation_read.h'],'Complete implementation and static SQL reviewed. No result limit, rank or source standing is introduced by this reader. Duplicate operands share a query probe but preserve occurrence callbacks; self-reference can appear in both endpoint roles without reversing the stored claim. NULL filters are unrestricted; explicitly empty filters yield no witnesses. The SQL-facing materialized wrapper returns ten columns and omits the qualifier mask carried by native callbacks.')
    view('consensus','snapshot, permissions and partition selection','''start
:Require at least one endpoint set;
:Validate/deduplicate subject, object and type arrays;
if (Any supplied set empty?) then (yes)
 :Return without opening consensus table;
 stop
endif
:Resolve laplace.consensus; require root SELECT ACL;
:Reject enabled row security rather than bypass it;
if (Default-only scan?) then (yes)
 :Require partitioned root and existing default partition; otherwise return;
endif
while (Next selected relation?) is (yes)
 :Open with AccessShareLock; check interrupts;
 if (Partitioned?) then (yes)
  :Read current topology;
  :For single bytea subject/type LIST or HASH key, select matching partitions;
  :Otherwise visit all children;
 else (leaf)
  :Choose complete, endpoint-ranked or typed-plane scan;
 endif
 :Close relation while retaining transaction lock;
endwhile (done)
:Release identity sets;
stop''',['consensus_scan.c','consensus_scan.h','consensus_plane_scan.h'],'Complete consensus scan source and typed-plane include reviewed. Scans use GetActiveSnapshot, not a new snapshot or independently authenticated tenant. SELECT ACL/RLS checks are on the root; relation locks are retained through the transaction. Source/context testimony is absent from folded consensus rows and must be read separately.')
    view('leaf','complete keyed scan and ranked fallback','''start
:Require expected identity and standing column types;
:Prefer subjects as probe endpoint; otherwise objects;
if (Typed-plane mode and matching typed rank index exists?) then (yes)
 :Delegate endpoint/type ranges;
 stop
endif
:For endpoint-ranked mode seek valid ready B-tree with effective-mu descending expression;
if (No compatible ordered index?) then (yes)
 :Choose valid endpoint B-tree with longest constrained object/type prefix;
 :Reject absence of endpoint index;
 :Disable cutoff and scan complete matching keyed set;
else (ordered)
 :Scan an independent ordered range for each endpoint;
endif
:Read under active snapshot; filter exact endpoint/type sets;
:Validate stored identities and complete rating/RD/volatility/witness count;
if (Ordered range and callback cutoff true?) then (yes)
 :End this endpoint range before invoking consumer for that row;
else (no)
 :Stream row to consumer; continue;
endif
:Release scan, slot and index lock;
stop''',['consensus_scan.c','consensus_scan.h'],'Complete index recognition and leaf read reviewed. Only absent predicates or the specifically admissible object-IS-NOT-NULL predicate are accepted. Ranked access recognizes rating − 2*RD by expression, not index name. Callback cutoff correctness belongs to its caller. A typed-plane cutoff is never applied to the endpoint-wide fallback.')
    view('planes','independent relation-plane cutoffs','''start
:Require compatible endpoint ASC, type ASC, effective-mu DESC B-tree;
:Create discovery and ranked scans under active snapshot;
while (Next operand endpoint?) is (yes)
 :Start type seek at empty byte string lower bound;
 while (Next stored relation type exists?) is (yes)
  :Seek strictly past prior type; validate 16-byte relation identity;
  if (Type inside hard caller scope?) then (yes)
   :Open exact endpoint/type ordered range;
   :Filter endpoint sets and exclude unary rows; validate standing;
   :Apply caller cutoff only to this type range;
   :Stream retained rows to consumer;
  endif
 endwhile (done)
endwhile (done)
:Close both scans and release temporary lower-bound/type values;
stop''',['consensus_scan.c','consensus_plane_scan.h'],'Complete plane-index recognition and range traversal reviewed. Types are discovered from stored keys rather than a fixed semantic roster. Empty byte string is an index lower bound, not a minted entity. Reaching one type cutoff moves to the next type, preserving independent response planes. No ranked index means complete keyed fallback without this cutoff.')
