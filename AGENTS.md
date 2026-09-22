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

Identity is canonical content. Equal content under one recipe is one BLAKE3-128 entity. Composition identity is a Merkle hash over the ordered child ids. Tier, source, modality, role, coordinate, and standing are not mixed into that hash. A one-child composition is the child. Tier is altitude in a modality grammar, not a second identity.

A physicality is a typed realization of an entity: coordinate, Hilbert locality, trajectory, and the other declared physical forms. A trajectory says which constituents, in what order and roles, make the structure. The GeometryZM carrier packs that manifest into four binary64 values, 212 reversible bits: the 128-bit child id, a packed ordinal, a run length, and flags. Those packed values are not the child's position. A realized curve unpacks the child ids and reads each child's coordinate in ordinal order. Contains, precedes, and co-occurrence are facts of that trajectory.

Children placed in the closed unit 4-ball stay in that ball under the native Euclidean centroid, and so do their parents and the segments of the realized curve. The address law for Tier-0 is open. Unicode's finite generation is one admitted window, not the capacity of the tier. Finite scalars are compositions of existing atoms (`255` is `2,5,5`; `0.34567` is `0 . 3 4 5 6 7`), reused wherever the same number occurs. Machine widths are windows, not limits on the invention.

The same entity sits in composition, trajectory, occurrence, attestation, consensus, geometry, and source context at once. An attestation is attributable testimony. Consensus is the folded standing of one typed triple: rating, deviation, volatility, witness count. Confirmation, draw, and refutation are distinct. Absence is not false. A recorded observation and a versioned calculation are different witnesses. Coordination of a point is not identity.

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
