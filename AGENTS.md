# Laplace

Laplace is one deterministic, content-addressed, recursively compositional knowledge and cognition substrate. It is the machine that replaces opaque learned-tensor authority for every operation those tensors are asked to perform: language, code, games, images, audio, models, and the rest of finite digital structure. The OpenAI-compatible endpoint, MCP, SQL, CLI, and web surface are that machine's interfaces. They are not adapters around a hidden model, and they are not a separate product from the substrate.

This file says how that machine is implemented here. The invention itself is `docs/INVENTION.md`, `docs/INVENTIONS.md`, `docs/CAPABILITIES.md`, and the binding specs. Code implements those documents. A comment, issue, plan, audit, or commit message does not define a smaller machine.

## Authority

1. The inventor's current instructions.
2. `docs/INVENTION.md` and `docs/INVENTIONS.md`.
3. `docs/CAPABILITIES.md`, for what those laws compose into.
4. Binding specifications:
   - `docs/specs/05_Substrate_Invariants.txt`
   - `docs/specs/06_Engineering_Ruleset.txt`
   - `docs/specs/08_Record_vs_Calculate_Spec.txt`
   - `docs/specs/09_Substrate_LM_Synthesis.txt`
   - `docs/specs/11_Chess_Provenance_Consensus_Spec.txt`
   - `docs/specs/33_Perfcache_Blob_Law.md`
   - `docs/specs/34_Conversational_Provenance.md`
   - `docs/specs/36_Laplace_Forward_Pass.md`
   - `docs/specs/37_Substrate_Operation_ISA.md`

When a lower document disagrees with a higher one, correct the lower document and implement the higher one.

`SaltyPatron/Laplace-Refactor` is a separate repository. It does not own this invention, this host, or this deployment.

## The machine

