# 18 — TYPED RESIDUAL STREAM & THE LEXICAL-SEMANTIC MESH
## how the ingested resources interlink, and how that mesh becomes the synthesized model's coordination layer

Date: 2026-07-08. Status: living spec. Scope: the tandem design — how layers/heads/tensors
of a Mold-A-Model export cooperate through ILI/synsets/frames/roles instead of mashing in
an anonymous shared subspace. Companions: 09 (thesis), 12 (primitive→slot map), 14
(diagnosis M1–M5 / prescriptions P1–P10 — this doc unifies P2/P3/P4b/P6/P7 and extends
P3), 16/17 (tier-correct attestation, decomposer audit). Every emission site below was
code-verified 2026-07-08 (four-agent audit; sites cited inline).

================================================================================
SECTION 0 — THESIS: COORDINATION THROUGH NAMED STRATA
================================================================================
A trained transformer's heads cooperate because SGD forces them to negotiate a shared
latent code in the residual stream — an unnamed, superposed, emergent interlingua that
nobody can address, audit, or update. The substrate does not need to DISCOVER an
interlingua: it INGESTED one. ILI (the interlingual index) is literally that — the
canonical concept identity that 393 languages' wordnets, plus frames, verb classes, and
rolesets, already converge on (§1). So the synthesized model's internal representation should
be ALLOCATED to the ontology's strata, not left anonymous:

  - A token id = an entity id at whatever tier the content is densest (tier-floor law:
    same content = same hash at every tier). Tokens are not arbitrary indices.
  - The residual stream = named, orthogonally-allocated subspaces per semantic stratum
    (§2). Heads are TYPED MAPS between strata. Cross-stratum write-collision becomes
    structurally impossible — mashing is what coordination looks like when the bus is
    unnamed; name the bus and the mash has nowhere to happen.
  - The layer stack = the resolution ladder (§3), not repeated diffusion.
  - Head firing is CONDITIONAL, gated by synthesized selectors (§4): the prompt's own attested
    content (EVOKES_FRAME, definitions, highway bits) chooses which relations apply.

Contrast, stated once: conventional AI computes the tier hierarchy (tokenization,
sentence segmentation, document packing) during training prep and THROWS IT AWAY — the
runtime model keeps a flat token stream and a frozen external tokenizer; "which sentence
of which document" is not answerable at any compute cost. Laplace keeps the hierarchy AS
the identity system: Merkle ids ARE the composition, mantissa-packed trajectories ARE the
ordered decomposition (walkable trunk→leaf via laplace_trajectory_constituents, leaf→trunk
via the constituents GIN), O(tier) traversal bottoming out in the mmap'd t0 perfcache with
zero DB reads. The hierarchy the field re-learns approximately per model (BLT's dynamic
patches, hierarchical attention) is here a lossless address space. Usage evidence GROWS
(Wiktionary/Tatoeba/OpenSubtitles HAS_EXAMPLE + every TurnWitness deposit); the substrate
is never static — only an export is static, a release build cut from a living source tree.

================================================================================
SECTION 1 — THE MESH INVENTORY (code-verified emission sites)
================================================================================
Convergence mechanism: every node id is content-addressed from a canonical key; two
decomposers producing the same key produce the SAME node — the collision IS the mesh.
Minting helpers (app/Laplace.Substrate/Abstractions/): ContentEmitter.RootId /
ContentTierSpine.ResolveRoot (surface text, space-normalized); CategoryAnchor.Id
(CategoryAnchor.cs:46 — VerbNet class, FrameNet frame/LU, PropBank roleset, WordNet
sense; identity is the bare key, typing via a separate IS_TYPED_AS edge so the identity
is shared across sources); ConceptAnchor.SynsetId (ConceptAnchor.cs:57 →
SourceEntityIdConventions.WordNetIli → ContentEmitter.RootId(ili)) — the synset node is
addressed BY ITS ILI STRING. Shared key normalizers: SourceEntityIdConventions.cs
(NumericVerbNetClassId :122, NormalizeSenseKey :110, FrameNetLuKey :178,
ResolveSynsetAnchor :199, ParseMcrSynsetKey :138, WordNetIli :90).

