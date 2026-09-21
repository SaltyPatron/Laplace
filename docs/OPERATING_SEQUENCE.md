# The complete operating sequence

This is the one walkthrough of how Laplace operates end to end: from a selected source artifact to a witnessed turn on a served surface, with the proof boundary named for each kind of claim.

It sequences and links; it does not redefine anything. The invention is governed by [`INVENTION.md`](INVENTION.md) and synthesized in [`CAPABILITIES.md`](CAPABILITIES.md); as-built architecture, including named divergences, is [`ARCHITECTURE.md`](ARCHITECTURE.md). Binding stage semantics live exactly once in [`specs/36_Laplace_Forward_Pass.md`](specs/36_Laplace_Forward_Pass.md) and [`specs/37_Substrate_Operation_ISA.md`](specs/37_Substrate_Operation_ISA.md) and are deliberately not restated here. Current divergences are named in the [deviation ledger](audits/INVENTION_DEVIATION_LEDGER_2026-09-19.md); this walkthrough does not maintain a second divergence list.

## The sequence at a glance

```text
selected source estate
        |
        v
1. ADMISSION      ingest: decompose -> compose -> converge -> persist -> fold -> receipt
        |
        v
2. DURABLE STATE  entities / physicalities / attestations / consensus (one world)
        |
        v
3. OPERATION      the forward program over that state (one canonical sequence)
        |
        v
4. SURFACES       chat / MCP / OpenAI / CLI / SQL / chess bind the same program
        |
        v
5. WITNESS        governed append turns outcomes back into durable state
        |
        +--> the next observation re-couples against the changed world

cross-cutting: execution grain - authority/capability - resource envelopes/receipts
```

## 1. Admission — ingestion is the learning process

A logical source has many releases, files and sidecars; every selected physical artifact gets an explicit disposition (admitted, equivalent packaging, superseded, excluded-with-reason, unsupported-with-why-not). Silent non-enumeration is invalid.

```text
artifact graph enumeration
-> streamed read/parse (native kernels; qualified parsers are provider evidence)
-> typed decomposition (grammar/recipe per modality)
-> recursive composition under one identity law (equal content converges)
-> working-set dedup / bulk existence / COPY persistence
-> set-sized evidence fold (attestations -> consensus)
-> receipt/journal completion (physical files and semantic units reported separately)
```

- Boundary law: [`plan/INGEST_BOUNDARY_AND_RECIPE_LAW.md`](plan/INGEST_BOUNDARY_AND_RECIPE_LAW.md); as-built spine: [`ARCHITECTURE.md`](ARCHITECTURE.md) §5 (`IngestPipeline`, `IngestRunner`, `ConsensusAccumulatingWriter`).
- State-class law: [`specs/05_Substrate_Invariants.txt`](specs/05_Substrate_Invariants.txt), [`specs/08_Record_vs_Calculate_Spec.txt`](specs/08_Record_vs_Calculate_Spec.txt).
- Current owners: source estate #1403, decomposer normalization #1177, native-source fidelity #1153, generic parallel apply #967, recipe-driven ingest #1045.

## 2. Durable state — what admission leaves behind

Four primary families persist the world (see [`ARCHITECTURE.md`](ARCHITECTURE.md) §1–4):

```text
entities       canonical content identity (Merkle hash over the ordered child ids)
physicalities  typed realization: coord, Hilbert address, packed trajectory (exact manifest)
attestations   source-attributed typed testimony (confirm/draw/refute; absence != false)
consensus      folded standing (Glicko-2 rating/RD/volatility/witnesses)
```

These are different things and must not collapse:

```text
identity != physicality != occurrence != testimony != consensus != calculation
coord (real placement) != packed trajectory (212-bit constituent manifest) != realized curve
```

Perfcaches (T0 codepoints, highway, number roots, chess floors, ...) are derived, rebuildable ROM accelerators over this state — [`specs/33_Perfcache_Blob_Law.md`](specs/33_Perfcache_Blob_Law.md). A miss falls back to canonical composition; a cache is never semantic authority.

