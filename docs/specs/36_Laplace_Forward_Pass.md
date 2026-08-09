# The Laplace forward pass

## Purpose

Laplace uses one stateful, typed execution program for conversation, code, games,
model-scoped queries, and export. API adapters bind requests to this program; they do
not implement rival generation paths.

## Canonical program

`RESOLVE → ORIENT → ROUTE → SCAN → COMPOSE → PROPOSE → STEER → SELECT → REALIZE → WITNESS`

### RESOLVE

Resolve exact content entities, session/context identity, source scope, and requested
output contract. Unicode decomposition and tier ascent are deterministic.

### ORIENT

Construct task and discourse state from the ordered session trajectory, current input,
declared policy, and relevant witnessed dependencies.

### ROUTE

Elect typed relation families, salience bands, modalities, circuit planes, and operation
constraints. Routing is evidence-bearing and may retain multiple hypotheses.

### SCAN

Discover bounded candidates through exact identity, containment, indexes, perfcache,
PostGIS/Hilbert locality, source/context filters, and typed graph operations. Approximate
geometry may propose candidates but cannot establish final identity or truth.

### COMPOSE

Build the active typed frontier from candidate evidence, trajectories, factors, tiers,
relation bands, and uncertainty. Corroborating and conflicting witnesses remain visible.

### PROPOSE

Produce legal/grammatical/typed next constituents or actions. Proposal may combine
corpus continuity, graph traversal, model-circuit testimony, code grammar, tool results,
or domain operators without committing to an answer.

### STEER

Apply task, discourse, source scope, A*/hop/fan-out constraints, ordinal continuity,
Glicko confidence, observed outcomes, and residual/frontier state to rerank proposals.

### SELECT

Select under an explicit deterministic or stochastic policy. The selected item must be
supported by the declared evidence and constraints. Pooled model consensus is consumed
here as standing state; N external model answers are never adjudicated here.

### REALIZE

Render the selected entity/action into the requested surface without using rendering to
reclassify it. Realization is batchable and preserves exact identity.

### WITNESS

Append the turn/action/tool/result and its receipt through the governed write lane.
Calculated outcomes identify analyzer/tool version. Reads alone do not witness.

## Stateful emission

Every emitted constituent updates the active frontier, residual state, ordinal context,
and session trajectory before the next constituent is proposed. Building a semantic
frontier once and draining it without feedback is not a conforming forward pass.

## Heterogeneous source consensus

Checkpoint sources, corpora, tools, user feedback, and domain observations meet through
canonical content and typed evidence. A single pass can run source A, source B, or
pooled A+B for diagnosis. Pooled mode produces one answer path from pre-folded evidence,
not runtime voting or a judge.

## Code lane

Code generation uses the same program with grammar/AST trajectories and toolchain
operations. Generated code is staged as content, compiled/tested under declared tools,
and the outcomes are witnessed before a subsequent decision can learn from them.

## Trace contract

Each pass exposes a bounded semantic trace: resolved ids, scope, route, candidate counts,
evidence cells, scores/uncertainty, selected item, realization, state transition, and
writes. MCP, HTTP, streaming, and export must agree at this level.

## Acceptance

- Multi-turn correction, anaphora, topic return, and abstention work after restart.
- Each emitted constituent changes the next-step state.
- Equivalent MCP/OpenAI calls share semantic traces.
- Code generate/compile/test feedback affects a later pass.
- Incompatible model sources participate in one pooled answer.
- No external answer-writing LLM or GPU is required.
