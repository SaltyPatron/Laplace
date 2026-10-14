"""Prompt intent planes, occurrence provenance and witnessed invocation compilation."""
def add_intent_views(add):
    files=['Laplace/extension/laplace_substrate/src/prompt_intent.h']
    def view(id,title,body,note):
        add('monorepo-intent-'+id,'Monorepo — '+title,body,files,note,[])
    def shape(id,title,body,note):
        add('monorepo-shape-'+id,'Monorepo — '+title,body,files+['Laplace/extension/laplace_substrate/src/task_shape.c','Laplace/engine/core/src/sql_catalog.def'],note,[])
    shape('discover','discover and verify task-shape structures','''start
:Require nonempty observation forms, available structure and positive fanout;
:Propagate structure budget exhaustion to intent;
:Resolve exact canonical IS_EXAMPLE_OF/CALLS/HAS_INPUT/HAS_PARSE relations;
:Require every protocol relation inside caller hard scope;
:Read type-8 trajectories containing observation forms and parse-schema constituent;
:Read vertex-flagged parse candidates in a second membership pass;
:For each candidate require expected physicality ID;
:Decode all trajectory constituents and flags;
if (Every constituent has parse flag?) then (yes)
 :Decode vertex parse; recompute canonical ID; require entity identity matches;
else (flat)
 :Require unflagged constituents and canonical ordered Merkle identity;
 :Try task-shape codec; otherwise try flat UD parse codec;
endif
:Read IS_EXAMPLE_OF observations on retained parses;
:Read nominated shape trajectories and their CALLS/HAS_INPUT declarations;
:Read HAS_PARSE witnesses for exemplar parses;
:Probe consensus cells; sort observations by witness ID;
:Read stored entity type IDs for every coupled binding;
stop''','Complete PostgreSQL task_shape.c orchestration/helpers reviewed. Type-8 trajectory structure is checked before using it as a parse or shape. The second vertex-only membership read ignores its completeness return; first membership overflow and witness overflow set read.failed. Exact native codec/matcher implementations and membership query semantics remain nested.')
    shape('contract','task-shape evidence and observed-parse compatibility','''start
while (Next IS_EXAMPLE_OF observation?) is (yes)
 :Require confirming, positive-standing, unrefuted claim in source/context;
 :Require decoded shape points to decoded exemplar and predicate is allowed;
 :Require a supported positive HAS_PARSE witness for exemplar;
 :Read same-source/context declarations on shape;
 if (Any CALLS differs from predicate or any HAS_INPUT is unknown/unsupported?) then (yes)
  :Reject shape contract;
 elseif (CALLS and every declared slot witnessed?) then (yes)
  :Match observation forms against exemplar shape;
  :Check every supported current parse against shape constraints;
  if (Any supported current parse conflicts?) then (yes)
   :Mark intent ambiguous; do not instantiate this shape;
  elseif (Projection matches?) then (yes)
   :Instantiate once per supported current parse;
   :If none exists, instantiate from exact observation forms;
  endif
 endif
endwhile (done)
if (Any bounded read/combination failed?) then (yes)
 :Set budget_exhausted; discard entire operation set;
endif
:If operations remain, mark explicit_invocation;
:Compare predicate and ordered per-input identities/origins; mark differing alternatives ambiguous;
stop''','Complete shape_contract, positivity checks and compiler loop reviewed. Shape CALLS/slot witnesses share example source/context; exemplar HAS_PARSE support is selected separately. Any same-scope refutation blocks the matching positive cell. Unlike the explicit prompt compiler comparison, shaped inputs compare ordered slots and per-slot origin sets. No inference is accepted merely from a parser mood or label.')
    shape('instantiate','typed slot alternatives and calculated projection identity','''start
:Match current parse or observation forms to shape; resolve exact slot ordinals;
:For a current parse require an unrefuted confirming witness;
while (Next input slot?) is (yes)
 :Resolve exact observation occurrence origin;
 if (current_form slot?) then (yes)
  :Use observed form itself only if its stored type matches slot type;
 else (bound concept)
  :Collect coupled identities with this origin and matching stored type;
 endif
 :Sort choices by identity;
 :Empty choices yield no invocation;
 :Reject choice or Cartesian-product envelope beyond fanout;
endwhile (done)
:Require existing operation count plus combinations within fanout;
while (Next combination and no overflow?) is (yes)
 :Append predicate, shape, source/context, call/example/exemplar witnesses;
 :Record each selected input, slot witness and exact singleton origin;
 if (No supported current parse?) then (yes)
  :Calculate request-projection ID from domain, shape, exemplar,
root, ordered forms/origins and selected inputs;
  :Do not write a HAS_PARSE observation;
 else (parse exists)
  :Retain current parse ID and selected witness;
 endif
endwhile (done)
stop''','Complete instantiation, append and projection-ID helpers reviewed. Slot compatibility reads laplace.entities.type_id, not realized labels. A calculated projection is in-memory recipe identity; it is not newly witnessed parse evidence. Identity serialization uses native hash128 representations plus big-endian 32-bit counts; cross-platform canonical-byte acceptance is not established here. Matcher and shape-codec contracts remain separate.')
    view('begin','whole observation and structural provenance','''start
:Create intent owned by request memory context; retain exact input root and hard relation scope;
:Call prompt_structure_couple; validate occurrence/node projection counts;
:Bind each nonnull occurrence ID to its origin ordinal;
:Bind whole root to all valid current occurrence origins;
if (Trajectory scope supplied?) then (yes)
 :Read container/constituent/predecessor/successor/co-occurrence crossings
from root plus current occurrence IDs;
 :Merge ordered continuations as a distinct root-sourced structural relation;
 :Envelope = fanout * (source_count * 5 + 1), saturating on overflow;
 if (Complete structural response exceeds envelope?) then (yes)
  :Mark budget_exhausted; do not bind a retained prefix;
 else (within envelope)
  :Stage target origins from existing source bindings;
  :Merge staged origins; new identities form structural frontier;
 endif
endif
:Read root geometry responses after structural state is retained;
:Return intent with distinct structural and geometry planes;
stop''','Complete prompt_intent.h reviewed. Prompt structure parsing and native trajectory readers remain nested. The structural envelope is checked after complete native responses are read. Staging prevents the same structural pass from propagating through newly added targets. Discourse binding separately validates one-dimensional 16-byte IDs and retains order without granting current-observation origins.')
    view('geometry','root angular, Hilbert and realized-curve discovery','''start
if (fanout <= 0?) then (yes)
 :Return empty geometry frontier;
 stop
endif
:Prepare/cache three SPI plans; locate whole root in admitted tree;
:Angular KNN: same tier, excluding root, nonnull normalized directions;
:Take fanout nearest by chord; return angular metric ordered by chord then ID;
:Hilbert: read up to fanout equal, predecessor and successor keys each;
:Require 128-bit keys; compute exact unsigned absolute differences;
:Sort by difference then ID; retain fanout Hilbert responses;
if (Root has at least two immediate children and angular candidates exist?) then (yes)
 :Realize root curve from immediate ordered child coordinates;
 :Read structural.entity_curve for root angular candidates;
 :Calculate 4D Frechet distances; sort by distance then ID; limit fanout;
endif
:Append separate typed metric responses with source, rank and distance/key delta;
:Deduplicate candidate identities into geometry frontier;
stop''','All local geometry SQL and helpers reviewed. Only whole-root geometry is queried, not every occurrence. Frechet refines the angular candidate population, not the Hilbert population or all stored entities. SQL inner angular LIMIT orders by chord alone; the later ID tie-break does not determine membership among a boundary tie. Metric/curve implementation and index acceptance remain separate; these candidates are not testimony.')
    view('compile','witnessed CALLS and HAS_INPUT contract','''start
:Mark explicit invocation; store active context;
:Enumerate complete governed CALLS and HAS_INPUT relation families;
if (Any declaration family outside caller hard scope?) then (yes)
 :Return without compiling a partial protocol;
 stop
endif
:Read all root declarations; retain role-one rows with object/source/context,
positive occurrences and exact active context;
if (Distinct retained witnesses exceed fanout?) then (yes)
 :Finish set read; mark budget_exhausted; compile no retained prefix;
 stop
endif
:Probe consensus for retained subject/relation/object cells;
:Mark each cell positive only when edge weight > 0; sort witnesses by ID;
while (Next CALLS declaration?) is (yes)
 if (Confirm, positive standing, no same-source/context refutation?) then (yes)
  :Propose result relation and preserve call witness/source/context;
  :Inspect every HAS_INPUT declaration in same source/context;
  if (Any input unbound, not confirm, nonpositive or refuted?) then (yes)
   :Discard whole proposed invocation;
  elseif (At least one valid input?) then (yes)
   :Retain each input witness and occurrence origins;
   :Append invocation and union of operand origins;
  endif
 endif
endwhile (done)
:Mark ambiguous if any two invocations differ in result relation,
input-identity membership or union of operand origins;
stop''','Complete explicit compiler and declaration callbacks reviewed. Consensus positivity is keyed by claim cell; individual source/context governs declaration pairing and refutation. Refutations match source/context/type/object, without timestamp precedence in this helper. same_inputs compares identity membership and the union of origins, not ordered operands, multiplicity or per-operand origin correspondence. Generic task-shape compilation is a separate caller branch.')
    view('couple','one naming hop and exact occurrence origins','''start
:Create pending binding table;
if (Supported parse alternatives and structure budget not exhausted?) then (yes)
 :For each aligned token with valid origin, stage lemma ID and exact origin;
endif
while (Next supplied channel?) is (yes)
 if (Positive edge weight and governed naming/interpretation relation?) then (yes)
  :Permit HAS_NAME/HAS_SENSE/IS_LEMMA_OF/EVOKES_FRAME families;
  :Permit only root CORRESPONDS_TO, not argument-role descendants;
  if (Anchor has existing current-observation origins?) then (yes)
   :Stage candidate with anchor origins;
  endif
 endif
endwhile (done)
:After reading all channels, merge staged origin sets into intent bindings;
:Return identities whose origin set grew;
stop''','Complete naming-coupling helper reviewed. Pending additions are merged only after channel traversal, so one call is one witnessed naming hop; it cannot cascade through freshly created origins in the same pass. Supported lemma projection does not itself establish a speech act. Hard relation filtering and channel orientation are caller/read boundaries, not newly enforced in this helper.')
    view('continuations','merge ordered structural continuations','''start
:Read scoped continuations of exact ordered input context;
if (No continuations?) then (yes)
 :Return zero;
 stop
endif
:Check count/allocation capacity; index existing structural routes
by source, target and relation mask;
:Reject duplicate existing structural route keys;
while (Next continuation?) is (yes)
 :Find/create key = root, continuation identity, CONTINUATION;
 :Require continuation relation; reject negative count or int64 overflow;
 :Add occurrence count; nearest gap at most one;
endwhile (done)
:Release temporary index; return native continuation count;
stop''','Entire merge helper reviewed. Counts remain structural occurrence counts, not source trust or attestations. Underlying scoped trajectory matching and order expansion remain separately reviewed boundaries. Route identity includes source and relation, so an equal target on another structural plane is not silently merged.')
