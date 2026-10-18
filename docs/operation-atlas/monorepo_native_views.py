"""Reviewed walk_continuations orchestration; typed semantic helpers stay explicit."""
def add_monorepo_native_views(add):
    def view(name,title,body,note,ops):
        add('monorepo-'+name,'Monorepo native — '+title,'start\n'+body+'\nstop',
            ['Laplace/extension/laplace_substrate/src/trajectory_generate.c'],note,ops)
    view('forward-setup','retained state, coupling and orientation','''
:Require non-null context; read step, stride, spread, topK, fanout and seed;
:Validate nonnegative limits, finite nonnegative spread and ID arrays;
:Initialize materialized result;
if (Steps or topK zero?) then (yes)
 :Return empty result;
 stop
endif
:Connect SPI; copy non-null ordered context within allocation bounds;
if (No context remains?) then (yes)
 :Finish SPI; return empty result;
 stop
endif
:query_operands assigns context, semantic frontier and discourse roles;
:Create retained query state; assign occurrence provenance;
:Create route_seen and geometry_routed sets;
if (Stride positive?) then (yes)
 :Bind supplied observation/containing scopes and prompt input;
endif
if (Prompt input supplied?) then (yes)
 :Emit resolve when tracing;
 :Begin prompt intent; bind prior discourse;
 :Extend structural frontier with PHYSICALITY role and prompt origins;
 :Extend geometry frontier with GEOMETRY role and root origins;
 repeat
  :Couple retained channels into prompt intent;
  if (No changes or coupling hop ceiling reached?) then (yes)
   break
  endif
  :Preserve origins; collect previously unseen IDs;
  :Increment coupling hops; extend query state in one batch;
 repeat while (true) is (next round)
 if (Invocation context supplied?) then (yes)
  :Compile applicable prompt-intent program;
 elseif (No explicit output-relation projection?) then (yes)
  :Compile task shape;
 endif
 :Use compiled intent only when invocation or no explicit projection;
 if (Unambiguous, within budget and relation operations present?) then (yes)
  :Collect allowed predicates and selected input IDs;
  :Extend unseen inputs; create separate output query state;
 endif
 :Create cognition program from input, channels and selected intent;
 :Finalize ambiguous or budget-exhausted intent before emission;
 :Emit orient when tracing;
endif
if (No output state, no invocation context, explicit projection supplied?) then (yes)
 :Create output query state from prompt root or active operands;
endif
:Create per-round scratch and geometry summaries;
:Define open-turn status; enter election loop;
''','Reviewed caller control flow, not the internals of prompt intent, task-shape compilation or typed query-state construction. Coupling and routing use separate hop counters under the same ceiling. Geometry and physicality retain distinct operand roles; geometry does not become testimony by entering working state.', ['20.1','20.2','20.3'])
    view('forward-round','proposal, exact evidence and candidate retention','''
:Loop only while emission steps remain and intent is not blocked;
:Reset round scratch; check PostgreSQL interrupts; read remaining obligations;
:Create candidate index;
if (Stride positive and context tail nonempty?) then (yes)
 :Read scoped trajectory continuations;
 :First prompt step uses full context; later steps cap tail at stride;
 :Nominate IDs with occurrence counts/stride and inherited sequence origins;
endif
:Nominate geometry responders not yet promoted through routing;
:Read bounded semantic proposal summaries;
if (Prompt input and relation intent or routing budget?) then (yes)
 :Nominate positive traversal IDs not already routed;
endif
if (Output query state exists?) then (yes)
 :Nominate positive projected results;
 :Skip new nominations already in ordered context;
endif
:Re-add carried candidates absent from ordered context;
:Emit propose stage when tracing;
if (No candidates?) then (yes)
 :Mark exhausted; leave election loop;
 stop
endif
:SCAN exact typed cells between candidates and active query operands;
:Keep both positive and refuting evidence in summaries;
:Mark eligible bound-operation results from exact channels;
:Read exact projection channels if output state exists;
:Emit scan stage;
:Record semantic completion provenance before structural ancestry merge;
:Propagate candidate origins; attach query/traversal/projection/geometry summaries;
:Keep candidates with sequence occurrence, positive typed evidence or geometry;
:Emit compose stage;
if (No candidates remain?) then (yes)
 :Mark exhausted; leave election loop;
 stop
endif
if (Explicit observation scope has unopposed sequence candidates?) then (yes)
 :Retain only those unopposed scoped continuations;
endif
:Sort by candidate_compare tuple; count output-eligible candidates;
:Emit steer stage; pass candidates to route/select decision;
''','Evidence nomination is bounded, but candidate adjudication requests exact typed cells through a separate helper. This diagram does not certify those SQL/native helper implementations. Completion provenance is recorded before structural origins are merged, so structural ancestry is not itself a semantic completion certificate.', ['20.5','20.6','20.7','20.8'])
    view('forward-election','route or select and update working state','''
:Determine whether an open-turn candidate covers all remaining obligations;
:Compute remaining routing budget independently of emission steps;
if (No output candidate and no routing budget?) then (yes)
 :Mark exhausted; leave election loop;
 stop
endif
if (No output candidate, or incomplete meeting with routing budget?) then (route)
 :Increment routing hop; notify cognition program;
 :Emit route trace for candidates;
 :For incomplete meeting, retain outputtable candidates for later rounds;
 :Mark routed IDs and promoted geometry responders;
 :Extend query and output states in batches;
 :Repeat proposal/SCAN at same emission step;
 stop
endif
:Keep output-eligible candidates;
if (Open turn?) then (yes)
 :Reorder leading candidates by sharing specificity;
endif
:Limit election to min(candidate count, topK);
if (Spread zero?) then (yes)
 :Select first candidate;
else (seeded ordinal draw)
 :For each retained rank i, draw u and compute -i/spread - log(-log(u));
 :Select largest key;
endif
if (No selection?) then (yes)
 :Mark exhausted; leave election loop;
 stop
endif
:Emit select stage;
:Append selected ID to ordered context and route_seen;
:Add occurrence provenance; fail on ordinal overflow;
:Extend query/output state with selected ID;
:Update selected trajectory scope, distinguishing ordered continuation;
:Notify cognition program of emitted ID and typed semantic support;
:Emit result after state transition; increment emission step;
if (Receipt complete and no explicit output-relation projection?) then (yes)
 :Leave election loop;
else (no)
 :Repeat while steps remain and intent permits;
endif
''','The draw acts on ordinal rank after typed election; it is not a probability distribution over the evidence fields. candidate_can_output excludes geometry-only evidence, naming-only channels and contract metadata from direct semantic output. Full obligation closure and sharing/summary comparators remain nested helpers.', ['20.4','20.8','20.9','20.12'])
    view('forward-terminal','terminal receipt and cleanup','''
:Election loop ends by completion, exhaustion, limit or blocked intent;
if (Cognition program exists?) then (yes)
 :Read current receipt;
 if (Incomplete and disposition still OPEN?) then (yes)
  if (Not exhausted and output count reached step limit?) then (yes)
   :Finalize BUDGET_EXHAUSTED;
  else (no)
   :Finalize EXHAUSTED;
  endif
 endif
 if (Trace requested?) then (yes)
  :Emit terminal row at output_count + 1;
  :Event is complete if receipt.complete, otherwise unresolved;
  :Keep more specific disposition as a separate receipt field;
  :Include program, obligations, output count and optional fingerprints/semantic act;
 endif
endif
:Delete round scratch; destroy output/query/cognition states;
:Free routing index, origin bitmaps and retained ordered context;
:Finish SPI; return materialized result;
''','This is normal control-flow cleanup; PostgreSQL ERROR/interrupt paths are not translated here into ordinary terminal rows. Terminal event name and specific disposition are different fields. The surrounding SQL realization gate consumes this receipt; persistent response witnessing happens later in the managed HTTP path.', ['20.10','20.11','20.12'])
    view('forward-comparator','typed candidate election order','''
:Compare positive output-projection summaries;
if (Tie?) then (yes)
 :Compare positive traversal summaries;
 if (Tie?) then (yes)
  :Compare positive query summaries;
  if (Tie?) then (yes)
   :Prefer longer structural stride;
   if (Tie?) then (yes)
    :Prefer more trajectory occurrences;
    if (Tie?) then (yes)
     :Compare query opposition, then traversal opposition,
then projection opposition;
     if (Tie?) then (yes)
      :Compare typed geometry summary;
      if (Tie?) then (yes)
       :Compare ID bytes;
      endif
     endif
    endif
   endif
  endif
 endif
endif
:Return first non-tied comparison;
''','candidate_compare uses a lexicographic ordering, not a product of evidence, sequence and geometry scores. positive_summary_compare and opposition_summary_compare internals remain deeper boundaries. Open-turn sharing refinement and seeded ordinal selection occur after this comparator.', ['20.8','20.9'])