MESH POINT A — synset ≡ ILI (the master hub). One node per interlingual concept; the
witnesses that converge on it:
  - CILI mints it (CILIDecomposer.cs:87,96,162): IS_TYPED_AS WordNet_Synset,
    HAS_DEFINITION, HAS_SYNSET_KEY.
  - WordNet (WordNetDecomposer.cs:266,286 anchor): IS_SYNONYM_OF from lemma :296,
    IS_SENSE_OF from sense :371,381, all pointer relations :351
    (IS_A/HAS_PART/ENTAILS/CAUSES/...).
  - OMW (OMWGrammarWitness.cs:56): multilingual lemma IS_SYNONYM_OF synset, defs/
    examples with language context — 393 languages, one knot.
  - ConceptNet /c/en/word/n/wn/... suffix (ConceptNetDecomposer.cs:84-85):
    CORRESPONDS_TO synset.
  - Wiktionary sense links/senseids/wikidata (WiktionaryEmit.cs:194,199 — the emission
    moved out of WiktionaryGrammarWitness.cs, which is now 33 lines):
    CORRESPONDS_TO synset.
  - SemLink pb-wn/vn-wn/fn-wn (SemLinkGrammarWitness.cs:158): category
    CORRESPONDS_TO synset.
  - PredicateMatrix (PredicateMatrixIngest.cs:73,85-108): roleset/frame/vnclass
    CORRESPONDS_TO synset via MCR/ILI per row.
  - VerbNet member wn sense keys (VerbNetDecomposer.cs:105-110): CORRESPONDS_TO sense.
  - MapNet (MapNetIngest.cs:38; FnLuSynsetBridgeIngest.cs:36) and WordFrameNet
    (FnLuSynsetBridgeIngest.cs:36,81): frame/LU CORRESPONDS_TO synset.
  VERSION SKEW: WordNet/SemLink/PredicateMatrix target pwn30; MapNet/WordFrameNet pwn16;
  reconciliation is ENTIRELY the CILI map (ili-map-pwn*.tab/ttl) collapsing offsets onto
  one ILI. Failure asymmetry: OMW/SemLink/MapNet/WordFrameNet HARD-FAIL if the CILI map
  is missing (EnsureCiliMapForIngest); WordNet/ConceptNet only WARN and silently drop
  their synset anchors (WarnIfCiliMapMissing) — a known trap.

MESH POINT B — VerbNet class node (CategoryAnchor.Id(NumericVerbNetClassId), type
VerbNet_Class). Authority VerbNetDecomposer.cs:70 (MEMBER_OF_VERBNET_CLASS :99, subclass
IS_A :89); re-hit by SemLink pb-vn2 :53,57 / vn-fn2 :86,99 / external_vn2pb :116,129,
PropBank rolelinks (PropBankDecomposer.cs:170,177), PredicateMatrix :101-108, and as the
CONTEXT id of every ROLE_CORRESPONDS_TO row (SemLinkRoleMappingIngest.cs:57,78).

MESH POINT C — FrameNet frame node (CategoryAnchor.Id(frameName), type FrameNet_Frame).
Authority FrameNetDecomposer.cs:257; HAS_FRAME_ELEMENT :307; frame-to-frame
INHERITS_FROM/FRAME_USES/PERSPECTIVE_ON/HAS_SUBEVENT/CAUSATIVE_OF/INCHOATIVE_OF/PRECEDES/
ALSO_SEE :341-355 (map :37-47); FE-to-FE REQUIRES/EXCLUDES :320-327. EVOKES_FRAME into it
from: FrameNet LUs (:338; FrameNetLuIngest.cs:128,164), fulltext targets (:200), VerbNet
member fnframe attrs (VerbNetDecomposer.cs:116-119). Re-hit by SemLink vn-fn2/fn-wn,
PropBank FrameNet rolelinks, PredicateMatrix, MapNet.

MESH POINT D — FrameNet LU node (CategoryAnchor.Id(FrameNetLuKey(frame,lu)), type
FrameNet_LU). Minted FrameNetLuIngest.EmitLu :117; re-hit by WordFrameNet & MapNet lu
files (FnLuSynsetBridgeIngest.cs:39-45) attaching CORRESPONDS_TO synset.

MESH POINT E — PropBank roleset node (CategoryAnchor.Id(rsId), type PropBank_Roleset).
Authority PropBankDecomposer.cs:94 (HAS_SENSE :99, HAS_SEMANTIC_ROLE :136); re-hit by
SemLink pb-vn2/pb-wn/external_vn2pb (SemLinkGrammarWitness.cs:43,126) and PredicateMatrix
:83-90.

