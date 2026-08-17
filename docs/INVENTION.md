# The Invention

Laplace is one invention. This document states its premise and derives the whole system
from it.

This document records the intended invention, not transient implementation status. A
disagreement between it and the running system is an unresolved design or implementation
gap; neither side silently rewrites the other.

---

## 1. The premise

**Identity is content, and everything else is testimony about identities.**

One idea in two clauses, because the second only works if the first is true. Every
mechanism in the system is forced by it, and the rest of this document is the derivation.

The consequence stated as a thesis: **training was only ever a bad database.** Gradient
descent and ingestion are two implementations of one operation — accumulating a corpus's
structure into relational state. Backprop does it lossily, anonymously, and then freezes
the result. Ingestion does it explicitly, attributably, and never stops.

---

## 2. The derivation

If a thing's identity is computed from its content, then two witnesses referring to the
same thing are referring to the same object — automatically, from any source, in any
modality, forever. Cross-source merging is a hash collision, not an entity-resolution
pass. That single property is what makes pooling free.

Once pooling is free, there is no reason to keep a dictionary, a treebank, a chess game, a
source repository, and a neural checkpoint in separate systems. They go in one space.

But pooled testimony contradicts itself. So the system cannot store *truth* — it can only
store *standing*. Every claim holds a tournament rating, a deviation, and a volatility,
accumulated over every witness that ever spoke to it.

Standing is meaningless without knowing whose it is, so provenance is inalienable. Evidence
rows are permanent and provenance-only. Disagreement is recorded rather than resolved, and
dissent stays visible after it loses.

Once every claim is rated by the evidence for it, the only way the system can change is
more evidence. That removes the training run — not as an optimization, but because nothing
is left for it to do.

If the state is genuinely a function of the evidence, it must be deterministic, or it is
not a function. Hence fixed-point integer arithmetic, one compiled kernel per mathematical
truth, and byte-identical output across compilers and operating systems. This is the
premise refusing to be violated, not fastidiousness.

Because the state *is* the accumulated rated testimony, a model is not a separate artifact.
It is a view of that state, read off rather than fit. Inference is not multiplication
either — it is traversal of the same state. A prompt is simply more content, so there is no
context window; context is biography.

The system's own output is content too. It deposits as testimony, feedback attests to the
exact triples that produced it, and the loop closes. Its own voice is structurally
low-trust, or the loop confirms itself into noise.

Because ignorance is now a number rather than an absence, the system can rank what it is
least sure of and point at where evidence stops.

And any modality that can be segmented into content enters the same machinery unchanged.

---

## 3. Identity

**Entity.** The unit of knowledge: a row whose id is the BLAKE3-128 of its canonical
content bytes. The same content is the same entity everywhere, forever, regardless of which
witness mentioned it.

**Composition is Merkle.** Leaves hash their canonical bytes; composites hash a fixed
domain byte followed by their ordered children's ids. Decomposition and reconstruction are
exact operations, not statistical ones — a document rebuilds byte-for-byte from its id
alone.

**A file's metadata is a branch of its trunk, not a side channel.** The file node
composes (at least) a content subtree and a metadata subtree: EXIF tags, OPF blocks,
format headers, ID3 frames — parsed by format-native grammar into content entities like
everything else, so the ten-thousandth file stamped with the same license string collides
into one entity and pooling stays free. Typed edges (title, author, license) are then
attestations derived from entities the DAG already holds — provenance is decomposed from
bytes the file actually carries, never invented. Because the trunk hash covers headers
and content together, the round-trip law recovers the original file byte-exact, not just
its prose.

**Tier is altitude, not category.** Compositional depth is a separate column and is never
an input to the hash, so identical content is one id no matter what tier it was reached at
or which source produced it. Single-child compositions collapse to the child's own id.
Every level exists simultaneously and is first-class: characters *and* words *and*
sentences, each addressable, each carrying its own relations.

