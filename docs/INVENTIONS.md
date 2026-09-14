# Laplace invention catalog

This catalog names the mechanisms and preservation laws of the invention without pretending to report implementation status. The complete argument and proofs belong in [INVENTION.md](INVENTION.md); architecture-as-built belongs in [ARCHITECTURE.md](ARCHITECTURE.md); active acceptance belongs in GitHub; annotated historical catalogs remain under `docs/archive/`.

The catalog is deliberately implementation-aware without making today's finite machine choices into mathematical limits.

## Identity, bounded physicality and exact composition

1. **Content-addressed identity as the join.** Equal canonical content under one declared recipe converges across sources and modalities without a separate source-specific identity namespace.
2. **Content convergence is not a hash collision.** Normal convergence means equal canonical content produced the same executable id. A true cryptographic collision is a distinct implementation event and must be detected/handled as such.
3. **Tier as compositional altitude rather than ontology.** Tier describes a selected decomposition/composition ladder; it does not redefine canonical content identity or impose one semantic hierarchy on every modality.
4. **Finite/countable basis, unbounded finite composition family.** A finite or countable admitted basis can form countably unbounded finite recursive compositions. The current atom window is not the size of the representable composition family.
5. **Bounded recursive placement.** Under the current native centroid law, children in a closed bounded ball produce a parent in that same ball; induction keeps every finite recursive composition inside the fixed domain.
6. **Convex realized-path closure.** A realized trajectory resolves child physicality coordinates; line segments between points inside a convex ball also remain inside it.
7. **Radix- and dimension-independent construction law.** The recursive/countability and convex-closure arguments do not depend on binary notation or exactly four dimensions. Radix, dimension and numeric format are executable representation choices.
8. **S³ Super-Fibonacci atom placement.** The current text/primitive generation uses deterministic, near-uniform placements on the unit 3-sphere as the boundary of the current 4D frame.
9. **Hilbert content locality.** A locality-preserving serialized address supports indexed physical discovery without becoming canonical identity.
10. **Typed physicalities.** One canonical entity may participate in point, trajectory, factor, projection, board, model and other typed physical realizations without multiplying identity.
11. **Lossless constituent trajectories.** Ordered composition retains the exact constituent identities/order required to reconstruct the composition independently of a conventional tokenizer context window.
12. **Exact 212-bit GeometryZM carrier.** Four binary64 components provide four 53-bit reversible carrier slots: exactly 128 constituent-id bits + 16 packed ordinal bits + 16 run-length bits + 52 flags/typed metadata bits.
13. **Carrier width is not composition width.** Packed ordinal/run fields are local carrier fields; logical positions and split runs allow compositions wider than the packed field itself.
14. **Packed manifest versus realized curve.** Mantissa-packed vertices are exact constituent manifests, not semantic positions. Geometry resolves the child ids to their live physicality coordinates and orders those coordinates by logical ordinal before curve operations.
15. **Dimension/payload trade.** The current 4D binary64 carrier fits the complete 212-bit vertex payload exactly. 3D still fits the current 128-bit id with less metadata; 2D does not fit that id in one current carrier vertex. This is a format trade, not a theorem that 2D composition is impossible.
16. **Exact occurrence by indexed containment.** A canonical entity can reach trajectories/compositions containing it without rescanning source text or relying on ANN guesses.
17. **Perfcache as derived ROM.** Deterministic mmap/read-mostly structures accelerate canonical state while remaining rebuildable derivatives rather than a second authority.
18. **Current finite machine windows are replaceable.** Hash width, binary64, Unicode generation, ordinal carrier fields, CPU address width and database capacity constrain one executable generation; they do not redefine the abstract invention.

## Evidence, relations and consensus

19. **Universal attestation reduction.** Facts from corpora, users, tools, games, code and checkpoints reduce to typed subject/relation/object/source/context/outcome evidence.
20. **Content/evidence/consensus separation.** Identity converges, witnesses remain attributable and propositions acquire standing without erasing their evidence roots.
21. **Glicko-2 epistemology.** Rating, deviation, volatility, source semantics, witness count and conservative score retain support and uncertainty rather than one opaque confidence number.
22. **Signed three-valued testimony.** Confirmation, draw/indeterminate evidence and refutation remain distinct; absence is not silently converted into falsehood.
23. **Provenance/aggregation duality.** Context-scoped occurrences and folded proposition standing coexist without erasing one another.
24. **Record-versus-calculate boundary.** Literal observation and versioned deterministic/analytic calculation remain distinguishable witnesses.
25. **Tier-correct attestation.** Evidence attaches to the compositional object the source actually asserts rather than being sprayed across every constituent.
26. **Governed relation registry.** Relation identity, aliases, append-only highway bits, salience bands, rank and physical hotness remain explicit axes.
27. **Trust-class lattice.** Curated sources, tools, conventional models, responses and feedback have declared source semantics rather than one untyped global weight.
28. **Dependence-aware evidence.** Multiple paths/witnesses are useful only to the degree their evidence roots are actually independent; provenance remains available to prevent duplicate dependence from masquerading as independent confirmation.