MESH POINT F — WordNet sense node (SenseAnchor.Id("lemma%ss:lex:id"), type
WordNet_Sense). Authority WordNetDecomposer.cs:371,381; re-hit by VerbNet member wn keys
and PredicateMatrix ColWnSense (PredicateMatrixIngest.cs:27,77).

MESH POINT G — word SURFACE node (ContentTierSpine, space-normalized). The
surface-collision join: WordNet lemmas, OMW lemmas, ConceptNet terms, Wiktionary words,
VerbNet members (VerbNetDecomposer.cs:96), PropBank predicate lemmas
(PropBankDecomposer.cs:83) all land on one node by identical normalized surface. This is
the ONLY join for un-sense-tagged ConceptNet/Wiktionary terms — polysemy rides on the
surface node until a sense/synset link (A/F) disambiguates. Design consequence: the W→C
(word→concept) map is many-to-many and context-dependent — the WSD layer of §3 exists
because of this mesh point.

ROLE-LEVEL ALIGNMENT — ROLE_CORRESPONDS_TO (equivalence band, .82): PropBank arg ↔
VerbNet theta (PropBankDecomposer.cs:184), SemLink pb-vn arg↔theta
(SemLinkGrammarWitness.cs:67), SemLink VN-FN role mapping (SemLinkRoleMappingIngest.cs:75),
PredicateMatrix VN-role↔FN-FE (:118-123). All carry the VerbNet class as context_id — the
role alignment is SCOPED, not global.

PREDICATE MATRIX = ENGINEERED SECOND WITNESS. Distinct source id
(PredicateMatrixDecomposer/v1, minted SemLinkSources.cs:31,33 and declared
PredicateMatrixIngest.cs:18,327) so consensus counts
PM and SemLink as independent witnesses on the same VN↔FN↔synset edges — redundancy in
the evidence layer, on purpose. Beyond SemLink it adds direct WN-sense anchoring of
roleset/frame/vnclass, VN-role↔FN-FE alignment, and per-row MCR/ILI resolution across
pwn15/16/17/20/21/30 (McrVersionToPwn :166).

Salience context (relation_types.toml): CORRESPONDS_TO / ROLE_CORRESPONDS_TO /
IS_SYNONYM_OF / IS_TRANSLATION_OF / HAS_SENSE all sit in the equivalence band (.82);
EVOKES_FRAME / INHERITS_FROM / MEMBER_OF_VERBNET_CLASS taxonomic (.90); HAS_FRAME_ELEMENT
/ HAS_THEMATIC_ROLE / HAS_SEMANTIC_ROLE partitive (.73). IS_SYNONYM_OF and
IS_TRANSLATION_OF share family root SEMANTIC_EQUIVALENCE for read-time family queries but
stay distinct in consensus (relation_types.toml :810 IS_SYNONYM_OF / :818
IS_TRANSLATION_OF, both `family_root = "SEMANTIC_EQUIVALENCE"`) — recording never mashes.

What the mesh yields, in one sentence: surface → lemma → sense → ILI concept → frame/
class/roleset → roles is a FULLY ATTESTED, multi-witnessed, calibrated factorization of
"what does this utterance mean" — the exact factorization a trained model must smear into
one embedding table, and the substrate holds each arrow as a typed, Glicko-weighted,
provenanced edge family.

================================================================================
SECTION 2 — TYPED RESIDUAL ALLOCATION
================================================================================
Allocate d_model into named, orthogonal stratum subspaces (block-allocated at synthesis time;
block Gram-Schmidt — doc 14 P3 machinery — enforces disjointness):

  S  surface/positional identity — which token, where (hilbert content-PE dims + RoPE
     sequence position; doc 14 P7).
  W  word/lemma identity — the tier-2 entity (spectral dims of the word-level graph).
  C  sense/ILI concept — THE INTERNAL LINGUA FRANCA. All semantic relation planes are
     defined over C. Cross-lingual transfer is free: every language's surfaces map into
     the same C subspace because ILI is the address (§1 A).
  F  active frames + role bindings — which frames the prompt evoked, which arguments
     fill which roles (FrameNet FEs / VerbNet thetas / PropBank args, aligned by
     ROLE_CORRESPONDS_TO).
  G  relation-gate signals — which relation families the context has activated
     (highway-band indicator directions; feeds §4 gating).

