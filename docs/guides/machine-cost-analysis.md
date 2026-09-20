# Machine-cost analysis and measured cognition

Laplace's cost model is intended to derive computational work from explicit program and hardware structure, not merely infer it from historical latency.

## Artifact-to-machine chain

~~~text
source
-> grammar AST
-> compiler/linker artifacts
-> bytecode / object / executable container
-> functions / symbols / basic blocks
-> decoded machine instructions
-> control-flow graph
-> data/dependency graph
-> execution-count variables
-> target ISA
-> target microarchitecture/scheduling model
-> memory/initial-state assumptions
-> clock
-> resource-constrained cycles
-> machine time
~~~

A JAR has another explicit boundary:

~~~text
JAR / class files
-> JVM bytecode
-> selected JVM/version/mode
-> interpreter/JIT transformation
-> generated machine code
-> target processor
-> cycles
~~~

Laplace must not invent a direct "JVM opcode = N CPU cycles" shortcut unless the selected runtime model actually supports that claim.

## Symbolic state is correct state

If an execution count is unknown, retain it.

~~~text
loop iterations = N
cycles(N) = setup + N * body + branch/cache terms
~~~

If cache state, branch history, I/O service time or scheduler interference is unknown, those terms remain explicit/symbolic/conditional.

A precise-looking scalar based on a benchmark average is less truthful than a symbolic expression whose assumptions are visible.

## Instruction cost is not simple addition

Modern processors overlap work. The analyzer must account for dependency latency, issue/dispatch width, execution resources, throughput, micro-op expansion, load/store resources, branch behavior, cache/memory hierarchy and scheduling constraints where the target model exposes them.

Two independently scheduled instructions do not necessarily cost the sum of their individual latencies. The execution dependency DAG and resource model own the result.

## Current implementation foothold

The current MachineCostAnalyzer already accepts a built executable/object artifact without executing it, uses LLVM disassembly, rejects undecoded instructions, schedules the recovered stream through llvm-mca for a declared target CPU/triple, and reports instruction count, scheduled instances, cycles, uops, IPC, throughput, resource pressure and calculated time.

Its receipt explicitly labels the result as a linearized executable-section/named-symbol schedule and marks it as not control-flow weighted.

That is truthful partial implementation. It is not the completed invention.

The full path must add control-flow/data-flow/dependency-aware dynamic execution counts and richer machine/runtime state.

## Calculated versus observed

~~~text
given:
  artifact A
  exact path/counts P
  ISA I
  microarchitecture M
  initial/memory state S
  clock F

derive:
  C cycles
  T seconds
~~~

An observed receipt says what happened on a real host. Residual error can tug cache assumptions, branch behavior, frequency scaling, OS scheduling, memory latency, JIT/compiler differences and omitted machine semantics.

Benchmarking validates/calibrates the model. It does not replace derivable work with empirical authority.

## Billing Laplace itself

~~~text
request
-> routed forward/operation program
-> semantic work counts:
     hops
     fanout/frontier
     candidates/responders
     providers/operators
     trajectory/geometry/calculation work
-> physical implementation plan
-> target-machine cost estimate/expression
-> reserve hard ceiling
-> execute
-> actual receipt
-> reconcile/refund
~~~

This is why product levels are compute depth/breadth rather than model quality.

A smart question can be cheap if it resolves quickly. A simple question can be expensive if the caller requests exhaustive analysis.

## Semantic work versus implementation waste

Receipts retain useful semantic work separately from physical work such as cycles, instructions, database rows/pages, I/O and boundary crossings.

If a bad implementation performs thousands of avoidable SQL/SPI/PInvoke crossings, that is an optimization defect. It must not become permanent customer pricing authority.

## Required receipt identity

A serious machine-cost claim binds exact source/artifact, analyzer/toolchain generation, target ISA, target CPU/microarchitecture, control-flow/count assumptions, memory/cache/initial-state assumptions, clock/frequency assumptions, exact/symbolic output expression, calculated receipt identity, and observed host/provider receipt when calibration is performed.

This makes the estimate inspectable and reproducible within the declared boundary.


## Current implementation owners

- #1709 — control/data/dependency-aware dynamic execution counts and target-microarchitecture cycle derivation.
- #1431 — calculated-versus-observed execution/resource receipts and calibration evidence.
- #1561 — query/cognition preflight-versus-actual work receipts for billing/capacity.