This is the deepest break with the token paradigm. A transformer's vocabulary is a flat
field of arbitrary shards with no containment structure — it cannot know that "dog"
contains "d," only correlate them at quadratic cost. Laplace entities have altitude.
Knowledge attaches at the tier where it is true, traversal moves vertically as freely as
horizontally, and repetition deduplicates into evidence instead of consuming capacity.

**Tiers are per-modality and grammar-fed.** Tier is whatever depth the modality's
segmentation grammar composes to — UAX#29 is only the *text* grammar. Code composes through
its tree-sitter grammar (token → expression → statement → block → function → module); other
modalities compose through their own. Within-unit nesting produces tiers; between-unit
relations produce attestations.

**Ids are never constructed outside the system.** They resolve through the native hash
functions. A hand-minted id is not an id.

---

## 4. Testimony

A **witness** is anything that asserts relations: a standards body's data file, a curated
lexicon, a treebank, a corpus, a single document, a user's prompt, a deposed neural
network. All enter through identical machinery and differ only in **trust class**, which
determines the force of their testimony in adjudication.

The trust ladder descends roughly: substrate mandate → standards-derived (Unicode, ISO) →
academically curated (WordNet, UD, VerbNet, PropBank, FrameNet, SemLink) → academically
curated with user input (OMW, ConceptNet) → structured corpus (Tatoeba, Wiktionary,
OpenSubtitles) → user-curated → user prompt → app-derived → AI model probe → adversarial.

**Unattested text is prompt-grade by design.** A bare text file carries exactly a
prompt's epistemic standing: words with no authority behind them beyond whoever supplied
them, priced accordingly by the ladder. No correction is owed for corpus text riding the
user-prompt trust position — re-attributing it to an invented "documents" authority would
manufacture provenance, the exact thing the evidence law forbids. Text earns a stronger
position only through what its own bytes prove (headers, licenses, authorship — the
metadata branch of its trunk), never through which pipeline it entered.

The deliberate inversion against the industry: **a transformer's testimony is admissible
and outranked by the dictionary.** Models are witnesses to be cross-examined, never
oracles.

**The reduction that makes a model depositable.** All model math reduces to token→token
relations. Every operation in the stack is either a direct comparison or a set-against-set
comparison; norms, softmax, activations and residuals are calibration and aggregation of
those comparisons, and they fold away. So a checkpoint deposits as a small number of
token→token relation types — embedding similarity, attention, value-output relation,
completion — plus tokenizer testimony and recipe scalars. Intermediate spaces (neuron,
attention dimension, key-value dimension, layer, head) contract inside the native kernels
and never surface as entities or relation types.

Layer is witness provenance, never identity: it rides the context, so the same token pair
folds across layers and cross-layer agreement becomes the strongest testimony. Putting a
layer, head, or dimension index into a relation type or an entity id is the condemned
weight-archive pattern — a float codec wearing attestation costume, structurally unable to
fold across witnesses.

Record counts are therefore **claim-shaped, never parameter-shaped**. A larger model
testifies more reliably about the same claim space; it does not get more rows. And the
reduction is modality-independent — tokens for a language model happen to be text.

**The evidence law.** An attestation records *that* a witnessing happened — subject,
relation type, object, source, context, an outcome class, a count, a timestamp. It never
records a magnitude. A stored per-witness score is mathematically invertible back to the
raw weight; that is value-channel smuggling wearing provenance as a costume, and it is
banned. The witness's magnitude is consumed into adjudication at ingest and not persisted.

The outcome domain is three-valued and signed — refute, draw, confirm — so a source can
*deny* a triple rather than merely omit it.

Attestation identity is the content-addressed 5-tuple, so re-observation is idempotent.
Context refines the witness without entering relation identity: the containing document for
text, the layer for a model witness.

---

## 5. Adjudication