Heads become TYPED MAPS between strata: a WSD head reads W+context and writes C; a frame
head reads C+W and writes F; a relation head reads C gated by F/G and writes C; a
realization head reads C and writes W/S. A WSD head CANNOT clobber a frame head — they
write disjoint subspaces. This extends doc 14 P3 (orthogonal allocation per relation
band) to allocation per ONTOLOGY STRATUM, which is the version that kills M2's write
collision structurally rather than statistically.

Capacity note: stratum widths are COUNTED, not chosen — W/C widths from the spectral rank
of their graphs (hidden='auto' already works per-graph), F from the count of frames with
witnessed LUs, G from the 13 bands. Same counted-not-chosen law as heads/layers.

================================================================================
SECTION 3 — LAYER SCHEDULE = THE RESOLUTION LADDER
================================================================================
Layers stop being repeated diffusion (doc 14 M3) and become the ladder, each layer's
operator an attested edge family:

  L-comp   composition: graphemes→words via containment/trajectory membership (doc 14
           C5; needs P2 gating). With the floor+merges vocab (Issue 44 / plan Phase 3),
           most in-vocab words skip this; it exists for OOV closure.
  L-wsd    word→sense: HAS_SENSE / IS_SENSE_OF planes, disambiguated by context
           coherence over the synset graph (graph-WSD as attention: neighbors' C
           components reinforce the compatible sense). Reads W, writes C.
  L-frame  predicate→frame: EVOKES_FRAME planes; role heads bind arguments to FEs/thetas
           (HAS_FRAME_ELEMENT / HAS_THEMATIC_ROLE planes, ROLE_CORRESPONDS_TO-aligned).
           Reads C+W, writes F.
  L-rel    relation application: F+G select which relation planes fire (§4); the selected
           head applies consensus(·,r,·) to the subject's C component. Multi-hop = stacked
           L-rel layers (the A* horizon). Reads C, writes C.
  L-real   realization: sense→lemma (synset members, IS_LEMMA_OF), lemma→surface. Reads
           C, writes W/S — feeds the lm_head (tier-2 continuation ∘ tier-3 sentence_order
           bridge; plan Phase 4).

This is doc 12's "LAYER = a hop/tier" and doc 14 P4b (tier ladder as layer schedule) made
concrete: the schedule is READ OFF the ontology, not authored per-recipe. Interim state
(current plan): layers remain recipe-authored; this section is the derivation target.

================================================================================
SECTION 4 — SELECTORS: CONDITIONAL HEADS FROM ATTESTED EDGES
================================================================================
The single biggest architectural absence in the current synthesis (14 §1.8, audit-confirmed):
no head is conditional on the prompt's MEANING — QK encodes "which tokens are related
under r," never "this context is ASKING about r." The selection knowledge is attested:

  - "capital" EVOKES_FRAME its frame; frames map to relation families; relation types
    are themselves entities with definitions. Synthesis EVOKES_FRAME/definition planes as QK
    so the prompt's own content activates the right relation heads. (The trained analogue
    — induction/task heads — is here a transcription of FrameNet, not a discovery.)
  - Highway band bits as FFN gates (doc 14 P2 / plan Phase 6): a token's gate is its
    band-membership indicator — a genuine content-dependent step function, synthesized from
    entities.highway_mask, no gradients.
  - G-subspace bookkeeping: frame heads write "band b active" directions into G; L-rel
    heads' o_proj are scaled by G alignment (soft gating) until true conditional
    execution (MoE, Milestone B) exists.

================================================================================
SECTION 5 — RELATIONSHIP TO DOC 14 (what this unifies / extends)
================================================================================
P2 (real gate) = §4 band gating. P3 (head allocation) = §2, extended from relation-band
slices to ontology strata. P4b (tier schedule) = §3. P6 (vocab + dialogue) = the token =
entity-id law + the L-real/lm_head composition. P7 (positions) = the S stratum. New here,
beyond 14: the SELECTOR synthesis (§4 EVOKES_FRAME-as-QK) and ILI-as-internal-basis (§2 C),
neither named in 14's prescriptions. The 2026-07-08 remediation plan
(.claude/plans/wild-orbiting-shamir.md — GONE as of 2026-07-20: `.claude/plans/` does not
exist, so every "plan Phase N" marker in this doc is now UNRESOLVABLE; doc 14 §6b is the
surviving execution log of that plan) implements the mechanical prerequisites
(P5/P4a/P3/P6/P7/P2 + rank adjudication); THIS doc is the build spec for the phase after
it: typed allocation, selectors, WSD/frame/realization layers.

