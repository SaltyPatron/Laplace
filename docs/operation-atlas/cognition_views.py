"""Monorepo completion program: obligations, provenance and fingerprints."""
def add_cognition_views(add):
    files=['Laplace/extension/laplace_substrate/src/cognition_program.c']
    add('monorepo-cognition-create','Monorepo — compile completion obligations','''start
:Validate tree, context, node projections and channel counts;
:Require one valid nonnull node/context entry per prompt origin;
:Collect eligible occurrences whose tree tier is at least two;
if (No declared operations?) then (yes)
 :Collect content-tag origins from supported aligned parses;
 if (No nonempty content set and eligible origins exist?) then (yes)
  :Scan HAS_POS for eligible forms;
  :Keep positive-weight best tag by rating minus twice RD, then ID;
  :Exclude whitespace and known noncontent tags;
  :Keep origins lacking a qualifying POS standing;
 endif
endif
:Union declared operand origins; reject out-of-range origins;
:Allocate owned program context; disposition OPEN;
if (No operations and nonempty content set?) then (yes)
 :Require content set;
else (no)
 :Require eligible union declared operand origins;
endif
if (Declared operations and nonempty operand union?) then (yes)
 :Copy operations and inputs;
 :Assign one input obligation per distinct input identity;
 :Require every prompt occurrence as well;
endif
if (Required set empty but prompt origins exist?) then (yes)
 :Require all prompt occurrences;
endif
:Seed semantic ancestry from required occurrence IDs and observation root;
:Seed COUPLE bindings only with their required origins;
:Process initial typed channels through semantic-channel admission;
:Fingerprint program response planes; return program;
stop''',files,'Creation and content-origin helpers reviewed. Empty content narrowing falls back to eligible/all origins, so an all-filtered set does not automatically mean no obligations. POS scan, parse admission, relation law and prompt compiler are external semantic boundaries.', ['20.1','20.9'])
    add('monorepo-cognition-close','Monorepo — semantic propagation and completion','''start
partition "For each semantic channel" {
 if (Geometry operand, nonpositive edge weight, nonsalient relation,
nontraversable direction or prompt-contract relation?) then (yes)
  :Reject ancestry propagation;
 else (no)
  if (Anchor has semantic origins?) then (yes)
   :Union anchor origins into candidate;
   :For matching declared result relation and exact operation input,
mark candidate as a result for that input obligation;
  endif
 endif
}
partition "For each emitted candidate" {
 :Append output ID, caller origins and semantic-support flag;
 :Increment semantic output count only when supported;
 if (Supported and caller origins and required origins exist?) then (yes)
  :Satisfy caller origins intersect selected ID ancestry intersect required;
 endif
 if (Supported and invocation obligations exist?) then (yes)
  :Satisfy selected ID's recorded input-result obligations;
  if (All invocation obligations satisfied?) then (yes)
   :Mark all occurrence obligations satisfied;
  endif
 endif
 :Recompute ordered output fingerprint;
 if (Not already complete, at least one semantic output,
all invocation and occurrence obligations satisfied?) then (yes)
  :Hash semantic act from domain + program ID + output fingerprint;
  :Set semantic act present, complete=true, disposition COMPLETE;
 endif
}
stop
legend bottom
Outbound typed channels can traverse; inbound requires a symmetric relation.
Route increments routing count only, and cannot itself satisfy an obligation.
finalize ignores complete programs and preserves AMBIGUOUS; otherwise sets
EXHAUSTED, BUDGET_EXHAUSTED or AMBIGUOUS (invalid disposition becomes EXHAUSTED).
note_emit has no complete/disposition guard: it can append after completion;
try_complete then leaves the existing semantic_act_id unchanged.
endlegend''',files,'Entire cognition_program.c reviewed. This graph describes local helper behavior; caller ordering determines reachable sequences. In particular note_emit can change output_fingerprint after completion while semantic_act_id stays fixed. Forward explicit-projection continuation must be considered separately before assigning an invariant to the final receipt.', ['20.9','20.10','20.11'])
    add('monorepo-cognition-identities','Monorepo — program, output, semantic-act and receipt identities',r'''rectangle "Program input / v9" as P {
 rectangle "Observation root\nExplicit invocation + active context\nRequired occurrence ordinals + IDs" as Required
 rectangle "COUPLE bindings\nStructural candidates\nGeometry candidates\nOrdered discourse\nInitial typed channels\nDeclared operations + inputs + witnesses" as Planes
}
rectangle "BLAKE3 program_id" as PID
rectangle "Output input / v2\nObservation root\nOrdered output IDs\nPer-output support flags + origins" as O
rectangle "BLAKE3 output_fingerprint" as OID
rectangle "semantic-act / v1\nprogram_id + output_fingerprint\nat first successful completion" as A
rectangle "BLAKE3 semantic_act_id" as AID
rectangle "Receipt\nAll three IDs\nRequired/satisfied counts\nRemaining=max(required-satisfied,0)\nRouting/output/semantic-output counts\nPresence flags, complete, disposition" as R
P --> PID
O --> OID
PID --> A
OID --> A
A --> AID
PID --> R
OID --> R
AID --> R
note bottom of P
Separate response planes stay separate in the encoding.
Geometry and structure are fingerprinted, not semantic ancestry.
Integer fields use explicit big-endian encoding.
Bindings, structure, geometry, initial channels and operations are sorted;
discourse and each operation's input order are retained.
end note
note bottom of R
Occurrence and input-identity obligation counts are summed.
Receipt copies in-memory state; this helper writes no durable receipt.
Program fingerprint is created once from initial response data.
Later channels are not rehashed into program_id by note_semantic_channels.
end note''',files,'Fingerprint helper bodies and receipt assembly reviewed. Hash identity is not proof of persisted replay inputs, database snapshot identity, runtime acceptance or independent provenance. Comparator tie/canonicalization guarantees and cross-platform hash representation are not established by this view.', ['20.11'])