Truth from many witnesses is structurally a tournament: repeated paired comparisons under
uncertainty, with confidence that tightens under consistent results and loosens under
volatility. Glicko-2 provides exactly strength, uncertainty, and surprise-tracking, with
principled updates — so it is used as the truth engine rather than as an analogy to one.

Consensus is one row per (subject, relation type, object), carrying rating, deviation,
volatility, and witness count, accumulated over every witness. **Source and context are
excluded from consensus identity** — witnesses affect the state, never the identity. That
is what makes "what does everyone think" a single indexed lookup.

A witness observation is a game against the neutral line, scored continuously from the
witness's magnitude; the witness's trust enters as the *opponent's deviation*. Trust is an
argument to the rating math, not a filter applied around it.

Reading the state:

- **Belief** is `eff_mu = rating − 2·rd`, the conservative bound. One definition,
  planner-inlined, mirrored exactly by the ranking indexes. All ranked reads order by it.
- **Uncertainty** is `rd`. `ORDER BY rd DESC` is the introspection query weight-based
  models cannot express.
- **Refuted** is `rating + 2·rd < neutral` — the *optimistic* bound below baseline: lost
  even at its best. Refuted edges are pruned from traversal but stay visible to ranked
  reads.
- **Witness count** is corroboration breadth, distinct from strength.

**Sign comes from the rating; magnitude from deviation and witness count.** The verdict
belongs to the rating alone. Deviation is a confidence interval, not a verdict, and applies
as decay. Signing on the conservative bound double-counts uncertainty and collapses
"uncertain" into "refuted" — a wide-deviation win must rank low while staying walkable,
while a genuine refutation goes negative and dead-ends.

There is no backfill or rebuild path. Consensus accumulates at ingest, inside the write.

---

## 6. Ingestion is training

| conventional | substrate |
|---|---|
| training run | `ingest <source>` — deterministic, resumable, minutes-scale |
| epoch | re-ingest is an idempotent no-op; overfitting is not expressible |
| curriculum | ladder order, enforced by layer gates |
| learning rate | trust class |
| checkpoint | the database, reproducible from sources |
| fine-tuning | more witnesses |
| catastrophic forgetting | impossible — old testimony is outvoted, never overwritten |
| knowledge cutoff | last observation, per source |
| alignment | trust-class policy |
| unlearning | adjudication: refuting testimony outvotes the claim |
| train/serve fleets | one database, concurrent under MVCC |

Learning is concurrent with serving: the system gets smarter while it answers.

**The ladder is a signal-dependency stack, not a preference order.** Unicode and ISO-639
are the floor. Documents come next — raw distributional trajectories that demonstrate
answers from text alone. Then the knowledge layer as one uniform sequence, WordNet first
because the rest bind to its interlingua-anchored senses. Then usage. Then code as the
capstone. Models last, because deposition presupposes export. No source is special-cased;
each layer enriches the prior.

**Why these sources.** They are the explicit form of everything a transformer must infer
implicitly: Unicode for the atoms, ISO-639 for the languages, WordNet and the interlingua
for the sense inventory and cross-lingual anchor, OMW for the same concepts in every
language, FrameNet / VerbNet / PropBank / SemLink for predicate-argument structure,
Universal Dependencies for syntax, ConceptNet and Atomic for commonsense, Tatoeba and
OpenSubtitles for usage, code and repositories for procedure. Linguistics spent forty years
building that, curated and attributed. Training discards all of it to re-derive a lossy
anonymous copy from scraped text.

Every source is public and licensed, which is why the substrate records license and
attribution per source. Unlearning is not removal at all: a false claim that
gets ingested sits as one weak witness, earns no confirmation, and is refuted
into negative standing by stronger testimony when it matters — pruned from
traversal, still visible on the record. Correction is more and better
evidence, or a direct edit of the offending record; deletion is never the
learning story. Source removal survives only as a compliance-grade
administrative hatch (a licensing takedown), not an epistemic operation.

