# 38 — Collections are compositions, not edge fans

Design law for set-valued facts. Measured live against `laplace` 2026-08-15 unless a line
says otherwise. Companion to `08_Record_vs_Calculate_Spec.txt` (what may be derived) and
`36_Laplace_Forward_Pass.md` (where a distribution belongs in the program).

---

## 0. The measurement that forces this

```
SELECT count(*), count(DISTINCT subject_id) FROM laplace.consensus
WHERE type_id = laplace.relation_type_id('HAS_FEATURE');
```

| quantity | value |
|---|---|
| `HAS_FEATURE` consensus rows | **186,562,442** |
| distinct subjects (forms) | **62,066,099** |
| edges per subject | **3.01** |
| distinct objects (the tag vocabulary) | **1,610** |
| **distinct feature SETS** (`array_agg(object_id ORDER BY object_id)` per subject) | **145,619** |

The full-table `count(DISTINCT bundle)` ran in 363,269 ms. `HAS_FEATURE` is 84.93% of the
`consensus` DEFAULT partition and has no named partition of its own
(`ops.consensus_partition_pressure(100000)`, 93,820 ms).

`HAS_PROPERTY` is **24,891 rows** — small, same shape, ConceptNet and Atomic2020 only
(`ConceptNetSource.cs:32`, `Atomic2020Source.cs:24`).

`laplace.physicalities` holds **116,748,585 rows and every one is `type = 1`**. The
composition primitive this document is about exists, is indexed, and has exactly one lane
using it.

---

## 1. Three shapes, one emitter

An emitter that writes a fact about a subject is choosing between three shapes. Two are
settled law here and the third has never been implemented.

| shape | example | correct storage | status |
|---|---|---|---|
| **ordered sequence** | word order in a sentence | trajectory geometry, read with `laplace_trajectory_constituents` | settled — CLAUDE.md §4, six inference sites |
| **single-valued attribute** | `HAS_BLOCK`, `HAS_AGE`, `HAS_SCRIPT`, `HAS_EAST_ASIAN_WIDTH`, `HAS_LINE_BREAK` | one typed edge | correct as written |
| **set-valued attribute** | a form's morphological analysis `{nominative, singular, masculine}` | **a composition entity, one edge** | **unimplemented — 186M rows of the wrong shape** |

The defect is one emitter: shape 3's data written with shape 2's loop. `WiktionaryEmit.cs:238-240`

```csharp
if (form.Tags is { } tags)
    foreach (var tag in tags)
        if (Stage(b, tag, roots, out var tagId)) Attest(b, formId, "HAS_FEATURE", tagId, null);
```

Three tags, three attestations, and the analysis itself has no id.

---

## 2. Why the fan is wrong, beyond its size

1. **The set has no identity.** `{nom, sg, masc}` is one morphological analysis. As three
   edges it is three independent claims and there is nothing to point at. 145,619 real facts
   are re-derived 1,281× on average (186,562,442 / 145,619).
2. **Nothing can be attested about the set.** Glicko-2 adjudicates a subject–type–object
   triple. With no bundle entity there is no rating, no `witness_count`, and no refutation of
   *the analysis* — only of its members, which is a different claim. A second source that
   disagrees about the analysis as a whole has nowhere to put the disagreement.
3. **`context_id` is one slot.** `\d laplace.attestations` — `context_id bytea`, single. An
   emitter with a set of qualifiers must therefore drop all but one. Live instance,
   `WiktionaryEmit.cs:216-224`:

   ```csharp
   if (snd.Tags is { } tags)
       foreach (var tag in tags)
           if (Stage(b, tag, roots, out var dialectId)) { dialectCtx = dialectId; break; }
   Attest(b, wordId, "TRANSCRIBES_AS", ipaId, dialectCtx);
   ```

   The `break` is not laziness, it is the schema: a set does not fit in a bytea. That is
   **data loss** across 5,192,208 `TRANSCRIBES_AS` rows, not merely a shape complaint. A
   bundle id fits in the slot.
4. **The read is a self-join.** "forms that are nominative AND singular AND masculine" is a
   three-way self-join over a 186M-row relation whose `type_id` predicate prunes to one LIST
   partition and whose `subject_id` hash then fans across 8 — per join arm. As a composition
   it is one `@>` probe on `physicalities_constituents_gin`, the probe `containers_of.c:65`
   already runs.
5. **§15.** The canonical implementation exists and the callers were never rewired — the same
   failure the 32-of-33 decomposers bypassing `IngestComposePipeline` are.

---

## 3. What is already built

Nothing in §4 needs a new primitive. All four pieces are installed:

| piece | site |
|---|---|
| packed constituent geometry from ids | `Trajectory.Build(ReadOnlySpan<Hash128>)`, `app/Laplace.Core/Core/Trajectory.cs:5` |
| merkle id over children | `hash128_merkle`, content-derived, no float |
| **permutation-invariant placement** | `Math4d.KarcherMean`, `app/Laplace.Core/Core/Math4d.cs:16-22` — "all three internal accumulations run in a canonical order, so a placement is reproducible under constituent reordering" |
| membership probe | `physicalities_constituents_gin`, `IngestCommands.cs:799`; `laplace_trajectory_constituent_ids(trajectory) @> ARRAY[$1]` |

The Karcher-mean line is the load-bearing one. An unordered set lands at the same coordinate
regardless of member order, so a *set* is already a well-defined composition in the geometry —
the only thing missing is a writer that mints one.

