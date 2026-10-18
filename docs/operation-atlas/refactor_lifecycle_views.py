"""Refactor framework, ISA and effect lifecycle; independent of older families."""
def add_refactor_lifecycle_views(add):
    framework='Laplace-Refactor/engine/src/framework.c'
    isa='Laplace-Refactor/engine/src/isa.c'
    pg='Laplace-Refactor/integrations/postgresql/extension/src/laplace_pg.c'
    add('refactor-producer','Refactor — finite production, cancellation and replay verification','''start
:Initialize receipt and optional artifact output;
:Fingerprint context; require source and recipe pointers;
:Validate producer/control/sink callbacks, ABI, flags and reserved fields;
:Call producer.prepare; mark producer as prepared before call;
:Require nonzero plan batches, records, bytes and type;
:Fingerprint plan; initialize stream and prefix hashers;
:Build zero-progress checkpoint;
if (Replay checkpoint supplied?) then (yes)
 :Validate checksum, bounds, type, context, source, recipe, producer and plan;
 if (Replay has zero completed batches?) then (yes)
  :Require exact initial checkpoint ID; mark replay verified;
 endif
endif
:Check cancellation; publish initial progress; check cancellation;
:Begin sinks;
while (More planned batches?) is (yes)
 :Check cancellation; call producer.next with batch index;
 :Validate nonempty bytes and records, contiguous ordinal, type, flags and overflow;
 :Require accumulated counts within plan;
 :Stage batch to every sink;
 :Update stream/prefix hashes and checkpoint with cursor;
 if (This is requested replay boundary?) then (yes)
  :Require exact checkpoint ID; mark replay verified;
 endif
 :Publish progress;
endwhile (no)
:Require exact final record/byte totals and requested replay verification;
:Check cancellation; call producer.finish;
:Finalize stream hash; seal sinks;
:Set STAGED_INERT; hash stream and producer receipts; return OK;
stop
legend bottom
Any failed check/callback jumps to: abort begun sinks; abort prepared producer;
set EFFECT_NONE and failure status; hash failure receipts; return.
Replay starts at batch zero and stages the prefix again; it is verified regeneration,
not skipping directly to a saved offset. Checkpoint comparison follows sink staging.
Cancellation is polled at shown boundaries, not injected into provider callbacks.
endlegend''',[framework], 'run_producer_impl and its wrappers reviewed. Producer, sink and progress callback implementations control external effects. Failure EFFECT_NONE is a framework disposition, not independently established rollback of arbitrary providers. Checkpoint hash field construction and provider-specific resumability remain separate.',[])
    add('refactor-stream-stage','Refactor — canonical stream staging and sink failures','''start
:Require receipt; zero it and set no-failure indices;
:Validate and clear optional per-sink artifact output;
:Validate and fingerprint execution context;
:Validate stream shape, flags, batches and size;
:Bind source and recipe fingerprints;
:Fingerprint canonical stream and compute type / record / byte totals;
:Require every sink callback and supported ABI;
while (More sinks to begin?) is (yes)
 :Call sink.begin; count this sink as begun even if begin fails;
 if (Begin fails?) then (yes)
  :Abort all begun sinks including failed sink;
  :Hash failure receipt and return;
  stop
 endif
endwhile (no)
while (More batches?) is (yes)
 :Send batch to each sink in array order;
 if (Stage fails?) then (yes)
  :Record batch/sink index; abort all begun sinks;
  :Hash failure receipt and return;
  stop
 endif
endwhile (no)
:Seal sinks sequentially using stream fingerprint;
if (Seal fails?) then (yes)
 :Clear artifact output; abort all begun sinks;
 :Hash failure receipt and return;
 stop
endif
:Hash ordered sink artifacts;
:Set STAGED_INERT and OK; hash stream receipt; return;
stop''',[framework], 'Stream wrapper and sink callback lifecycle reviewed. The framework calls abort but cannot establish provider rollback success because abort returns void. Sealing a sink is staging, not activation; provider implementation and storage durability remain distinct. Normal behavior excludes named mutation-test macros.',[])
    add('refactor-activation','Refactor — admit, commit or abort staged effects',r'''state "Staged inert artifact + receipt" as Staged
state "Admission checks" as Check
state "Provider preparation" as Prepare
state "ACTIVATION_ADMITTED\nOK receipt" as Admitted
state "Provider commit" as Commit
state "ACTIVATED\nOK receipt" as Activated
state "STAGED_INERT\nOK aborted receipt" as Aborted
state "STAGED_INERT\nadmission failed receipt" as AdmissionFailed
state "Commit failed receipt\nprovider abort called" as CommitFailed
state "Rejected request / context / authority / provider\nhashed failure receipt" as Rejected
[*] --> Staged
Staged --> Check : admit_staged_stream
Check --> Rejected : invalid context, staged receipt binding, request,\nread-only flag, expected epoch or provider ABI
Check --> Prepare : valid request; expected epoch differs from next
Prepare --> AdmissionFailed : prepare fails / call provider.abort
Prepare --> Admitted : prepare succeeds / retain preparation fingerprint
Admitted --> Commit : commit after validation
Commit --> CommitFailed : commit fails / call provider.abort
Commit --> Activated : commit succeeds / fingerprint and rehash receipt
Admitted --> Aborted : abort after validation
note bottom of Admitted
Normal admission does not commit.
activate_staged_stream is convenience admit then commit.
Commit/abort validate receipt, context and epoch request before provider calls.
Abort clears the activation fingerprint; validation failure returns unchanged.
end note
note bottom of Activated
Framework receipt records provider return and fingerprints.
External effects, atomicity, compensation and durable epoch publication
depend on the concrete provider; the framework cannot infer them.
end note''',[framework], 'Admission, commit, abort, admitted-receipt validation and committed-receipt identity validation bodies reviewed. Context binding and expected-epoch checks apply in normal builds. Explicit named test mutations can remove checks or commit during admission and are not represented as production behavior.',[])
    add('refactor-context','Refactor — execution-context decoding and fingerprint','''start
:PostgreSQL adapter zeroes native context;
:Require one-dimensional bytea array with exact epoch count;
:Require nonnull fixed-size epoch and authority digests;
:Read memory, CPU, IO, mask, version and flags;
:Reject nonpositive memory/CPU/mask, negative IO/version/flags;
:Native validator checks supported version, known flags and mask,
zero reserved field, nonzero memory and CPU grant;
:Require every absent epoch slot to have canonical zero payload;
:Accept context representation;
:Hash domain, version, flags, mask, each present epoch index and digest,
authority fingerprint, memory bytes, CPU slots and IO slots;
:Return context fingerprint;
stop
legend bottom
Normal native build does not reject zero payload in a PRESENT epoch or authority digest.
The LAPLACE_TEST_REJECT_ZERO_CONTEXT_VALUES mutation changes that behavior.
This validator checks representation; it does not query an authority registry
or measure host resources. The grant and authority are supplied context fields.
endlegend''',[framework,pg], 'Context decoder, native validation and fingerprint bodies reviewed. Declared authority and resources must not be equated with independently verified permission or available hardware merely because they hash successfully. Downstream admission and provider checks are separate.',[])
    add('refactor-isa-validate','Refactor — whole-program preflight and buffer separation','''start
:Clear error; validate pointers and nonempty program;
:Validate framework context;
:Check ISA version, exact flags, zero reserved and FULL receipt detail;
:Reject array-size overflow and value_count beyond uint32;
while (More values?) is (yes)
 :Require known type, exact flags, zero reserved, count at most capacity;
 :Validate capacity span: data, stride, multiplication and pointer overflow;
endwhile (no)
while (More instructions?) is (yes)
 :Check flags and register indices; forbid same input/output register;
 :Binary-search operation registry by opcode;
 :Check introduced minor version, instruction version and input/output types;
 :Invoke operation-specific validator;
endwhile (no)
:Check each output capacity range against its input count range;
:Check pairwise output/output and both output/input overlaps;
:Return OK;
stop
legend bottom
First validation failure returns typed status and relevant instruction/value indices.
Zero-length spans do not overlap. Input/input overlap is permitted here.
Normal build validates every instruction; a named test mutation validates only the first.
Operation-specific validation and resource arithmetic remain separate paths.
endlegend''',[isa,framework], 'Top-level validator, value spans, instruction registry lookup and global-range checks reviewed. Pairwise checks mean these public instruction buffers are not an unrestricted in-place dataflow chain. Successful preflight does not itself execute or commit an operation.',[])
    add('refactor-isa-execute','Refactor — execute program and seal successful receipt','''start
if (Receipt pointer absent?) then (yes)
 :Return INVALID_ARGUMENT;
 stop
endif
:Zero receipt; copy program version/detail/count if program present;
:Validate entire program;
if (Validation fails?) then (yes)
 :Set receipt status; return failure;
 stop
endif
:Fingerprint context and program;
:Fingerprint inputs;
if (Input fingerprint cannot be built?) then (yes)
 :Return RESOURCE_INSUFFICIENT;
 stop
endif
while (More instructions?) is (yes)
 :Resolve binding and execute handler;
 if (Handler fails?) then (yes)
  :Receipt reports prior completed instruction count;
  :Return failure with current instruction and output index;
  stop
 endif
 :Advance executed instruction count;
endwhile (no)
:Fingerprint outputs;
if (Output fingerprint cannot be built?) then (yes)
 :Return RESOURCE_INSUFFICIENT after execution;
 stop
endif
:Set OK; hash receipt; clear error; return OK;
stop
legend bottom
Earlier output mutations can remain on later failure; this function has no rollback.
Failed receipt branches are not sealed with hash_receipt.
receipt_validate accepts only supported FULL successful all-instructions receipts
whose recomputed receipt ID matches; it does not rerun program or verify persistence.
endlegend''',[isa,framework], 'Execute orchestration and receipt validator reviewed. Operation-specific hash_inputs/hash_outputs encodings and nested validators are still separate review boundaries. Fourteen execution wrappers are linked in the ISA viewer; normal build behavior is distinguished from named test mutations.',[])