## Ingestion and the knowledge mesh

29. **One ordered ingest spine.** Enumerate/unpack, stream, decompose, deduplicate, accumulate, persist and fold under one deterministic protocol rather than source-private execution engines.
30. **Pure omni-modal decomposers.** Source adapters recover source structure and emit the common substrate change algebra; they do not own private identity, SQL-write or consensus laws.
31. **Modality as grammar, not architecture.** Text, code, chess, images, audio, models and other finite structures use different typed decomposition grammars while sharing identity/physicality/evidence machinery.
32. **Interlingual convergence mesh.** Lemmas, senses, concepts, frames, roles and multilingual surfaces connect through canonical identities and typed witnesses rather than being flattened into one language-specific vocabulary.
33. **Narrow parser authority.** Tree-sitter, UAX algorithms and file/format parsers unpack declared structure; parser output is not automatically semantic truth.
34. **Seed profiles as capability contracts.** Foundation, linguistic, conversational, code, model and domain seed sets declare what evidence/calculation surfaces are expected from a deployment.
35. **Canonical reuse under repeated observation.** Re-observing known content reuses structure and adds occurrence/provenance/evidence instead of duplicating the canonical object.
36. **Novelty and observation volume are separate growth axes.** Structural novelty may taper as the world fills while evidence/occurrence volume continues to grow; any exact growth rate is measured rather than assumed.

## Universal execution-grain law

37. **PostgreSQL stores/indexes the world; native code executes repeated algorithms.** PostgreSQL owns persistence, MVCC, transactions and index/set access. Native C/C++ owns hot loops, recursion, parsing kernels, composition, trajectory work, fanout/search, reductions, encoding and materialization.
38. **SPI as set-sized bridge.** SPI connects native operators to indexed PostgreSQL state through prepared/bounded set operations; per-row dynamic SPI is not the architecture merely because the caller is written in C.
39. **C#/SQL as orchestration boundaries.** Managed code and SQL own source/session/service orchestration, contracts and transport rather than reimplementing inner-loop substrate algorithms.
40. **Execution grain is universal.** The same rule applies to decomposition, ingestion, reads, cognition, analysis/domain engines, reconstruction, synthesis and export. Optimizing one lane while leaving another as RBAR does not satisfy the design.
41. **Batch is semantic, not nominal.** A `Batch` API whose body loops scalar DB/PInvoke/SPI calls is still RBAR. Repeated work belongs in one canonical coarse native/set operator.
42. **Boundary elimination as first-class performance work.** Removing per-row planner/marshalling/function/transaction/parser/materialization crossings can turn millisecond orchestration into microsecond native operations without changing semantics.
43. **Sparse selection × coarse execution.** The performance thesis combines less work selected through identity/indexes/reuse/hops/fanout with less overhead per selected unit through native/set-sized execution.
44. **SIMD/GPU as additional providers, not the premise.** Wider ISA and accelerators can add headroom, but the architecture should already avoid unnecessary work and boundary crossings on ordinary CPUs.

## Chess and games as executable trajectories

45. **Outcome bit identity.** Game outcomes and epistemic outcomes share a signed fold domain where the typed contract permits it.
46. **Move/position consensus.** Transpositions converge while game/player/source occurrences remain attributable.
47. **Board modality ladder.** Pieces, squares, moves, positions, lines and games form deterministic typed structures above the common substrate.
48. **Flat-cost witnessed depth.** Completed trajectories can deposit deep outcomes so later reads may use retained evidence rather than replaying every historical search.
49. **Game as forward program.** Compose state, orient task, propose legal actions, steer/select, realize and witness through the same operation machinery.
50. **Annotations as shared entities.** Commentary concepts connect dictionary meaning, move instances, sequence context and evaluation testimony.

## Conventional models as witnesses

51. **Checkpoint-as-source.** SafeTensors/config/tokenizer/tensor structure enters as source-scoped recorded/calculated state rather than an opaque runtime authority.
52. **Circuit testimony trajectories.** Layers, heads, experts, factors, projections, token effects and completions can be represented with exact source/recipe provenance.
53. **Structural-versus-functional circuit identity.** Source coordinates can align structural roles while functional correlation remains separately calculated evidence.
54. **Cross-architecture circuit cube.** Entity × source × plane × circuit queries can compare causal, encoder, reranker, MoE and multimodal models without pretending the architectures are identical.
55. **Model round table as pooled evidence.** Heterogeneous models/sources may contribute to one decision without tensor merging, equal architectures, voting or one hidden judge model.
56. **Consumer-role model anatomy.** Q/K/V/O, embeddings, FFN Gate/Up/Down, MoE/MLA roles, norms, heads and experts are conventional source/target architecture roles; they are not the native ontology of Laplace cognition.