## 3. Operation — one forward program

Binding semantics: spec 36 (stage contracts), spec 37 (operation ISA). Current read/cognition execution contract: [`read-path.md`](read-path.md). As-built entry: `generation.forward_program`, one native program (`pg_laplace_forward_trace`) — see [`ARCHITECTURE.md`](ARCHITECTURE.md) §8.

```text
OP0 RESOLVE   admit the exact observation/root, occurrences, bindings, obligations, scope
OP10 COUPLE   typed response field: what responds, by which indexed route, with what force
OP1 ORIENT    joint interpretation from the coupling field (ambiguity is a valid state)
OP2 ROUTE     compile the cognition program + work envelope (policy follows interpretation)
OP3 SCAN      bounded indexed star expansion (hops/fanout are the compute coordinates)
OP4 COMPOSE   fold the responding frontier; corroborating/conflicting witnesses stay typed
OP5 PROPOSE   legal/typed next constituents, semantic acts, actions
OP6 STEER     apply query-relative task/discourse/authority/obligation state
OP7 SELECT    select under the declared policy with retained receipt state
OP8 REALIZE   render the selected act to the requested surface (an authority boundary)
OP9 WITNESS   append the governed turn/action/result when the contract calls for it
```

Emission is stateful: every emitted constituent/act changes the active trajectory and re-couples before the next selection. A*, Dijkstra, walks, continuation, containment, geometry, chess search and deterministic calculators are operators inside this program — none of them is the cognition. Current implementation owner: #1401.

## 4. Surfaces — one program, many bound fronts

`converse.chat`, the OpenAI-compatible endpoint, MCP tools, CLI query commands, the SQL operator schemas (`ops`, `converse`, `generation`, `lexical`, `taxonomy`, `chess`, ...) and the chess lab bind requests to the same program; they do not implement rival cognition paths. Equivalent semantic requests must execute equivalent programs and agree at the trace-contract level (spec 37). Conversation provenance law: [`specs/34_Conversational_Provenance.md`](specs/34_Conversational_Provenance.md).

## 5. Cross-cutting law

- **Execution grain** ([`ARCHITECTURE.md`](ARCHITECTURE.md) §6): PostgreSQL owns durable indexed state and set access; native C/C++ owns repeated algorithms; SPI is the prepared set-sized bridge; C#/SQL orchestrate. RBAR in a hot/repeated loop is an architecture defect even when the semantic output is correct.
- **Knowledge != authority != compute** (spec 37 authority contract; [`CAPABILITIES.md`](CAPABILITIES.md)): governance decides what a principal may discover/couple/realize/execute; standing is not permission; restricted knowledge remains known and attributable.
- **Resource envelopes and receipts** (spec 36 compute envelope; #1425, #1561): plan/EXPLAIN → reserve → execute under hard counters → actual receipt → reconcile. Hops, fanout and frontier/candidate work are first-class dimensions over the same knowledge world.

## 6. Proof and delivery boundaries

Different claims need different evidence ([`INVENTION.md`](INVENTION.md) §17):

| Claim | Evidence |
|---|---|
| mathematical closure/countability | proof in `INVENTION.md` §2 |
| executable representation law | native/managed property tests (carrier/trajectory round-trips) |
| finite implementation window | exhaustive checks (e.g. the selected Unicode generation) |
| live substrate behavior | live query/readback witnesses |
| performance | exact-revision, exact-artifact, host-bound benchmark receipts |
| machine cost | versioned derivation from artifact + ISA/microarchitecture model |

One deployed revision: the application, prefix native libraries, PostgreSQL execution module and T0 perfcache must identify one build (`scripts/check-deployed-revision.sh`). A green workflow is not delivery when install/readback was skipped. For current-vs-repaired defect state, the [deviation ledger](audits/INVENTION_DEVIATION_LEDGER_2026-09-19.md) is the entry point; dated documents in this tree are evidence, not status.