**Canonical order for a set is ascending by member id.** That is what makes the merkle id of a
set well-defined, which is what makes it content-addressed, which is what collapses 186,562,442
writes into 145,619 distinct entities. Order is not a policy choice; it is the deduplication
mechanism.

---

## 4. The write API

One method on `SubstrateChangeBuilder` (`app/Laplace.Substrate/Crud/SubstrateChangeBuilder.cs`,
which already exposes `AddEntity`, `AddPhysicality`, `AddAttestation`):

```
AttestSet(subject, relation, IReadOnlyList<Hash128> members, source, trust, context = null)
```

1. sort `members` ascending by id, deduplicate — canonical, so the id is order-independent;
2. `bundleId = hash128_merkle(sorted)`;
3. `AddEntity(bundleId, EntityTypeRegistry.Collection)`;
4. `AddPhysicality(bundleId, trajectory: Trajectory.Build(sorted), n_constituents: sorted.Length,
   type: PhysicalityType.Set)`;
5. `AddAttestation(subject, relation, bundleId)` — **one** edge.

Steps 3–4 are idempotent by content address: the 1,281st form carrying `{nom, sg, masc}`
re-stages the same id and writes nothing new, exactly as text surfaces already dedupe.

**The physicality `type` discriminator is not optional.** `physicalities_constituents_gin`,
`physicalities_traj_first_id_btree` and `physicalities_traj_probe` are all partial on
`type = 1`. A set is not a text trajectory and must not silently widen those three indexes;
it gets its own type value and its own partial GIN. The `type` column is `smallint` and holds
one value today, so the discriminator is free.

**What this is not.** Not a CSV column, not a `text[]`, not JSON, not a bitmask. A collection
is an entity with a merkle id, a coordinate, and typed edges — the same as every other thing
in the substrate. A collection that cannot be attested about is the defect being fixed, so a
representation that cannot carry a rating is not a candidate.

---

## 5. The read API

Two functions, both on shapes that already have index support:

- `consensus.set_members(p_subject bytea, p_relation text)` — the bundle's constituents, in
  canonical order, via `laplace_trajectory_constituents`. Replaces the 3-row fan.
- `consensus.subjects_with(p_relation text, p_members bytea[])` — GIN containment on the
  bundle, then the reverse edge. Replaces the N-way self-join.

`consensus.salient_facts` (`salient_facts.sql.in:13-38`) currently carries a hand-written
fence for "dynamic `HAS_FEATURE` children"; with one edge per form that fence is deletable.

Reverse reads still pay the 216-leaf Append (CLAUDE.md §4 — `object_id` prunes at neither
level), so `subjects_with` resolves the bundle first and passes `type_id`.

---

## 6. Sites, enumerated

134 `AddAttestation` call sites across 22 files under `app/Laplace.Decomposers/`. The
set-valued ones, ranked by live row count:

| relation | rows | site | shape |
|---|---|---|---|
| `HAS_FEATURE` | 186,562,442 | `Wiktionary/WiktionaryEmit.cs:240` | set → bundle |
| `TRANSCRIBES_AS` | 5,192,208 | `Wiktionary/WiktionaryEmit.cs:216-224` | set truncated to 1 by `break` |
| `HAS_FEATURE` | (subset) | `PropBank/PropBankDecomposer.cs:137` | set → bundle |
| `HAS_USAGE_REGISTER` | 495,117 | `Wiktionary/WiktionaryEmit.cs` register tags | set → bundle |
| `HAS_PROPERTY` | 24,891 | `ConceptNet/ConceptNetSource.cs:32`, `Atomic2020/Atomic2020Source.cs:24` | set → bundle |

Single-valued and correct as edges — **do not convert**: `HAS_LINE_BREAK` (356,930),
`HAS_EAST_ASIAN_WIDTH` (355,548), `HAS_BLOCK` (303,808), `HAS_AGE` (299,448), `HAS_SCRIPT`
(159,866), all from `Unicode/UnicodeDecomposer.cs` (24 sites). A codepoint has exactly one
block. These are a record with named fields, not a collection, and collapsing them would
destroy the typed relation that names each field.

---

## 7. Cost of landing

| stage | state |
|---|---|
| primitive (`Trajectory`, `hash128_merkle`, Karcher mean, GIN) | **installed** |
| `AttestSet` writer | **not written** |
| `consensus.set_members` / `consensus.subjects_with` | **not written** |
| decomposer rewire (5 sites above) | **not written** |
| re-ingest | **186,562,442 rows** — the whole cost, and the reason this is a spec and not a patch |

Nothing here is fixable by a migration over the existing rows: the bundle entities do not
exist, and inventing them from `consensus` would mint entities with no source and no witness,
which `08_Record_vs_Calculate_Spec.txt` forbids without analyzer identity, version, inputs and
recipe. The bundles are written by the decomposer or they are calculated testimony; there is
no third option.

---

## 8. Related, landed 2026-08-15

`consensus.belief_distribution(subject, relation, k)` —
`extension/laplace_substrate/sql/functions/consensus/belief_distribution.sql.in`. The
normalised distribution over a subject's objects under one relation, from Glicko-2 log-odds
(`consensus.glicko2_logit`) rather than from a dot product. It is the probability counterpart
to `generation.adjudicated_row`'s exact ranking, and it returns the **empty set** when the
subject couples to nothing — the answer a softmax cannot give.

A collection changes what that distribution is *over*. With the edge fan, a distribution over
`HAS_FEATURE` is a distribution over 1,610 individually-attested tags, which is not a
morphological analysis. With bundles it is a distribution over 145,619 attested analyses,
which is.
