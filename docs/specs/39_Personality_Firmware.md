# 39 — Personality firmware and the Gödel/OODA loop

Binding contract for the program that operates the forward pass. It is read under
`docs/INVENTION.md`, `docs/INVENTIONS.md` and `docs/CAPABILITIES.md`, and beside
`36_Laplace_Forward_Pass.md` (the stages), `37_Substrate_Operation_ISA.md` (the
opcodes) and `34_Conversational_Provenance.md` (the turn). GitHub issue #1726;
workstream K of `docs/plan/ASSIMILATION_ROADMAP.md`.

Sources reconciled here, in authority order:

1. The inventor, 2026-09-25 session (quoted in §0).
2. `docs/INVENTION.md` §5, §11, §13, §16, §19; `docs/INVENTIONS.md` #29, #51, #77, #122.
3. `docs/CAPABILITIES.md` ("AI for every human": the personal effective mind includes governance/firmware).
4. Specs 34, 36, 37; the archived design record `docs/archive/specs-v1/15_Godel_Engine_OODA_Loop.txt`.
5. `SaltyPatron/Laplace-Refactor`, a separate repository written from the inventor's
   direct requirements (`AGENTS.md`: it does not own this repository). Its documents
   are references to reconcile, not imports: `docs/product/CONSTITUTION.md` §1, §7.2, §8;
   `docs/product/INVENTION_MODEL.md` §1.3, §10–§13;
   `docs/reconstruction/07_EXECUTION_CONTROL_GODEL_OODA.md`;
   `contracts/authority-stack.json`; `requirements/product.yaml` (LP-OODA-001,
   LP-GODEL-001, LP-GOVERNANCE-001, LP-FIRMWARE-001);
   `requirements/features/firmware_governance.feature` and `godel_ooda.feature`;
   `docs/architecture/PROMPT_OBSERVATION_TRAJECTORY_MATCHING_AND_REALIZATION.md` §2, §10;
   `docs/architecture/TASK_SCOPED_USER_HABITS_SAFETY_AND_AUDIENCE_ADMISSIBILITY.md` §7.

Where these sources disagree, §9 records both positions for the inventor. This spec
does not settle them.

---

## 0. The requirement

The inventor (spelling normalized):

> The forward pass for laplace is meant to use the godel engine, ooda loops, and my idea
> of personality firmware that detach operation and generation and such from the data
> itself... Humans know guns exist and they use them to kill each other but that doesn't
> mean every human will use a gun to kill someone... AI/Laplace/etc is a tool and the
> firmware and personality use that tool... the godel engine, forward pass, etc is that
> personality... "Personality Firmware" since I basically am mapping out the individual
> steps of the conventional GPT to semantic instruction sets.

Three consequences are binding:

- **The world is the tool; firmware is the hand that uses it.** Knowledge records what
  exists and what was observed. Firmware decides what to do with it. Knowing does not
  decide doing, and doing never edits knowing.
- **Firmware is a program over the semantic instruction set.** The GPT's steps are
  mapped to the opcodes of spec 37 and the stages of spec 36. Personality firmware is
  the program over those instructions that decides observation, orientation, decision,
  action and generation.
- **The same world under different firmware behaves differently.** Two firmware
  programs over the same pinned substrate may choose different trajectories, spend
  different work, demand different evidence and realize differently. Both read the same
  truth.

Laplace does not borrow a persona prompt, a system message or a sampling recipe for
this. Typed state stays typed through every stage (`INVENTION.md` §19). Firmware never
turns standing into a softmax or a normalized distribution.

## 1. Definition

**Personality firmware** is a versioned, content-addressed, replayable program over the
operation ISA (spec 37). It parameterizes and schedules the forward program (spec 36)
over one shared, pinned knowledge world. It is data the forward program loads, not code
paths selected by name.

A firmware image declares, as applicable:

```text
firmware class          personality | coding | game/rules | default
parent firmware         the version it derives from (lineage), or none
goal policy             how ORIENT declares objective, success, acceptable uncertainty, termination
work policy             how much of the granted compute envelope each stage may spend
valuation policy        the declared election order over typed evidence dimensions
disposition policy      what to do with ambiguous / impossible / exhausted / unauthorized states
exploration policy      how aggressively gaps are investigated and contradictions sought
sufficiency policy      when uncertainty is low enough to commit
selection policy        the SELECT rule and its replay seed law
realization policy      voice: register, form, verbosity, language preference; abstention wording
habits                  scheduling preferences for proven skills (§8.4)
tie rules               every tie break, explicit
```