**The document round-trip** is the storage/learning duality in one command: decompose to
entities and trajectories, attest the sequence, fold consensus, then reconstruct the
document *from the database* and byte-compare. The store is simultaneously a perfect
archive and a semantic decomposition. Training-data extraction inverts from an attack into
a feature with a warranty.

---

## 7. Inference is traversal

Queries are indexed descents and compiled path searches over relation arenas, edges
weighted by adjudicated strength, refuted edges pruned. Cost is bounded by the path through
relevant relations, not by corpus size. No GPU exists anywhere in the query path.

The walk is the forward pass. Where a transformer walks a continuation through trained
attention, this engine walks it through indexed lookups — and each step has more available
than a trained dot product: the full rating tuple, relation salience, highway bits,
geometry, trajectory ordinals, locality, source trust, and provenance down to individual
witnesses.

**Realization** renders entity paths into language. Language is a render-time choice, not a
property of knowledge: testimony in any language strengthens consensus readable in every
language.

**There is no context window.** A prompt is ingested content, so attention over it is
unbounded retrieval and recall from last month costs the same indexed descent as recall
from ten seconds ago.

**Explainability is columns.** Ratings and witnesses return with the answer. "No gloss
witnessed yet" is a structural answer, not a failure.

**One election law:** no ranking may be decided on a single-token scalar. The discriminating
information lives in the graph *between* a prompt's tokens — joint topic, sense, and
relation elected together — which is why a per-token score answers "what is a pawn in
chess" with a fact about the letter A.

---

## 8. The dual engine

Two exact, indexed, orthogonal similarity systems over the same identities:

- **Relational** — what testimony binds: order by belief over witnessed arenas.
- **Structural** — what form resembles: position and curve mathematics.

The canonical measurement: *whale* and *while* are structurally near with zero relational
testimony; *whale* and *ship* are relationally bound across many witnesses and structurally
far. A single embedding similarity cannot represent both facts at once. This is the
architecture's standing rebuke of cosine-as-meaning.

Their disagreement is generative. Structurally near plus relationally silent is a
**hypothesis candidate** — a frayed edge, the system's own reading list. Relationally
strong plus structurally far marks *learned* association as opposed to formal kinship.

**Ignorance is first-class.** Absence is provable by closed-world count over attestations —
the system can prove it was never told something, which is impossible in principle for a
weight-based model. Uncertainty is a number. Gaps are witnessable objects.

---

## 9. Geometry

Geometry is an identity, ordering, and serialization system, and the lens for comparison.
It is instrument-tier by ruling: truth lives in the relational engine. Point proximity is
not the relatedness signal.

**The frame is anchored, not learned.** Atoms are placed by deterministic law on the unit
3-sphere and never move; composed entities derive position from their constituents.

**Two-channel identity**, discovered rather than designed: an entity's *position* encodes
its constituent multiset while its *curve* encodes constituent order. Position is
composition; curve is sequence. Anagram retrieval therefore collapses to a B-tree equality
on the locality key.

The mechanism is arithmetic, not magic: above tier 0 a coordinate is the centroid of its
constituents, and a mean is commutative, so any two entities built from the same multiset
in any order are *guaranteed* the same point and the same locality key.

**Hence the exactness law.** Content-hash collision is the system's one strong identity
invariant: identical id means identical canonical content. Coordinate and locality-key
collision are *not* identity. Above tier 0 they certify only "same constituent multiset,
some order" — never the same entity, never a similar shape. They are a candidate filter for
a subsequent order-sensitive check on the trajectory, and using one as a final admission
criterion is a defect with a name and a history in this tree.

Tier 0 is the exception, and for the opposite reason: atoms are placed by fixed law rather
than averaged, so a tier-0 coordinate collision between distinct atoms would be a seeding
bug. The same bit pattern means two structurally different things depending on tier, so any
code reasoning about a collision must branch on tier first.