================================================================================
SECTION 6 — CORRECTIONS TO THE RECORD (2026-07-08 audit; propagate + keep)
================================================================================
  C-1  Doc 14 M4 is STALE: the live default basis IS the normalized-Laplacian eigenmap
       (eigenmaps.cpp:56 — L = I − D^-1/2 W D^-1/2, Spectra/Lanczos low eigenvectors,
       trivial vector dropped, D^-1/2 row rescale); BuildBasisAffinity (raw SVD) is dead
       code (affRaw hardcoded null). hidden='auto' spectral-rank sizing IS implemented
       (FoundryCommands.cs:1020 validation, :1198 "hidden_size auto → spectral rank …").
       P1 is effectively done. M2 is HALF-fixed (per-head
       input subspaces distinct; write collision remains → P3).
  C-2  Doc 14 C3 is WRONG: no sentence-level turn/response plane exists in consensus.
       OpenSubtitles emits ONLY cross-language IS_TRANSLATION_OF line pairs
       (OpenSubtitlesDecomposer.cs:48); Tatoeba only translations; PRECEDES is
       word-tier only (TextEntityBuilder.BuildDistributionalAttestations). Sentence order
       [Superseded 2026-07-20: there is no `BuildDistributionalAttestations` (0 hits), and
       text emits NO PRECEDES at all. TextEntityBuilder.cs:241-248 (Pillar 3a): text emits
       its content DAG (entities + physicalities/trajectory) ONLY, `attestations =
       ImmutableArray<AttestationRow>.Empty`; the comment states PRECEDES is a MODEL
       relation (token couplings from Q/K/V/O/gate/up/down/norms), NOT text word-adjacency,
       and the word→word emission was DELETED. Corroborated by commit e150a9f. C-2's
       conclusion — no sentence-level turn/response plane in consensus — stands, and is now
       true a fortiori.]
       IS preserved — in tier-4 document trajectories — so the dialogue plane is a
       CALCULATED plane over witnessed trajectories (sentence_order, plan Phase 4), not
       an unpoured consensus asset.
  C-3  Highway bits: 182 assigned (highway_manifest.h:7 LAPLACE_HIGHWAY_REL_COUNT 182u)
       [this correction is itself superseded 2026-07-19 -> 203u; stop pinning the number
       here — docs/INVENTORY.md is the generated count authority],
       not 153 (CLAUDE.md stale; fixed 2026-07-08). Doc 09's "181 relation types"
       (2026-07-04 inventory) predates one addition.
  C-4  The Glicko fold consumes the CONTINUOUS mean score (sum_score_fp1e9/games,
       glicko2_fold_uniform_period, glicko2.c:346); the {0,1,2} outcome enum is stored
       classification only. Trust enters as opponent RD (witness φ ∈ [30,350],
       attestation_engine.c:136) — trust is INSIDE the rating math.
  C-5  Salience-rank adjudication — MEASURED 2026-07-08 (plan Phase R, read-side
       ablation, 12 probe words, hit@10 across all relation types):
         next-word intent:  manifest-rank 0.000   eff_mu-only 0.889
         knowledge intent:  manifest-rank 0.556   eff_mu-only 0.056
       VERDICT: a perfect double dissociation. The hand-authored ranks are a
       KNOWLEDGE-INTENT PRIOR — excellent at surfacing taxonomic/definitional
       objects, catastrophic when applied to generation (PRECEDES at 0.18 buries
       every corpus continuation). Neither table replacement nor vindication:
       the defect is UNIVERSAL APPLICATION of one intent's prior. Prescription =
       intent-conditional band weighting (doc 15 C-b's highway gating — intent
       selects which bands weight the read); on the export side, salience
       scaling now exempts continuation operators (FoundryCommands o_proj fill).
       "Counted, not chosen" resolution: the ranks stay hand-authored as the
       knowledge prior; what gets COUNTED is which prior each intent invokes.
  C-6  POS/SENSE/DEPREL QUERY-GRAIN AUDIT (2026-07-09, live-verified; prompted by
       the eff_mu-head observation that the consensus's global top is annotation
       scaffolding — IS_A 'NN'→'NOUN' at eff_mu 3214 / 1.24M witnesses):
       CLEAN — HAS_POS objects are ONE inventory: 18 UD-upos classes end to end
         (NOUN 4.57M subjects/22.1M witnesses … INTJ; one IDIO outlier, 27 words).
         No Penn tags as HAS_POS objects; corpus-witnessed and usage-dominant
         ('close': VERB 567 > ADJ 284 > ADV 61 > NOUN 53). pos_transition_plane
         v1's single-inventory + dominant-POS assumptions HOLD as synthesized.
       CLEAN — the multilingual hub is live and grain-consistent: top_synset('dog')
         → IS_TYPED_AS WordNet_Synset (typing de-conflated, correct);
         HAS_SYNSET_KEY '01595188-n' = POS AT HUB GRAIN (one fact, every language
         inherits); synset_members per-sense across languages (Hund=dog-animal,
         verfolgen=dog-chase, Sperrklinke=dog-catch); word-grain HAS_POS('Hund')
         = NOUN×48 consistent with the hub. "dog is a noun in German" is answered
         by evidence already ingested.
       DEFECT (recording, watch) — the XPOS→UPOS tag map is recorded as IS_A
         between class entities ('NN' IS_A 'NOUN'). Cross-schema correspondence
         is CORRESPONDS_TO's job; as IS_A it (a) tops every eff_mu-ranked
         undisciplined read, (b) reaches hypernym walks through PUNNED tokens:
         the synthesis vocab contains 'IN' and 'CD' — uppercase corpus words content-
         addressed to the SAME entities as the Penn tags. Benign for current
         recipes (no IS_A operator synthesized); any IS_A/crawl consumer must
         type-discipline first. Doc 16 hub-unification class.
       GAP (query depth, next synthesis) — IS_SYNONYM_OF is MIXED GRAIN under one
         family: synset→word co-membership spokes (multilingual) AND word→word
         direct edges. The vocab-joined plane correctly keeps word→word only
         (21,083 edges) but that is the WEAKER evidence — WordNet co-membership
         synonymy is 2-hop through the synset hub and invisible to every 1-hop
         plane. The S-stratum synthesis needs the derived projection: word→synset→word
         (same lang) server-side. Same shape for hub-POS and EVOKES_FRAME:
         the mesh's convergence points are queryable TODAY (senses/top_synset/
         synset_members all answer) and NO synthesized plane consumes them yet.