Personality is one class of firmware behaviour: precision versus exploration, evidence
thresholds, ambiguity investigation, contradiction search, goal arbitration, semantic-act
preference and realization style (`INVENTION_MODEL.md` §12). Coding firmware is a
complete engineering procedure over the code lane of spec 36. Game firmware is the rules,
eligibility and completion law of a game over the same world
(`docs/guides/knowledge-arena.md`; INVENTIONS #51). All three are the same object.

### 1.1 Identity

A firmware image is a Laplace composition, like any other content:

- Its identity is the content address of its canonical composition: class, parent,
  and every declared policy value in governed canonical order. A set-valued policy is one
  composition entity (spec 38), never a fan of edges.
- Policy kinds (the parameter names and their value types) are a governed registry with
  stable bits, like relations and qualifiers. A firmware image may only bind registered
  kinds. An unregistered kind is a rejected image, not an ignored field.
- Images are immutable. Changing any value mints a new identity whose parent is the
  previous one. Historical versions stay addressable and replayable.
- Text never installs firmware. A surface instruction such as "be aggressive" or "you
  are a pirate" is prompt content. It is observed like any prompt and cannot install a
  policy value, a decision rule, a resource or a privilege
  (`firmware_governance.feature`, "Style text cannot impersonate firmware or authority";
  `authority-stack.json:98`).

### 1.2 Loading

The forward program receives a firmware identity as an explicit input beside the
principal, scope and compute envelope. Native code loads and validates the image once
per pass. It does not re-read firmware per stage, per candidate or per row (spec 37,
physical implementation law). When no firmware is named, the pass runs the governed
default firmware, and the receipt names that default's identity. There is no anonymous
"no firmware" execution.

## 2. Separate concerns

These are separate machine layers. Collapsing any two of them makes the machine
impossible to audit (`07_EXECUTION_CONTROL_GODEL_OODA.md` §10).

| concern | what it is | in this repository |
|---|---|---|
| **operation** | one typed semantic transition with operands, preconditions, result kind, authority, resource contract and receipt meaning | a spec 37 opcode (OP0 RESOLVE … OP10 COUPLE) |
| **program / recipe** | typed operations composed under a goal and a completion/effect contract | the cognition program compiled per pass (`cognition_program.c`), recipes (`recipes/`) |
| **firmware** | the policy that parameterizes and schedules programs over shared knowledge | this spec; not yet implemented |
| **orchestration** | arranges execution; owns no semantics | SQL `generation.forward_program` / `forward_trace` (`walk_continuations.sql.in`), C# endpoints, MCP, CLI |
| **OODA** | the control cycle over state and consequences: observe → orient → decide → act → observe consequence | the forward pass plus WITNESS plus the later observed outcome (§8.1) |
| **evidence learning** | new observations publish a new standing epoch | ingestion and the Glicko-2 fold (`INVENTION.md` §11) |
| **Gödel extension** | typed incompleteness proposes a candidate fact, relation family, law, operator, program or firmware operation, activated only on disjoint evidence | the fray lane (#1048); not yet implemented (§8.3) |

Orchestration that chooses truth or ranking has become a second engine. OODA does not
create a new calculus. Gödel is not "another pass of the loop". Firmware is not
knowledge, not authority and not orchestration.

## 3. What firmware parameterizes, stage by stage

Each row names the decisions firmware owns, what it can never own at that stage, and the
concrete native parameter that carries the decision today. Today these parameters are
loose SQL arguments with hard-coded defaults. Under this spec each one becomes a policy
value inside a firmware image, and the SQL argument becomes an override the caller may
pass only within its authority.

Work dimensions obey one rule: **the compute envelope is the ceiling, and firmware
chooses how much of it to spend.** Firmware may spend less than the granted hops, fanout
or steps. It can never spend more (spec 36, compute envelope; `INVENTION.md` §16).

| stage | firmware owns | firmware never owns | native carrier today |
|---|---|---|---|
| **RESOLVE** | nothing semantic | the exact observation, occurrences, principal, scope, authority, output contract | `forward_prompt` (`trajectory_generate.c:1988`) |
| **COUPLE** | how many coupling rounds to spend, up to the envelope | eligibility of any plane; a relation/provider mask before ORIENT | `semantic_hop_limit` bounds coupling rounds (`trajectory_generate.c:1349`); `fanout` bounds each plane (`prompt_intent.h:776`, `854-869`) |
| **ORIENT** | the active goal (objective, success conditions, acceptable uncertainty, termination); the *response* to an ambiguous, impossible or exhausted orientation: abstain, ask, retain alternatives, or spend more coupling | the *detection* of ambiguity or budget exhaustion; which interpretation is true | `LaplacePromptIntent.ambiguous` / `budget_exhausted` (`prompt_intent.h:89-91`, set at `prompt_intent.h:1192`, `task_shape.c:645,665,675`); today ambiguity finalizes the program and stops emission (`trajectory_generate.c:1478-1481`, `1517`) |
| **ROUTE** | hop and fanout spend; how aggressively gaps are explored (extra routing rounds toward unresolved obligations); how aggressively contradictions are sought (routing refutation readers, not only support) | the caller's hard relation scope and output contract; authority | `p_hops` → `semantic_hop_limit` (default 2, `trajectory_generate.c:2011`; checks at `1592`, `1823`); `p_fanout` (default 8, `trajectory_generate.c:2012`); task-shape instantiation bound (`task_shape.c:475-486`) |
| **SCAN** | operator choice inside the routed program; beam and depth spend; whether optional geometry or ordinal-continuity operators run | a default intent mask standing in for ORIENT (`INVENTION.md:911`, spec 36 ORIENT) | `walk_branches` `max_depth` / `beam` (`generate_walk.c:573-574`), `p_ordinal_continuity` (`generate_walk.c:604`), `p_use_geometry` (`generate_walk.c:612`); `p_intent_mask` (`generate_walk.c:577-583`) is a caller hard constraint only |
| **COMPOSE** | nothing | the fold, the dependence law, standing itself | query state fold (`query_evidence.h`) |
| **PROPOSE** | semantic-act preference: answer, ask, explain, abstain, act | grammar/type legality; which result relations an operation establishes | `candidate_can_output` (`trajectory_generate.c:759`) |
| **STEER** | the declared election order over typed dimensions; the sufficiency bound (how much deviation is tolerated before a claim counts as supported); the ordinal-continuity window; the emission budget | a cross-family scalar product; a global popularity score; the claims' standing | `candidate_compare` election order (`trajectory_generate.c:914-944`); conservative bound `rating − 2·rd` / `rating + 2·rd` (`trajectory_generate.c:412-413`, `449-450`); `p_max_stride` (default 5); `p_steps` (default 24) (`trajectory_generate.c:1155-1156`) |
| **SELECT** | the selection rule and its replay seed law | selecting an item the admitted evidence does not support; a softmax or distribution over standing | `p_spread` (default 0.7) and `p_top_k` (default 10) (`trajectory_generate.c:1157-1158`, used at `1856-1872`); `p_seed`, defaulted from BLAKE3 of the prompt (`trajectory_generate.c:2063-2085`). See conflict C2. |
| **REALIZE** | voice: register, form, verbosity, language preference; how an abstention or partial result is worded | what is disclosed beyond realization authority; reclassifying the selected act; turning an unresolved search into answer content | disposition names `open` / `complete` / `unresolved` / `budget_exhausted` / `ambiguous` (`cognition_program.c:1025-1033`); realization today is the `chat_scaffold` templates (roadmap F) |
| **WITNESS** | nothing about whether the governed lane runs | skipping a write the operation contract requires; attesting its own output (§9, C1) | turn witnessing in `ConversationContent.TryBuildTurnChange` |

SQL defaults for the same parameters are in
`extension/laplace_substrate/sql/functions/generation/walk_continuations.sql.in:25-30`.

### 3.1 Parameters that are not firmware

- **`p_output_relation_types`, `p_invocation_context`, the caller's hard relation scope.**
  These are caller contracts (spec 37: an operation may arrive already oriented). They
  are inputs beside firmware, not firmware.
- **The principal, tenant, knowledge-package grants and capability mask.** These are
  authority (spec 37, authority contract). Firmware reads them and cannot widen them.
- **Relation rank.** The roadmap decided that relation rank is salience applied at
  reading, in coupling (`ASSIMILATION_ROADMAP.md:90`). The election uses it as its first
  key (`walk_relation_rank` in `positive_channel_compare`, `trajectory_generate.c:410-418`).
  Whether firmware may re-weight salience or only read the governed registry value is
  open (§9, Q3).

## 4. What firmware can never do

1. **Acquire authority.** Firmware cannot grant itself an instruction, a resource, data
   scope or an effect permission. Read-only authority stays read-only when firmware
   investigates aggressively or recommends an effect (`INVENTION_MODEL.md` §12;
   `CONSTITUTION.md` §1). Authority is checked again at the act (spec 36 SELECT).
2. **Change truth.** Firmware cannot write, fold, re-rate or hide a claim's standing. Two
   firmware images over the same pinned substrate read identical standing. Firmware
   standing (§6) never enters the standing of the claims its passes selected.
3. **Bypass an effect envelope.** Every path that can produce an external effect or a
   governed realization reaches the same effect and safety admissibility contract,
   whichever firmware selected the act. Safety is not a firmware and not a habit, and a
   learned habit or accelerated path cannot skip it
   (`TASK_SCOPED_USER_HABITS_SAFETY_AND_AUDIENCE_ADMISSIBILITY.md` §7).
4. **Alter knowledge.** Firmware cannot add, delete or edit content, physicality,
   occurrence, testimony, provenance or history. Governance of knowledge is not deletion
   of knowledge (`INVENTION.md` §16, "Governance and honest abstention").
5. **Substitute for ORIENT.** Firmware cannot supply a default relation or provider mask
   before the query-relative field is computed (`INVENTION.md:911`; spec 36 ORIENT and
   acceptance; spec 37 canonical ordering).
6. **Flatten typed state.** Firmware declares an election *order* over typed dimensions.
   It cannot declare a cross-family product, a universal relevance number or a
   normalized share (`INVENTION.md:913`).
7. **Exceed the compute envelope.** Firmware spends within the granted hops, fanout,
   steps and work. The envelope belongs to the principal and the tier.
8. **Certify itself.** A firmware's own passes, predictions and descendants are not
   independent evidence for that firmware or for any claim (§6, §8.3).
9. **Activate itself.** A firmware version becomes active only by explicit activation
   authority (§8.3).

## 5. Identity in every trace and receipt

Every pass names the firmware that ran it. This is the "policy as a content-addressed
entity" slot the archived design record left open
(`docs/archive/specs-v1/15_Godel_Engine_OODA_Loop.txt:157-159`, B4 at `:180-183`).

- **Receipt field.** `LaplaceCognitionProgramReceipt` (`cognition_program.h:22-37`)
  gains `firmware_id`. Every row of `generation.forward_trace` and
  `generation.forward_program` carries it beside `program_id`
  (`walk_continuations.sql.in:31-42`; receipt columns written by `put_cognition_receipt`,
  `trajectory_generate.c:945-966`).
- **Program identity stays interpretation identity.** `program_id` today binds the
  admitted root, obligations and the whole coupling field, and no policy value
  (`cognition_program.c:370-444`, domain `laplace:cognition-program:v9`). It stays that
  way: the same observation under two firmware images has the same `program_id`, which
  is what makes their divergence comparable. The firmware is a separate identity.
- **Decision identity.** The receipt carries a decision identity: the content address of
  `program_id`, `firmware_id` and `output_fingerprint`. The semantic act id
  (`cognition_program.c:488-492`) stays firmware-independent, so the same act reached
  under two firmware images is recognizably the same act.
- **Every stage row.** Each `emit_stage` event (`resolve`, `couple`, `orient`,
  `propose`, `scan`, `compose`, `steer`, `select`: `trajectory_generate.c:1269-1881`)
  names the firmware policy values it consumed, so each divergence is attributable to an
  executed instruction or value (`firmware_governance.feature`, "Different personalities
  make different valid computational choices"). ROUTE, REALIZE and WITNESS have no trace
  events today; they gain them.
- **Turn contract.** Spec 34's "selected operation program and semantic trace" includes
  `firmware_id`. MCP, HTTP, CLI and SQL agree on it (spec 36 trace contract).
- **Effect envelope.** An effect proposal is canonicalized with its firmware identity
  (`INVENTION_MODEL.md` §13). The executor verifies the approved envelope, firmware
  included, before execution.
- **Replay.** A pass replays from the pinned substrate epoch, calculus, firmware,
  program, effect and observation boundaries (`CONSTITUTION.md` §7.2, last paragraph).
  Replay under the same firmware reproduces the same selections and trace.

## 6. Witnessing and rating

Firmware is rated by the consequences of what it chose, the way a chess player is rated
through games (spec 11). Its outputs are not testimony about the world.

- **Firmware is an attributable participant.** A firmware identity is an entity. A pass
  under it is an occurrence attributed to it, as a game is attributed to its players.
- **Outcomes are matchups for the firmware.** An observed consequence that bears on a
  pass is a matchup for the firmware that ran it. Examples: a user's correction, a
  confirmed answer, a tool or test result, an effect outcome, a later independently
  adjudicated contradiction of the answer. It folds into the firmware's Glicko-2
  standing: rating, deviation, volatility, witnesses. The outcome's witness is whoever
  observed the consequence: the user, the test runner, the tool. It is never the firmware
  and never Laplace grading itself.
- **Consequences stay typed.** One pass may contain a good act, a bad explanation, an
  overspent budget and a later consequence. Those are separate observations. They update
  only the claims they bear on, never one success bit (`INVENTION_MODEL.md` §11,
  "autobiographical boundary"; `TASK_SCOPED_…` "do not flatten all of those into one
  win/loss").
- **Firmware standing does not leak into truth.** Firmware standing is about the
  firmware. It never multiplies, gates or re-rates the standing of claims that firmware
  read or selected.
- **Rating informs choice; it does not make it.** Which firmware runs is chosen by the
  principal or governance, within authority. Firmware standing is visible evidence for
  that choice. Whether Laplace may pick a firmware by its standing on its own is open
  (§9, Q4).
- **Idempotence.** Retrying a transport or replaying a read does not create another
  matchup (spec 34 turn contract).

This is the reinforcement the inventor described: an action's observed consequence is
witnessed and folds into standing, so what is reinforced is attributable standing, not an
opaque weight change (`ASSIMILATION_ROADMAP.md:56`, law 18). There is no gradient.

## 7. Feedback lanes

"Feedback" is not one mechanism. The lane decides what may change
(`07_EXECUTION_CONTROL_GODEL_OODA.md` §5).

| lane | input | may change | may never change |
|---|---|---|---|
| fast cognition | a partial result, tool result or open obligation inside the pass | the pass's working frontier | testimony, calculus, firmware |
| effect / outcome | an action's observed consequence | occurrence and outcome state; firmware standing (§6) | the claims' standing, unless independent testimony bears on them |
| evidence | independent later observations and testimony | claim standing, as a new epoch | calculus, firmware |
| procedural | successful and failed pass trajectories | evidence for or against a program or firmware operation as reusable | truth |
| physical execution | latency, CPU, I/O, crossings | eligibility for an accelerated path | semantics |
| Gödel | persistent typed frays, failed predictions, failed outcomes | *candidate* extensions (§8.3) | anything active, until activation |

## 8. The loop

### 8.1 OODA over the forward program

```text
OBSERVE            RESOLVE: the exact observation, discourse, open obligations, prior consequences
ORIENT             COUPLE → ORIENT: the typed response field and the joint interpretation, under firmware goals
DECIDE             ROUTE → SCAN → COMPOSE → PROPOSE → STEER → SELECT, under firmware policy
ACT                REALIZE, and any effect under the authority/effect envelope
OBSERVE CONSEQUENCE WITNESS of what happened, and later outcomes that bear on it (§6)
```

This is `INVENTION.md` §13's loop (`observe -> couple/orient -> decide/execute -> realize
-> witness -> update`, `INVENTION.md:704`) with the firmware named. OODA controls action
selection and consequence closure. It does not create a new calculus, source truth or a
reusable instruction by itself (`07_…` §4).

### 8.2 Three rates

```text
fast cognition   query → calculate/search → semantic act           (one pass; no epoch or calculus change)
learning         observation → testimony → adjudicated epoch       (standing changes; calculus and firmware do not)
Gödel discovery  persistent typed fray → candidate extension → disjoint evaluation → activated version
```

None of the three can impersonate another in state or receipts (`CONSTITUTION.md`
§7.2; `godel_ooda.feature`, "Fast cognition learning and Godel discovery cannot
impersonate one another").

### 8.3 Gödel extension

Typed incompleteness is a discovery signal, not a defect to hide
(`INVENTION_MODEL.md` §11; `ASSIMILATION_ROADMAP.md:57`, law 19, "a periodic table of
knowledge").

1. **Signal.** A constrained vacancy: repeated typed structure predicts that something
   with a particular signature belongs somewhere, and no supported occupant is present.
   Also persistent contradictions, failed predictions and failed outcomes. An empty query
   result is not a vacancy.
2. **Candidate.** The engine proposes a candidate: a fact, relation family, motif, law,
   operator, cognition program, **firmware operation** or calculus extension. It carries
   its predicted signature and complete ancestry. It is a derived hypothesis, not an
   observation.
3. **Disjoint evaluation.** It is fitted only on permitted evidence and evaluated on
   pinned held-out evidence it did not manufacture. Evaluation leakage fails the
   experiment. It is searched for counterexamples and charged its complexity.
4. **Activation.** It becomes active only by explicit activation authority, as a new
   version whose predecessor stays replayable. Self-generated descendants never
   corroborate the candidate that produced them.
5. **Fill.** A predicted occupant does not fill its own vacancy. Reality supplies an
   occurrence, testimony or experiment outcome.

A firmware version proposed this way is a Gödel candidate like any other. It is
activated only on disjoint evidence, and it is rated afterward by §6.

### 8.4 Memory, skill, habit, muscle memory

These are separate promotion levels (`07_…` §9):

- **memory**: retained observations, passes, trajectories, outcomes and receipts;
- **skill**: a reusable, versioned cognition program proven under its activation contract;
- **habit**: a firmware scheduling preference to propose a proven skill earlier under
  matching state. Habit changes proposal order and expected work, never truth. Contrary
  current state or preconditions defeat it;
- **muscle memory**: a semantically equivalent accelerated path (fused native operator,
  perfcache plane, prepared provider) for a repeatedly proven procedure. It changes
  physical execution, never meaning, and must keep every authority and admissibility
  check.

## 9. Conflicts and decisions for the inventor

Each item states both positions with citations. None is resolved here.

### C1. Do Laplace's own outputs, and user prompts, become attestations?

**This repository: responses self-witness.**

- Archived spec 15 §0: "A response is content-addressed and deposited as a witness
  (self-reference); feedback on a response is an attestation folding into the SAME
  consensus the next walk reads... Evaluation IS ingestion"
  (`docs/archive/specs-v1/15_Godel_Engine_OODA_Loop.txt:24-27`). Invariant I1: every
  feedback signal, behavioural verdict and self-adjudication result is an attestation
  (`:123-126`). I6: the engine's self-signals are outranked by design, Response trust .20
  (`:138-140`). §1.2: prompt and response are deposited as witnesses (`:46-61`).
- `INVENTION.md:383`: "A witness may be ... a conventional model or Laplace itself."
  `INVENTION.md:699`: "A generated response is itself content and may be witnessed with
  its derivation/receipt. It does not become authoritative truth merely because the system
  produced it."
- INVENTIONS #29 (`docs/INVENTIONS.md:40`) lists responses and feedback as trust classes.
  #77 (`:106`): "Prompt, response, tool, evaluation and feedback outcomes can deposit
  through governed lanes and affect later standing/cognition."
- Current code: a turn is written with the prompt under `UserPrompt@{tenant}` and the
  reply under `Response@{tenant}`, each with a declared source prior
  (`app/Laplace.Substrate/Abstractions/ConversationContent.cs:196-197`), and the reply
  attests `DEPENDS_ON` the prompt under the response source
  (`ConversationContent.cs:221-223`). `ResponseContent.TryBuildWitnessChange` deposits
  content-witness attestations under the Response source
  (`app/Laplace.Substrate/Abstractions/ResponseContent.cs:38-62`). User feedback
  deposits confirm/refute attestations through `FeedbackContent`
  (`app/Laplace.Cli/QueryCommands.cs:316-376`).

**Laplace-Refactor: observation state, zero semantic attestations.**

- `contracts/authority-stack.json:8`: user prompts and live activity "create exact
  canonical observation occurrence and trajectory state but zero semantic attestations
  merely by being observed; internal cognition and generated output likewise do not
  self-attest; ... later independently observed or adjudicated outcomes may update typed
  standing." Forbidden substitution (`:105`): "user-prompt observation or internal
  cognition for semantic attestation."
- `docs/architecture/PROMPT_OBSERVATION_TRAJECTORY_MATCHING_AND_REALIZATION.md:39`: a
  prompt "creates zero semantic attestations merely because the user said it." `:65`:
  cognition and generated responses "create zero independent semantic attestations merely
  by being produced." `:277`: executions "do not attest to their own semantic
  correctness."
- `docs/product/INVENTION_MODEL.md` §11 (`:849-853`): a self-generated diagnosis or
  extension "cannot serve as independent corroboration of itself."

**Where they already agree.** Both say a response is content with an occurrence, and
neither lets it become truth because Laplace produced it (`INVENTION.md:699`). Both let
an independently observed consequence change standing.

**What is undecided.**

1. Whether a response or prompt may mint attestations at all, or only observation,
   occurrence and receipt state.
2. Whether turn bookkeeping claims (`APPEARS_IN` membership, `HAS_ATTRIBUTION`, reply
   `DEPENDS_ON` prompt) count as semantic attestations under the Refactor boundary or as
   record-lane structure.
3. Whether explicit user feedback (`laplace attest confirm|refute`) is testimony by the
   user as a witness (this repository), or an assertion that becomes a matchup only
   after independent adjudication (Refactor, `PROMPT_OBSERVATION…:51-61`).
4. Whether the response source should carry the firmware identity (a response witnessed
   as `[Response, firmware]`), which matters only if responses witness at all.

§6 (firmware rating) is written to hold under either answer: firmware standing comes
from observed consequences witnessed by someone other than the firmware.

### C2. Is SELECT stochastic?

- **This repository.** Spec 36 SELECT: "Select under the declared deterministic or
  stochastic policy" (`docs/specs/36_Laplace_Forward_Pass.md:125`). The native pass
  elects by ordinal rank and then, unless `spread = 0`, draws with a Gumbel-max key
  `−i/spread − log(−log u)` over the first `top_k` ranks
  (`trajectory_generate.c:1856-1872`). That draw is mathematically a sample from a
  softmax over rank positions with scale `spread`. The default is stochastic
  (`spread 0.7`, `walk_continuations.sql.in:27`), with a replayable seed derived from the
  prompt (`trajectory_generate.c:2063-2085`). The archived record calls it "Gumbel top-k
  sampling" (`15_Godel_Engine_OODA_Loop.txt:43`). The code comment says spread applies to
  the ordinal election, not to an evidence scalar (`trajectory_generate.c:1865-1866`).
- **Laplace-Refactor.** `CONSTITUTION.md` §8 (`:443-445`): "Laplace does not use softmax
  continuation to create an answer. Selection is a deterministic, inspectable execution."
  `INVENTION_MODEL.md` §12 (`:865`): "Firmware is a deterministic cognitive policy."
- **The inventor, this session:** typed state is never a softmax or a distribution.

**Undecided.** Is a seeded, replayable draw over the rank order a lawful firmware
selection policy (an "exploration" personality), or must every SELECT policy be
deterministic, which would make `spread` and `top_k` retire or become deterministic
window rules? Until this is decided, the default firmware (§10) keeps today's behaviour
and names it in the receipt.

### C3. Is the Gödel engine the OODA loop?

- **This repository.** Archived spec 15 is titled "GÖDEL ENGINE: closing the OODA loop"
  and defines the Gödel property as self-reference: outputs become inputs
  (`15_Godel_Engine_OODA_Loop.txt:3`, `:24-33`). Roadmap law 17: "The Gödel engine is its
  OODA loop" (`docs/plan/ASSIMILATION_ROADMAP.md:55`). The inventor, this session: "the
  godel engine, forward pass, etc is that personality."
- **Laplace-Refactor.** `07_EXECUTION_CONTROL_GODEL_OODA.md` §8 (`:197-217`) marks
  "Gödel = multi-scale OODA" as historical Hartonomous terminology, superseded where it
  conflicts. The current model is: OODA is the control/effect cycle, evidence learning is
  an epoch change, and Gödel is candidate calculus and procedure discovery with
  falsification (`:156-195`).

This spec uses the Refactor factoring (§2, §8), as workstream K asked, because it keeps
each mutation answerable. **Undecided:** whether "Gödel engine" names the whole
self-referential loop (the inventor's and archived usage) or only the discovery lane (the
Refactor usage). If the former, §8.3 should be renamed "Gödel discovery" and "Gödel
engine" used for §8 as a whole.

### Open questions

- **Q1. Is firmware a source?** Workstream K asks for firmware to be "witnessed and rated
  like any other source". Under roadmap law 1 a source is the witness of its
  observations, and firmware observes nothing. §6 treats firmware as a rated participant
  (the chess-player model), not a witness with a trust class. Confirm or correct.
- **Q2. The sufficiency bound.** The election's conservative bound uses a fixed
  multiplier of 2 deviations (`trajectory_generate.c:412-413`, `449-450`). Is that
  multiplier firmware ("when uncertainty is sufficient", `INVENTION_MODEL.md:867`) or
  standing law shared by every reader?
- **Q3. Relation salience.** Is relation rank a governed registry value every firmware
  reads unchanged, or a valuation firmware may re-weight ("how evidence is valued",
  `INVENTION_MODEL.md:866`)?
- **Q4. Who picks the firmware?** The archived B4 plan selects decode policies "by eff_mu
  with rd as the exploration bonus" (`15_Godel_Engine_OODA_Loop.txt:180-183`), that is,
  automatically. Refactor requires explicit activation authority
  (`07_…:190`). Is automatic selection among already activated firmware by standing
  lawful, or must the principal or governance always name it?
- **Q5. Ambiguity default.** Today an ambiguous orientation ends emission
  (`trajectory_generate.c:1478-1481`, `1517`). Is "abstain and report the alternatives"
  the default firmware's disposition, or should the default ask a clarifying question?

## 10. The default firmware

Until firmware images exist, the default firmware is today's behaviour, named:

```text
class        default
COUPLE/ROUTE semantic_hop_limit 2, fanout 8
STEER        election order of candidate_compare; bound rating ∓ 2·rd; max_stride 5; steps 24
SELECT       spread 0.7, top_k 10, seed = BLAKE3(prompt UTF-8) first 8 bytes
ORIENT       ambiguous or budget-exhausted → finalize with that disposition; no emission
REALIZE      chat_scaffold templates
```

Registering this as the first firmware image changes no output. It makes every current
pass attributable, and it is the parent of every later image.

## 11. Acceptance

- Two firmware images over one pinned substrate, goal and ISA produce different
  scheduled trajectories, evidence thresholds, semantic-act preferences or decision
  traces. Every difference is attributable to a named policy value in the trace. Both
  read identical claim standing.
- A prompt asking for a personality or an unauthorized effect installs no policy value
  and grants no capability. The effect stays denied when firmware recommends it.
- A firmware image that names an unregistered policy kind, exceeds the compute
  envelope, or supplies a relation mask before ORIENT is rejected before any provider
  runs.
- Every `forward_trace` row, every cognition receipt, every spec 34 turn and every effect
  envelope names `firmware_id`. MCP, HTTP, CLI and SQL agree.
- Replay under the same substrate epoch, firmware and seed reproduces the same
  selections and trace.
- An observed consequence moves the firmware's standing and leaves the standing of the
  claims it selected unchanged unless independent testimony bears on them.
- A firmware candidate from the Gödel lane is rejected by held-out or counterexample
  evidence it did not fit, and activates only by explicit authority.
- A habit loses to contrary current state or preconditions. An accelerated path keeps
  semantic, admissibility and receipt parity.
- Every path that can produce an effect reaches the same admissibility contract under
  every firmware, habit and accelerated path.

## 12. Implementation map

| work | owner |
|---|---|
| governed registry of firmware policy kinds; firmware image as a composition (spec 38) | #1726 |
| load one firmware image per pass in native code; move the §3 parameters into it; SQL arguments become bounded overrides | #1726, over the consolidated ISA #951 |
| `firmware_id` and decision identity in `LaplaceCognitionProgramReceipt`, `forward_trace` columns, spec 34 turns; ROUTE/REALIZE/WITNESS trace events | #1726, #1720 |
| firmware rating by observed consequences | #1726; absorbs #356 (archived B4 walk-policy rating) |
| behavioural harness and loop-closure metrics, rebuilt on this spec after C1 is decided | #355, #357 |
| Gödel candidate lane over frays, including firmware operations | #1048, #1726 |
| game firmware and COMBINE as firmware over the forward program | #1421, #1420 |