## The spider-colony web and query-relative cognition

57. **Many recursive DAGs plus cross-links, one shared identity fabric.** A canonical entity may simultaneously be a constituent, container member, occurrence, relation endpoint, evidence target, context participant and geometric neighbor. These overlapping structures form the web.
58. **Tug-a-strand response primitive.** Given an active structure, enumerate what responds, through which indexed/typed routes and with what structural/evidential/geometric/provenance response.
59. **Convergent routes are signal.** Multiple compatible routes reaching the same canonical candidate are part of its query-relative evidence; they are not flattened away as duplicate graph edges.
60. **Whole-observation perturbation.** A prompt/request is first one exact admitted observation/root with constituent occurrences, discourse bindings, scope and open obligations—not a bag of independently interpreted tokens.
61. **Query-relative coupling field.** The active observation perturbs every eligible indexed plane; the substrate exposes typed responses before one interpretation/provider/search mask is frozen.
62. **Joint interpretation before unconstrained policy.** Candidate senses/bindings constrain one another through the rest of the observation. Unique, ambiguous, impossible and resource-bounded dispositions can emerge from the jointly compatible state.
63. **Typed channels remain typed.** Structure, role compatibility, relation identity, ordinal/gap state, evidence, contradiction, standing, source dependence, geometry and provenance are not prematurely collapsed into one universal relevance score.
64. **Policy normally follows interpretation.** Caller-supplied relation/provider masks are valid explicit constraints, but a convenience mask must not masquerade as cognition when Laplace is supposed to infer what the observation means.
65. **Sparse star expansion.** Once oriented, cognition expands indexed responders around the current root/frontier rather than performing an all-world comparison.
66. **Hops and fanout as compute coordinates.** Search depth, branch/frontier width, source/operator scope and other explicit budgets govern how much of the same knowledge world one request activates.
67. **A*/Dijkstra/walk/trajectory/geometry as operators, not cognition itself.** Search algorithms execute within the selected program; none alone defines interpretation or intelligence.
68. **Dynamic-frontier autoregression.** Every selected/emitted constituent changes the active trajectory, bindings and obligations, causing the next response field/frontier to be recomputed.
69. **Functional Q/K/V/O reconstruction without transformer math.** Active observation/bindings/obligations play the Q role; indexed typed addresses play K; coupling is QK-like relevance; responding facts/evidence/calculations play V; the receipted fold into updated state plays O.
70. **Named channel/head analogues.** Relation/provider/tier/context/operator planes remain explicit rather than anonymous latent attention heads.
71. **No fixed context-window ontology.** Historical discourse remains addressable content/occurrence state; per-request resource boundaries limit execution without making older knowledge structurally unreachable.

## Conversation, operations and serving

72. **Conversation as witnessed trajectory.** Sessions contain ordered turns, bindings and obligations; corrections/dependencies add evidence without deleting history.
73. **Code as player.** Generate, stage, compile/test, witness outcomes and feed them into subsequent decisions through the same operation substrate.
74. **Explainability as typed receipt.** Answers/operations can expose bounded cells, routes, scores, evidence roots, source scope, stages, selection and writes rather than one opaque confidence score.
75. **Closed self-improvement loop.** Prompt, response, tool, evaluation and feedback outcomes can deposit through governed lanes and affect later standing/cognition without offline retraining.
76. **One typed operation ISA.** SQL, MCP, OpenAI-compatible serving, games, code, model inspection and export compose the same governed operation algebra rather than inventing private semantics.
77. **OpenAI-compatible surface without transformer ontology leakage.** Compatibility binds roles/parameters/tools to Laplace semantics and reports unsupported/translated behavior honestly.
78. **MCP citizenship.** Agents read, witness, give feedback, ingest, inspect traces and invoke the same canonical operation surfaces under governance.

## Construction, synthesis and export

79. **The witnessed web as a Laplacian source.** Typed uncertainty-bearing evidence can supply graph/spectral structures for analysis/construction when the mathematical contract is explicit.
80. **Architecture derived from evidence.** Relation bands, tiers, hops, modalities, factors, trajectories and recipes can determine operator schedules/consumer materialization rather than copying one witness checkpoint as authority.
81. **Glicko-complete edge state.** Support, uncertainty, refutation, source semantics and witness saturation may reach construction/inference under declared recipes.
82. **Source-scoped synthesis.** A, B or pooled A+B constructions are reproducible diagnostic/export scopes without requiring another gradient-training cycle.
83. **Conditional continuation floor.** Exact witnessed continuation distributions and typed backoff can ground output selection.
84. **Typed residual/frontier.** Surface, lexical, concept, frame, circuit, code and domain strata remain named rather than anonymously superposed.
85. **Geometry as typed operator evidence.** Angular, trajectory, containment, Hilbert, Procrustes and related physical calculations contribute only through explicit contracts; measurable geometry does not become semantic truth by default.
86. **Recipe-driven decomposable export.** Constructed slots/materializations trace to substrate state, calculations, scope and deterministic recipe inputs.
87. **Codec-independent native state.** SafeTensors, GGUF and other formats are inputs/outputs/consumer materializations, not the ontology of the live substrate.
88. **Bulk reconstruction/export law.** Exact output must be streamed/materialized through coarse native/set operations rather than one constituent/tensor value/output row per high-level boundary crossing.