A further trap follows from the packing: coordinate and trajectory columns double as the
mantissa payload channel, disambiguated only by a flag bit. A geometric function called on a
packed row returns a numerically valid, semantically meaningless answer with no error to
catch it.

**The trajectory law.** Stored trajectories carry constituent *identity*, mantissa-packed,
never coordinates. Positions move by design as witnesses re-adjudicate; identity is the only
placement-proof cargo. Realized curves are built on demand from live coordinates, so curve
math always measures current geometry.

**One pipeline, both directions.** Laplacian eigenmaps → Gram-Schmidt → Procrustes runs
inward at deposition, reducing a witness's native embedding space and aligning it onto the
shared frame; and outward at export, generating a brand-new basis from the consensus graph
at the mold's dimension. The generated spaces owe nothing to any witness's geometry.

The alignment is well-posed **because of the premise**: content addressing supplies exact
point correspondences — the model's "king" *is* the substrate's king — so the correspondence
problem that cripples manifold alignment elsewhere is solved by construction.

**Fireflies.** Each witness's placement of an entity is a distinct specimen in one frame:
species of the same identity, never a blend. The species are the product; collapsing them
destroys the comparative signal that *is* the instrument. This supports per-entity
cross-model belief distance, whole-cloud lineage and distillation forensics, checkpoint
drift diffs, bias measurement in defensible geodesics, and Voronoi tessellation into
conceptual territories where boundary proximity is ambiguity and empty cells are visible
lexical gaps. Because placements are stock geometry, standard GIS tooling renders it.

---

## 10. Synthesis: the model as a render target

Conventional flow: corpus → months of gradient descent → frozen weights → opaque inference.
Laplace flow: witnesses → adjudicated consensus, minutes and attributed → pour into a mold
→ an artifact any standard runtime executes, with every weight traceable to testimony.

A model is something you **cast**, not train. The artifact is rebuildable on demand,
diffable build-to-build with deltas naming their witnesses, exportable at any dimension, and
disposable. The substrate stays alive; the file is a cache for the existing ecosystem, which
never learns that nothing was trained.

The mold is an architecture recipe — user-authored, or discovered from any deposed model, so
the same mold can be poured again with better data. The basis is generated from the
consensus graph, operators are projected through it, factored at the mold's ranks, and
written closed-form. No witness's floats are ever a referent; export renders consensus, it
does not invert an ingest. Any mold tensor the foundry does not define is a hard error,
never a zero-fill and never a copy from a witness.

Artifact classes: a same-mold recast of a deposed model, where the only variable is the
data; a **clean-room model** cast from enumerated licensed witnesses with zero model
ancestry; a no-ancestor compile from literature alone; and resized casts at any dimension —
distillation with no teacher-student loop and no license entanglement.

Validation is behavioral, not bitwise: the cast artifact must load and run as a normal,
non-faulty model on CPU with no degradation against a structurally comparable one. Numeric
agreement with any ingested witness is not a metric.

**The moat is provenance.** Fitting a transformer to imitate the native engine would work
and would forfeit the entire claim, because gradient-learned weights carry none. Conditional
structure has to be *compiled* — constructed directly, training-free — or it is not this
invention.

---

## 11. The loop

The engine's own outputs are first-class inputs. A response is content-addressed and
deposited as a witness; feedback binds to the *triples* that produced it, not to response
text, and folds into the same consensus the next walk reads. Evaluation is ingestion.

Observe: prompt and response witnessed into the substrate. Orient: intent routing. Decide:
walk over consensus. Act: respond, and deposit the response. Feedback: confirm or refute,
fold, and the next walk differs.

Self-signals are outranked by design. The engine's own voice sits low on the trust ladder,
one witness among many, so self-confirmation cannot outshout curated sources. This is what
makes a self-consuming loop safe: provenance survives, and the system's own output can never
win an argument against the dictionary.

---

## 12. What this eliminates

