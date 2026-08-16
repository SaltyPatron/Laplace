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

## 6. Sites, enumerated — CORRECTED 2026-08-16 by reading every emitter

The first revision of this table listed five set-valued sites. **Three of them were wrong**,
asserted from a relation name without opening the emitter. The corrected finding is that the
defect is far narrower than the row count suggests, and concentrated in one witness.

**Genuinely set-valued — the whole of it:**

| relation | rows | site | state |
|---|---|---|---|
| `HAS_FEATURE` | 186,562,442 | `Wiktionary/WiktionaryEmit.cs` `WalkForms` | rewired 292af9ae |
| `TRANSCRIBES_AS` | 5,192,208 | `Wiktionary/WiktionaryEmit.cs` `WalkSounds` | rewired 292af9ae (was truncated to 1 by `break`) |
| `HAS_USAGE_REGISTER` | 495,117 | `Wiktionary/WiktionaryEmit.cs` `WalkSense` | rewired |

**Asserted as set-valued in revision 1 and WRONG — do not convert:**

- `PropBank/PropBankDecomposer.cs:137` — `HAS_FEATURE` from `role.GetAttribute("f")`. One
  attribute per role. Single-valued, already correct.
- `UD/UdSentenceEmitter.cs:100` — FEATS emits a **different relation type per feature**
  (`RelationTypeRegistry.ResolveFeature` → `FEAT_Case`, `FEAT_Number`, …) against a
  `Name=Value` entity. That is a record with named fields, which §1 shape 3 already calls
  correct, and it is the *better* shape than a bundle: each field is independently
  adjudicable and independently queryable. UD was right before this spec existed.
- `ConceptNet/ConceptNetSource.cs`, `Atomic2020/Atomic2020Source.cs` — `HAS_PROPERTY` comes
  from source rows that are already triples, one relation per row. There is no bundle in the
  input to preserve.
- `WordNet/WordNetDecomposer.cs:267,274` — multiple `HAS_DEFINITION` / `HAS_EXAMPLE` per
  synset are independent claims, each separately corroborable. Multi-valued is not
  set-valued.

**Single-valued and correct as edges:** `HAS_LINE_BREAK` (356,930), `HAS_EAST_ASIAN_WIDTH`
(355,548), `HAS_BLOCK` (303,808), `HAS_AGE` (299,448), `HAS_SCRIPT` (159,866), all from
`Unicode/UnicodeDecomposer.cs`. A codepoint has exactly one block.

**The test that separates the three cases**, since the relation name does not:

1. Would a second witness corroborate or refute the members *as a whole*? → set → bundle.
2. Does each member answer a differently-named question? → record → one typed edge per field
   (UD FEATS).
3. Is each member an independent claim about the subject? → multi-valued → one edge each
   (WordNet glosses).

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

---

## 9. What is NOT done — the backlog, enumerated 2026-08-16

No task-list tool exists in this session (searched: EnterPlanMode, ExitPlanMode, CronList,
TaskOutput, TaskStop, DesignSync, EndConversation, EnterWorktree — none is a checklist), so
the backlog lives here, where it outlives the session.

**Blocking everything below:** `laplace` was dropped. `psql -l` returns `postgres`,
`template0`, `template1`. Every measurement in this document was taken before that and none
can currently be re-run. Treat every live number here as **UNVERIFIED** until a rebuild
reproduces it.

**Written, never executed:**

1. No set composition has ever reached a database. `StageCollection` is exercised only by
   in-process tests (`StageCollectionTests` 8/8, `WiktionaryFeatureSetTests` 5/5).
2. The eviction that would release the rows into the new shape never ran:
   `evict WiktionaryDecomposer --relations HAS_FEATURE,TRANSCRIBES_AS,HAS_USAGE_REGISTER
   --rederive`. Blocked at the tool layer, then mooted by the drop.

**Defects introduced by this change and NOT validated:**

3. ~~`StageCollection` throws and would kill the bulk ingest.~~ **Downgraded 2026-08-16,
   verified by search, not asserted.** `grep -rn "StageCollection(" app/` returns exactly one
   production caller — `WiktionaryEmit.cs:303` — and it uses the explicit-coordinates
   overload, which never reaches the builder lookup. The throwing overload has **zero
   production callers**, so it cannot fire from any shipping path. It is covered by
   `StageCollectionTests.MemberWithNoStagedPhysicalityThrowsRatherThanForgingACoordinate`.
   The remaining risk is a future lane choosing the lookup overload for members the existence
   bitmap suppressed; the contract is in the XML doc and the throw names the fix.
4. `TryStageSet` silently DROPS a tag whose tier tree carries no composed geometry
   (`TryRootCoord` false). It fails quiet, not loud, and nothing measures how often. A
   dropped member changes the set id, so this can silently produce two ids for one analysis.

**Unwritten:**

5. `docs/INDEX.md` and `docs/INVENTORY.md` carry no entry for `consensus.belief_distribution`,
   `consensus.glicko2_logit`, `consensus.glicko2_g`, `consensus.set_members`,
   `consensus.subjects_with`, `physicalities_set_constituents_gin`, or `PhysicalityType.Set`.
6. CLAUDE.md §9 has no measured entry for any of those surfaces.
7. 252 issues open. None triaged, filed, or closed against this work.

**Open questions this document does not answer:**

8. A bundle carries one rating for the whole analysis. What happens to the per-member
   testimony that the edge fan used to carry — is `{nom, sg}` from one witness and
   `{nom, sg, masc}` from another a corroboration or two distinct claims? The merkle says
   distinct. Nothing here decides whether that is right.
9. `consensus.subjects_with` has never run against data. Its GIN plan is asserted from the
   index definition, not measured.
