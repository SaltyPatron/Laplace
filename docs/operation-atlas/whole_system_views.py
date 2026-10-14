"""Connected specification flows; these do not assert implementation acceptance."""

def add_whole_system_views(add):
    wiki = 'Laplace-Wiki/Sequence/'
    add('whole-lifecycle', 'Laplace — construction prerequisites and recurring operation lanes', r'''
top to bottom direction
package "Construction / admission prerequisites (Wiki numbered order)" {
 component "1 Unicode → 2 Toolchain → 3 Builds\n4 Projection → 5 Tier 0" as foundation
 component "6 Registries → 7 Perfcaches → 8 Database" as storage
 component "9 Sources → 10 Recipes → 11 Content\n12 Attestations → 13 Consensus → 14 Indexes" as admission
 component "15 Web → 16 Authority → 17 Envelope\n18 Firmware → 19 Pull" as execution
 foundation --> storage : compatible artifacts / fingerprint
 storage --> admission : canonical store + derived read maps
 admission --> execution : indexed content + attributable standing
}
package "Recurring operation" {
 component "20 Forward\nRESOLVE → COUPLE → ORIENT → ROUTE\nSCAN → COMPOSE → PROPOSE → STEER\nSELECT → REALIZE → WITNESS" as forward
 component "21 Sessions / 22 Users\nprincipal · exact observation · discourse\nturns · effects · receipts" as session
 component "23 Learning\nconsequence / evidence / procedure\nphysical acceleration / candidate discovery" as learning
execution --> forward : operators / firmware\nscope / envelope
 session --> forward : current observation + retained state
 forward --> session : selected act / realization / governed witness
 session --> learning : attributable consequence
 learning --> admission : new observations / independent testimony
 learning --> forward : changed state / explicitly activated version
}
package "Lanes using the same machine — not successive stages" {
 component "24 Models\n25 Export\n26 Code\n27 Chess\n28 Games\n29 Maintenance" as lanes
}
lanes --> forward : lane-specific contract / grammar / act
lanes --> learning : consequences under\nthe same evidence law
lanes ..> foundation : Unicode upgrade repeats 1–5\nnew fingerprint (29)
note bottom of learning
Fast frontier changes, claim standing, firmware standing,
physical acceleration and activated extensions are different state.
Read replay and transport retry do not manufacture testimony.
end note
legend
Specification synthesis, not a deployment or call graph.
Wiki's prerequisite order is distinct from runtime feedback arrows.
Monorepo, Refactor and modular implementations require separate evidence.
Conflicts remain in Sequence/Conflicts.md; arrows do not resolve them.
endlegend
''', [wiki+'README.md', wiki+'Content.md', wiki+'Forward.md', wiki+'Learning.md',
      'Laplace/AGENTS.md'])

    add('shared-admission', 'Laplace — shared source admission, reuse and completion', r'''
start
:9–10 Active source generation + artifact graph\nprovider + declared recipe + admitted resource plan;
:11.1 Unpack into records, spans and packaging facts\nWhole working set or physically partitioned stream;
note right
One semantic object survives physical partitioning.
Shared scheduling preserves service headroom.
No adapter-private identity or persistence loop.
end note
:11.2–11.5 Decompose exact content on client\nfile = [metadata, content]\nsource = [source record, file trunks in path order]\nassign altitude and hash children bottom-up;
note right
Single child reuses its identity.
Identity excludes tier, source, role and coordinate.
No skipping an artifact before its trunk ID exists.
end note
:11.6–11.7 Compute physicalities\ncoordinate / Hilbert locality / typed packed path;
note right
Packing stores child identity and structure.
A realized curve resolves child coordinates.
Locality and coordinate equality are not identity.
Numerics / carrier differences require conflict review.
end note
:11.8 Coalesce identical canonical rows in working set;
while (Unresolved trunks / lower tiers?) is (yes)
 :Probe a set of IDs against canonical store / valid cache;
 if (Trunk already complete?) then (yes)
  :Reuse its canonical subtree\nretain new occurrences and distinct testimony;
 else (no)
  :Retain novel rows and descend to children;
 endif
endwhile (no)
:11.9–11.10 Count occurrences / containers\naccumulate partition-routed streams;
if (Recipe declares attributable claims?) then (yes)
 :12 Reduce by claim + witness identity\nretain outcomes, games, scores, qualifiers, context;
 note right
Ordinary content is observation, not testimony.
Structural containment / order are read from paths.
Repeated packaging is not independent witnessing.
 end note
endif
:11.11 Shared writer applies new rows in bulk\nleaves before parents + set merges;
note right
Trunk pruning depends on complete descendants.
This is the specified atomicity requirement;
reviewed implementation commit boundaries differ.
end note
:11.12 / 13 Fold admitted matchups in sets\nexclude already-folded testimony on retry\nretain FIFO where the fold is noncommutative;
:Record operational completion per unit / layer;
if (Recipe declares reconstruction?) then (yes)
 :11.13 Recompose from canonical paths\ncompare with artifact / declared loss;
endif
:11.14 Independent acceptance read\nentity / physicality census + fingerprints\nwork receipt + resumable journal;
stop
legend
Specified source lifecycle, not proof that a command implements it.
Completion state and a receipt are not independent acceptance evidence.
Source: Sequence/Content.md and Laplace/AGENTS.md.
endlegend
''', [wiki+'Content.md', 'Laplace/AGENTS.md'])

    add('stateful-forward', 'Laplace — request admission, stateful cognition and consequence', r'''
start
:21–22 Bind surface request / principal / tenant / session;
:20.1 RESOLVE\nexact observation + occurrence IDs + discourse\nworld / source / time + obligations + output contract;
:16.4–16.5 Resolve effective scope and capability grants\nload firmware version + hard resource boundary;
note right
Authority excludes inputs before COUPLE,
including otherwise mapped cache records.
Standing, permission and compute stay distinct.
end note
repeat
 :20.2 COUPLE\nwhole observation perturbs every eligible typed plane;
 :20.3 ORIENT\njoint interpretation / surviving ambiguity;
 if (Executable under evidence and remaining resources?) then (yes)
  :20.4 ROUTE\ncompile eligible operators, scope, obligations\nhop / fanout / frontier limits;
  :17.1–17.4 Estimate work and machine cost\nreserve ceiling where capacity-controlled or billable;
  :20.5 SCAN → 20.6 COMPOSE\nindexed frontier / typed evidence / dependence;
  :20.7 PROPOSE → 20.8 STEER\nlegal acts / coverage / uncertainty / constraints;
  :20.9 SELECT\nsupported decision + capability check for this act;
  if (Completed semantic act authorized for realization?) then (yes)
   :20.10 REALIZE\nrender selected identity or perform authorized act\nretain realization fingerprint;
   if (Operation contract requires witnessing?) then (yes)
    :20.11 WITNESS via governed write lane\nobservation / effect / receipt + idempotency key;
   endif
   :20.12 Update active trajectory, bindings, obligations\nresidual and frontier before the next selection;
  else (no)
   :Retain explicit unresolved / authority disposition;
  endif
 else (no)
  :Retain ambiguous / inconsistent / resource-bounded disposition;
 endif
repeat while (Further work allowed by termination policy and envelope?) is (yes) not (no)
:17.6–17.7 Actual work receipt and reserve reconciliation\nWHY / WHY_NOT within disclosure authority;
:21 Retain contract-required turn and effects\n23 Route subsequent consequences by feedback lane;
stop
legend
Semantic contracts may be fused into a coarse native operator.
The diagram does not prescribe eleven transport round trips.
Checks and counters apply throughout, including preflight work.
Reads alone create no testimony; response attestation remains conflict C1.
Source: Sequence/Authority, Envelope, Forward, Learning.
endlegend
''', [wiki+'Authority.md', wiki+'Envelope.md', wiki+'Forward.md', wiki+'Learning.md'])

    add('feedback-state', 'Laplace — what each feedback lane may change', r'''
!pragma useVerticalIf on
start
:23.1 Observe a pass, its consequences or later evidence;
:23.2–23.3 Classify the signal and its provenance;
if (Feedback lane?) then (Fast cognition)
 :Change working frontier / obligations\nno testimony, calculus or firmware change;
elseif (Effect / outcome?) then (yes)
 :Retain typed consequence and occurrence\nexternal observer rates the responsible firmware;
 :Do not re-rate claims merely because the act succeeded;
elseif (Independent evidence?) then (yes)
 :Admit independent observation / testimony\nfold claim standing into an adjudicated epoch;
elseif (Procedure?) then (yes)
 :Retain successful / failed pass trajectories\nevidence for reusable program, not claim truth;
 :23.6 Skill under activation contract\nhabit changes proposal order only;
elseif (Physical execution?) then (yes)
 :Use CPU / latency / I/O / boundary measurements\nto evaluate an equivalent accelerated path;
 :Keep semantics, authority and admissibility checks;
else (Candidate discovery)
 :23.7 Persistent typed vacancy / contradiction\nfailed prediction or outcome;
 note right
An empty query result alone is not a vacancy.
 end note
 :23.8 Hypothesis with predicted signature and ancestry;
 :23.9 Fit permitted evidence\nevaluate pinned disjoint evidence\nseek counterexamples and charge complexity;
 if (Survives evaluation and has explicit activation authority?) then (yes)
  :23.10 Activate a new version\nkeep predecessor replayable;
  :Reality supplies occurrence, testimony or experiment\npredicted occupant cannot fill its own vacancy;
 else (no)
  :Keep candidate inactive\nretain rejection as negative knowledge when rejected;
 endif
endif
:Rejoin subsequent passes through shared canonical state\nor the current pass's bounded working projection;
stop
legend
Firmware does not grade itself. Its standing does not multiply claim standing.
Replay and retries create no new matchups. Self-generated descendants
cannot corroborate their ancestor hypothesis. C1 and C3 remain explicit conflicts.
Source: Sequence/Learning.md, operations 23.1–23.10.
endlegend
''', [wiki+'Learning.md'])

    add('model-admission-export', 'Laplace — checkpoint evidence and constructed model export', r'''
!pragma useVerticalIf on
start
if (Requested operation?) then (Admit checkpoint)
 :24.1 Stage source generation\nconfig / tokenizer / shards / sidecars\nfile trunks + checkpoint source witness;
 :24.2 Native container read\nmetadata once / transient numeric tiles;
 :24.3–24.4 Admit ordered tokenizer vocabulary\nand canonical configuration facts;
 :24.5 Match tensor shapes to operator templates;
 if (Role established?) then (yes)
  :24.6 Derive circuit composition + Projection physicality\nmodel / layer / head retained as source context;
  :24.7 Apply declared significance calculation\nwrite attributable claims through shared admission / fold;
 else (no)
  :Retain ambiguous or unrecognized role\nnever guess an operator;
 endif
 :24.11 Receipt decoded work, reuse, circuits, claims\nprovider / precision / recipe / result fingerprints\nraw_weight_bytes_retained = 0;
 note right
Weights are transient operands in this contract.
This is not the Conventional training pipeline.
 end note
elseif (Separately requested model execution?) then (yes)
 :24.8 Pin checkpoint / input / runtime / tokenizer\nprecision / provider / seed / decoding recipe;
 :Admit versioned calculated transformation + receipt\nrepeat occurrences are not independent corroboration;
elseif (Query model evidence?) then (yes)
 :24.9–24.10 Select authorized source or pooled evidence\nrun one shared forward program;
 :Keep provenance and disagreements inspectable\nno hidden judge over separate model answers;
else (Construct export)
 :25.1 Require EXPORT for boundary crossing\nresolve source / context / evidence scope;
 :25.2 Deposit versioned Model_Recipe as content\nread the admitted recipe;
 :25.3 Assign operator per target slot\nrelation / metric / trajectory / coord / spectral / unary;
 :25.4 Build bounded circuits from eligible witnesses\nretain support, uncertainty, refutation and provenance;
 :25.5 Derive every tensor in bulk\nslot-to-cell calculation receipts;
 if (Target codec represents declared construction?) then (yes)
  :25.6 Emit model artifact + reproducible provenance;
  :25.7 Load in declared external runtime\nheld-out semantics + source ablation + provenance checks;
  if (Consumer acceptance passes?) then (yes)
   :Validated consumer artifact;
  else (no)
   :Retain failed validation and its evidence;
  endif
 else (no)
  :Explicit codec limitation;
 endif
endif
stop
legend
Specification contracts 24.1–24.11 and 25.1–25.7.
Export requires a correctly reading forward program and coherent seed.
A loadable artifact is not semantic acceptance or proof of the native substrate.
Source-scoped evidence is not a separate identity world or commercial intelligence tier.
endlegend
''', [wiki+'Models.md', wiki+'Export.md'])

    add('code-root-transition', 'Laplace — software construction, repair and governed deployment', r'''
start
:26.1 Bind exact grammar / runtime / toolchain / ABI / target;
if (Qualified target capability exists?) then (yes)
 :26.2 Resolve repository composition R0;
else (no)
 :Explicit missing capability;
 stop
endif
:26.3 COUPLE requirement with repository and repair evidence\nprefer exact reuse → composition → bounded adaptation → novelty;
:26.4 Construct changed subtree under grammar / type / API obligations;
repeat
 :26.5 Remint changed ancestry to candidate root R1\nunrelated subtrees remain shared;
 :26.6 REALIZE required source / AST / CST / IR in bulk;
 :26.7 Run declared compile / link / test / analysis / runtime providers;
 :26.8 WITNESS typed results\nexact code + toolchain + environment + analyzer identity;
 if (Verification obligations closed?) then (yes)
  :Retain verified root transition and proof;
 else (no)
  :26.9 Locate smallest divergent subtree\nconsult prior failures and repair trajectories;
  if (Lawful repair within authority and envelope?) then (yes)
   :Construct next delta;
  else (no)
   :Retain WHY_NOT and unexplained residual;
   stop
  endif
 endif
repeat while (Verification obligations remain?) is (yes) not (no)
:26.10 Find other authorized repositories warranting inspection\nexact / normalized / behavioral / trajectory evidence;
note right
Similarity nominates inspection; it does not grant
write, merge or deployment authority elsewhere.
end note
:26.11 Patch = FROM R0 TO R1\ntransfer missing closure + proof receipts\nactivation R1 / structural rollback R0;
note right
Database, configuration, secrets and external effects
remain explicit transition / compensation obligations.
Root rollback does not reverse mutable world state.
end note
if (Deployment requested and authorized?) then (yes)
 :26.12 Resolve current target state\nstage missing closure / declared migrations;
 :Verify target state / activate / observe / witness;
 if (Observed result violates obligation?) then (yes)
  :Execute declared rollback and compensation program;
 endif
endif
:26.13 Derive machine cost\ninstructions / dependency graph / target resources\nretain symbolic unknowns and measured calibration;
stop
legend
Specification operations 26.1–26.13 use shared Forward and Learning.
Syntax validity, compile success and behavioral correctness are distinct evidence.
This diagram does not claim the current CodeToolchain implements every obligation.
endlegend
''', [wiki+'Code.md', wiki+'Forward.md', wiki+'Learning.md'])

    add('frozen-game-learning', 'Laplace — chess and Knowledge Arena: play, compare, then learn', r'''
start
if (Domain?) then (Chess)
 :27.1–27.2 Compose canonical board vocabulary\nrecord reusable positions / moves and attributed game trajectories;
 :27.3 Keep recorder, analyzer and runtime sources distinct;
 :27.4 Pin Stockfish calculation boundary\nposition / move / binary / network / options / recipe;
 :27.5 Bind typed cross-modal evidence\nlexical meaning / motifs / book / game / calculation;
 :27.7 Freeze experiment generation\nconductor / opponents / openings / clocks / resources\nevidence epoch / firmware / adjudication / holdout law;
else (Knowledge Arena)
 :28.1 Compile MATCH / CONNECT / COMBINE / EXPLORE / PLAY obligation;
 :28.2 Pin ranked challenge generation\nepoch / ordered queue / scope / providers\nvisibility / scoring / timing / envelope;
 :28.3 Resolve pieces as canonical entities\nlabels and aliases are realization state;
endif
repeat
 :27.6 / 28.4 Run action through shared Forward\nCOUPLE → ORIENT → routed bounded operators\nlegal proposals → authorized selection → realization;
 :Append event trajectory and typed action receipt\nupdate state before the next action;
 note right
Exact legality and terminal state constrain chess.
Evidence standing cannot legalize a move.
Stockfish bestmove is not a hidden SELECT authority.
 end note
repeat while (Challenge permits another action?) is (yes) not (no)
if (Domain?) then (Chess)
 :27.8 Compare cumulative planes and matched ablations\nfixed reference / uncertainty / work / crashes / raw artifacts;
else (Knowledge Arena)
 :28.5 Score by declared components\nvalidity / obligations before style or novelty;
 :28.6 Receipt generation, exact transitions, evidence\nstanding, work, timing and trajectory fingerprint;
endif
:Close the frozen comparison;
:27.9 / 28.7 Admit artifacts into a later evidence epoch\nordinary testimony / fold / subsequent learning;
stop
legend
Specification flows; this depicts frozen comparisons, not every practice session.
Ranked / benchmark evidence cannot train its own reference world mid-match.
Produced games do not self-certify truth. Player ratings are not claim standing.
Chess source: 27.1–27.9; Arena source: 28.1–28.7.
endlegend
''', [wiki+'Chess.md', wiki+'Games.md', wiki+'Forward.md'])

    add('maintenance-lifecycle', 'Laplace — correction, generations and install verification', r'''
!pragma useVerticalIf on
start
:29.10 Bind selected install, serving revision and ownership\npreserve unrelated work / bounded resources / cancellation journal;
if (Maintenance operation?) then (Correct witness)
 :29.1 Remove selected witness contributions and trunk\nretain other witnesses and addressable prior evidence;
 :29.2 Sweep unreferenced objects top-down in sets\nretain Tier 0;
 :Replay affected standings\nadmit corrected source generation;
 note right
Containment-only forget is a target contingent on
equivalent replay proof, not the current row contract.
Database reset is a separately authorized operation.
 end note
elseif (Re-ingest?) then (yes)
 :29.3 Reuse existing identities\nexclude repeated testimony from another fold;
 note right #FFE4D6
CONTRACT CONFLICT:
29.3 says skip by the byte check of 11.2.
11.2 explicitly requires the composed trunk ID
and sends every artifact through decomposition.
The shortcut is not accepted as an equivalent flow.
 end note
elseif (Registry change?) then (yes)
 if (Semantics or historical bit coverage changes?) then (yes)
  :29.5 Reseed from materialized rows;
 else (append only)
  :Append stable bit / code\nretired bit stays reserved; no reseed;
 endif
elseif (New Unicode release?) then (yes)
 :29.9 Require final data and segmentation tooling\nrepeat stages 1–5 with new fingerprint;
 :Preserve existing content IDs\nrecompute dependent coordinates / Hilbert from leaves;
elseif (Extension upgrade?) then (yes)
 :29.6 Rebuild and install coherent modules\nALTER EXTENSION UPDATE through versioned scripts\nverify installed catalog; retain indexes;
else (Changed cache dependency)
 :Identify changed recipe / manifest / lower cache / hot set;
endif
if (Derived cache dependencies changed?) then (yes)
 :29.4 Rebuild only named dependents\npublish atomically; loader refuses stale generation;
endif
:29.7 Probe serving application / native libraries\nPostgreSQL execution module / Tier-0 fingerprint\nvocabulary and domain floors / substrate invariants;
if (Shared ingest spine changed?) then (yes)
 :29.8 Cross-source legal physical-plan matrix\nrequire identical durable semantic fingerprints;
endif
:Retain independent verification evidence\nsummary output alone does not prove the install;
stop
legend
Specified maintenance operations 29.1–29.10.
Failure recovery must use the actual implementation's commit and publication boundaries.
No rollback of independently committed state is implied by an activity arrow.
endlegend
''', [wiki+'Maintenance.md', wiki+'Content.md'])