================================================================================
SECTION 7 — OPEN DESIGN QUESTIONS (next docs, not this build)
================================================================================
  Q1  WSD operator mechanics: exact scoring for context-coherence over the synset graph
      (Lesk-like overlap vs eigenmap proximity vs walk-based); where it runs on the read
      side (a native wsd() alongside recall) vs only as synthesized L-wsd layers.
  Q2  Role-binding attention shape: FE/theta binding needs position-aware argument
      attachment — interaction with RoPE/S-stratum unresolved.
  Q3  Rival-edge cross-refutation: (s,r,o1) vs (s,r,o2) with o1 IS_ANTONYM_OF o2 /
      EXCLUDES — a calculated operator discounting rivals by relative tension
      (round-vs-flat-earth case). Oppositional band exists; the operator doesn't.
  Q4  Prose claim-extraction lane (the Cyc gap): raw text records as sequence+containment
      only; extracting (s,r,o) claims from prose = a versioned CALCULATED analysis pass
      (doc 08 slot) or LLM-as-decomposer witnesses (AiModelProbe-class trust). The
      epistemology is ready (record assertions at source trust; the fold decides); the
      lane doesn't exist.
  Q5  Temporal/contextual validity: context_id is the natural slot for valid-time and
      situation scoping ("capital of France in 1400"); no semantics built on it.
  Q6  Echo-loop guard: the generation corpus folds the engine's own outputs by COUNT
      (walk_continuations reads corpus counts; trust classes never reach the n-gram
      lane). Wire trust weighting into corpus fold or filter corpus_document_source —
      the self-improvement loop and self-contamination loop are currently the same loop.
================================================================================