- **The GPU at inference.** Query cost is path-bounded, not parameter-bounded.
- **The training run.** Deterministic, resumable, idempotent ingestion replaces it.
- **The knowledge cutoff and the static model.** The artifact is a render; the substrate is live.
- **Catastrophic forgetting.** Old testimony is outvoted, never overwritten.
- **The context window.** Attested history has no edge.
- **The black box.** Every answer decomposes into named witnesses, ratings, and paths — and
  black boxes can be held up to the light: audit, comparison, multi-model consensus,
  certifiable clean-room export.
- **The provenance void.** Every source enumerated, licensed, attributed — and every
  claim correctable by evidence: refutation outvotes, it never erases.

Capabilities that are unique in principle rather than in degree: proving a negative;
runtime learning that is attributed and timestamped; naming the teachers of a specific
claim; per-claim confidence with visible dissent; exact reconstruction of ingested content;
outvoting a lie without deleting the record of it; and identical answers across toolchains.

---

## 13. What is claimed and what is the bet

The claim is not that this is a better language model. The claim is that the data center,
the training run, the cluster-months, and the opacity that comes with them were contingent
choices rather than physics — and that the correct construction is small enough for one
person to build.

The honest frontier, tracked rather than hidden: open-ended generative fluency at parity
with trained models; the quality of cast artifacts at scale, measured behaviorally; the
positional and conditional structure that a marginals-only compiler cannot express; and the
unwritten modality annexes. The system's own epistemology applies to itself — claims about
it earn standing through witnesses, and the reproduction scripts are the deposition kit.

---

## 14. The annexes

Text works because Unicode spent thirty years writing its segmentation law: deterministic,
versioned, conformance-tested. No other modality ever received one, because the tensor
paradigm never needed identity.

The expansion plan is exactly to author that law per modality — pixel to region to object,
sample to frame to event — versioned and conformance-tested and compiled, after which
identity, attestation, adjudication, geometry, realization, and export apply **unchanged**.

The codepoint never knew it was text.

---

## 15. The implementation law

The premise constrains the code as tightly as it constrains the schema.

**One compiled kernel per mathematical truth.** A second body expressing the same
quantity is not redundancy, it is a future divergence — the two agree until a one-line edit,
and nothing tests that they still do. Where a formula is needed in two languages, one owns
it and the other calls it.

**Orchestrate versus compute.** The native libraries hold deterministic math, parsing
kernels, and bulk builders. The extension is the versioned surface: thin wrappers, set
logic, and search-provider callbacks. The application layer orchestrates, marshals, batches,
and does I/O — and never inlines math that a kernel owns.

**Policy lives in native code, generated from a manifest.** Relation resolution, alias
flips, symmetry orientation, family membership, tag normalization, and score assignment are
compiled from one manifest rather than hand-maintained per witness. A witness adapter parses
its source and hands surfaces to the engine; it does not decide what a relation means.

**Parsing is witness extraction, not domain modelling.** Structured corpora compose through
one grammar path; free text composes through the segmentation law. Shortcuts that skip
constituent composition produce content ids with no constituents behind them.

**Determinism is architectural.** No fast math, floating-point contraction off, fixed-point
where the fold lives, integer-pure hashing and locality keys, byte-compared build artifacts,
and regression outputs treated as byte contracts. Same input, byte-identical output, across
compilers and operating systems — because the state has to be a function of the evidence.

The recurring failure this guards against is specific: an operation gets a canonical
implementation, the caller that should use it is never rewired, both survive, and they
drift.

## 16. Structure of the work

**Core** — what the claims rest on: identity, testimony, adjudication,
ingestion-as-training, traversal inference, realization, synthesis.

**Instruments** — high value, not load-bearing for truth: per-witness placement and the
audit surface it supports, the uncertainty frontier, behavioral harnesses, metering.

**Annexes** — expansions that reuse the core unchanged: per-modality segmentation laws,
additional witnesses, additional molds.