Identity is content. Equal content is one BLAKE3-128 entity wherever it occurs. A codepoint's id is BLAKE3 of its UTF-8 bytes from the Tier-0 ROM; a composition's id is BLAKE3 over its children's 16-byte ids in order, repeats included, truncated to 16 bytes. Tier, source, modality, role, position, recipe, version, coordinate, and standing are not mixed into that hash, and there is no domain byte. A recipe decides how content decomposes into a tree; it never salts the hash. One Merkle DAG runs from codepoint to the source trunk: a file is `[metadata, content]`, a source is `[source record, its files' trunks in path order]`, and any client computes any id from the ROM with no database. A string made up to name something is never hashed into an id; it is decomposed content, a referenced trunk, or a recorded attestation. A source's highway nodes (an ILI, a PropBank roleset, a VerbNet class, a FrameNet frame, a language code) are content, one entity in every source that cites them; its internal pointers (a WordNet offset, a synset or sense id, a token number or `sent_id`, a row or line number) resolve to the id of what they point at and are not recorded, and the facts their notation carries (a part of speech, a lexicographer file) are attested. A one-child composition is the child. Tier is altitude in a modality grammar, not a second identity.

A physicality is a typed realization of an entity: coordinate, Hilbert locality, trajectory, and the other declared physical forms. A trajectory says which constituents, in what order and roles, make the structure. The GeometryZM carrier packs that manifest into four binary64 values, 212 reversible bits: the 128-bit child id, a packed ordinal, a run length, and flags. Those packed values are not the child's position. A realized curve unpacks the child ids and reads each child's coordinate in ordinal order. Contains, precedes, and co-occurrence are facts of that trajectory.

Children placed in the closed unit 4-ball stay in that ball under the native Euclidean centroid, and so do their parents and the segments of the realized curve. The address law for Tier-0 is open. Unicode's finite generation is one admitted window, not the capacity of the tier. Finite scalars are compositions of existing atoms (`255` is `2,5,5`; `0.34567` is `0 . 3 4 5 6 7`), reused wherever the same number occurs. Machine widths are windows, not limits on the invention.

The same entity sits in composition, trajectory, occurrence, attestation, consensus, geometry, and source context at once. An attestation is attributable testimony: a witness asserting a claim, with its outcome, games, score, qualifiers, and context. The witness is the source trunk, `[source record, its files' trunks in path order]`: a corpus is one trunk and one witness, its treebanks, lexicons and datasets are files under it, and the annotators, workers, speakers and members it names are content whose relations the corpus attests. The current schema keeps a per-(claim, witness) attestation row, with games, score, position and qualifier mask, whose witness is `SourceWitness.Id(authority, release)`; that witness identity violates the identity law. Provenance by containment, a source's record a path over the claims it asserts inside its file's content tree, many trunks containing one claim and a walk up finding them all, is the target, and the attestation row stays until a working prototype on real data shows containment answering everything the row answers today with nothing lost. A claim is an n-ary composition of content ids with referential integrity, of any arity and tier; `(subject, relation, object)` is one shape among many. Consensus is the folded standing of one claim composition: rating, deviation, volatility, witness count, stored for every claim in one table keyed by the claim, beside the path and never on it. Repetition is run length read off the tree (a run in a vertex's M, a lengthening session trajectory, a claim in k records under one trunk): a witness plays one matchup per claim per ingestion, with the run length carried as the certainty of its assertion, never as n Glicko-2 games, and the client folds it so the database takes one update per claim per witness; the run count is kept beside the claim; packaging (a cross-product layout, a tagger's per-token output) is not repetition; a number a source states is an observation. Confirmation, draw, and refutation are distinct. Absence is not false. A recorded observation and a versioned calculation are different witnesses. Coordination of a point is not identity.

A request is one admitted observation. The program over it is:

```text
RESOLVE → COUPLE → ORIENT → ROUTE → SCAN → COMPOSE
        → PROPOSE → STEER → SELECT → REALIZE → WITNESS
```

COUPLE is the typed response of every eligible plane to that observation. It is not one relevance score, and a caller mask does not substitute for it unless the caller asked for that constraint. ORIENT keeps a real ambiguity when the observation supports more than one reading. After that, expansion is a sparse indexed star. Hops and fanout are how far and how wide that star runs. A*, walks, geometry, chess search, and other domain procedures are operators inside the program.

Perfcaches are a lattice of derived read-only maps over structure that is already canonical: a dense tier can be a direct-address ROM; a huge tier caches the admitted estate. Video reuses image and audio maps. A cache is not a second authority and not a smaller knowledge world.

Knowledge, authority, and compute are separate. The admitted world is one world. Grants say who may discover, couple, traverse, derive, realize, export, or execute which of it. Hops, fanout, and measured resources say how much work one operation may spend. Governance can refuse an operation. It does not delete the fact.

PostgreSQL stores and indexes that world. Native C and C++ run the repeated work: parse, compose, search, fold, encode. SPI moves sets between them. C# and SQL orchestrate sessions, contracts, and transport. They do not become the inner loop. A batch whose body calls a scalar operation per element is still one-at-a-time work.

Ingest enumerates the selected artifacts, recovers source structure with a provider, and admits it through the shared recipe: compose, converge, persist in bulk, fold in sets, receipt. Source code does not own a private identity or a private commit loop.

The product is the same web, navigable: browse, rank, entity, evidence, trajectory, neighborhood. Chess, code, and conversation specialize presentation and grammar. They do not specialize identity or cognition. Constructed code is grammar-constrained composition with reuse, not token generation. A repository root is one application object. Export of a model is a recipe over current standing and structure, not a copy of an ingested checkpoint.

## Operating it

Use the installed machine while building it. MCP (`laplace-substrate` on the host), the OpenAI-compatible endpoint, SQL, and the CLI call the same operations. A read through those surfaces is evidence of what the running process does. It does not revise the invention.

## Comments

A comment states a non-obvious fact about what the construct does in this machine: identity, physicality, trajectory, occurrence, testimony, consensus, calculation, an ISA operation, or execution grain. It does not report a campaign, a date, a reduced temporary behavior, or an instruction to skip work.

## Where the bytes go

Inspect the host before allocating builds, databases, or install copies. A path under `/opt/laplace` is not proof of which volume backs it.

| Purpose | Place |
| --- | --- |
| Installed runtime | `/opt/laplace` |
| PostgreSQL data | `/opt/laplace/pgdata` |
| PostgreSQL WAL | `/var/lib/pgwal` |
| Build and tool scratch | `/build` |
| PostgreSQL spill | `/pgtemp` |
| Admitted source estate | `/vault` |

Prepare a replacement on `/build`. Copy it into place only after the replacement is complete. Do not delete a serving file, a library symlink, or a PostgreSQL module a database still has loaded. Do not create a second PostgreSQL server for an ordinary repair. Do not use `/tmp` or the source-estate disk as build scratch. `TMPDIR` belongs under `/build`.

Implementation lands on `main`. One install is one revision: application, native prefix, PostgreSQL execution module, perfcache, and extension catalog identify the same build.

A test proves the claim it names. The capability is the running operation, not the test.