## Resource, billing and capacity laws

89. **One knowledge world, variable compute.** Product tiers change the execution envelope, not the amount of world knowledge Laplace is allowed to know.
90. **Preflight from the real physical plan.** `EXPLAIN`/planner state can estimate hops, fanout, rows/cells, operators, provider work, I/O and other billable dimensions before expensive execution.
91. **Reserve, enforce, receipt, reconcile.** Compute credits can be reserved against an admitted upper bound, enforced during execution, measured from actual work and reconciled/refunded afterward.
92. **Adaptive early completion.** If convergence/uncertainty/obligation state is already sufficient, execution may stop before consuming the full envelope and return unused reserved capacity.
93. **Serviceable capacity versus saturation.** Throughput on a managed host is only serviceable capacity if required database/product/runner/control-plane health remains available. Full logical-CPU saturation is a separate explicit experiment.
94. **Benchmark receipts bind source, artifact, host and provider.** Performance evidence names the exact revision, built artifact, machine topology, selected execution provider, workload and resource boundary rather than reporting context-free `tokens/s`.
95. **Work-normalized evidence.** Token-equivalent rates are useful familiar normalizations, but receipts also report actual codepoints, recursive structural nodes, selected candidates/frontiers, evidence cells, paths/relations and other work the operation really performed.

## Cross-domain preservation laws

96. **Exact reusable subtrajectory identity.** Repeated ordered subpaths are reusable canonical structures under their declared trajectory recipe while each containing occurrence remains attributable.
97. **Identity-versus-realization separation.** SAN/PGN/FEN, labels, aliases, languages, codec fields, source paths and model tensor names are contextual realizations/references rather than canonical identity owners.
98. **Composition/trajectory/occurrence symmetry.** Text, chess, models, code and other modalities instantiate the same atom → composition → higher composition → trajectory/reusable structure → witnessed occurrence pattern under different typed grammars.
99. **Deterministic execution trajectory.** A conventional model forward pass, chess calculation, tablebase probe, code execution or other deterministic provider run is a declared transformation trajectory under its complete inputs, generation, numeric boundary, implementation and recipe.
100. **Information-shaped materialization.** Store irreducible admitted information once, reuse canonical compositions, derive deterministic views and materialize only measured rebuildable accelerators or required consumer artifacts; occurrence volume does not justify duplicate content or eager world-all-pairs persistence.
101. **Navigable entity world.** Canonical content, occurrences, provenance, calculations, testimony and realizations remain addressable through common semantic operations; UI state does not own the semantics.
102. **Whole-prompt-root cognition and participant/source separation.** A prompt is one exact canonical observation/trunk. Decomposition supports descent/rise without privileging a noun/topic/regex as interpretation root, and individual participants retain their own earned context/standing rather than inheriting source-class trust into every constituent.
103. **Structural geometry/semantic-web orthogonality.** S³/ball coordinates, trajectory geometry, Hilbert locality and related indexes describe/nominate structural state. Typed semantic relations, testimony, deterministic calculations, dependence and standing remain distinct.
104. **Falsifiable operator-generation law.** Sparse intersections/dot products, incidence/transport, Laplacian/spectral methods, Lanczos, QR/Gram-Schmidt, SVD, Procrustes, geometric metrics, Glicko-2, A*/best-first and other mathematics apply only through contracts naming input state, assumptions, scope/evidence roots, direction/roles, numeric boundary, resources, output, loss/approximation, provenance and counterexamples.
105. **Theorem / implementation / witness / benchmark separation.** Mathematical closure is proved mathematically; executable serialization and reconstruction are tested; finite windows may be exhaustively checked; live databases supply implementation witnesses; performance claims require exact receipts. None is silently substituted for another.

## Product identity

Laplace replaces opaque probabilistic runtime authority with exact reusable structure, bounded physicality, source-retaining testimony, uncertainty-bearing consensus, query-relative web response, sparse hop/fanout execution, coarse native operators and deterministic receipts.

Models, corpora, conversations, code, games, tools and users are participants in one witnessed world rather than disconnected knowledge silos or separately trained intelligence products.
