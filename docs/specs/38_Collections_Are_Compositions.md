# 38 — Collections are compositions, not edge fans

Design law for set-valued facts. Measured live against `laplace` 2026-08-15 unless a line
says otherwise. Companion to `08_Record_vs_Calculate_Spec.txt` (what may be derived) and
`36_Laplace_Forward_Pass.md` (where a distribution belongs in the program).

---

## 0. The requirement

A set-valued attribute is stored as ONE composition entity and ONE attestation, never as one
attestation per member. A fan of edges gives the set no id, so no witness can corroborate or
refute the set as a whole, and `attestations.context_id` — a single `bytea` — cannot hold a
set at all, which forces an emitter to drop all but one member.

Measurements motivating this design are recorded in the pull request that introduced it.

## 1. Three shapes, one emitter

An emitter that writes a fact about a subject is choosing between three shapes. Two are
settled law here and the third has never been implemented.

| shape | example | correct storage | status |
|---|---|---|---|
| **ordered sequence** | word order in a sentence | trajectory geometry, read with `laplace_trajectory_constituents` | used by six inference sites |
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
 edges it is three independent claims and there is nothing to point at. The number of
   DISTINCT analyses in a corpus is orders of magnitude below the number of edges spent
   encoding them, so the same fact is re-derived once per form that carries it.
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
 **data loss** across every `TRANSCRIBES_AS` row, not merely a shape complaint. A
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
set well-defined, which is what makes it content-addressed, which is what collapses the
writes into one entity per DISTINCT member set. Order is not a policy choice; it is the
deduplication mechanism.

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

Steps 3–4 are idempotent by content address: every later form carrying `{nom, sg, masc}`
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

Reverse reads still pay the 216-leaf Append because `object_id` prunes at neither level,
so `subjects_with` resolves the bundle first and passes `type_id`.

---

## 6. Which shape a site is

**Genuinely set-valued — the whole of it:**

| relation | site | why it is a set |
|---|---|---|
| `HAS_FEATURE` | `Wiktionary/WiktionaryEmit.cs` `WalkForms` | a form's tags are one morphological analysis |
| `TRANSCRIBES_AS` | `Wiktionary/WiktionaryEmit.cs` `WalkSounds` | the dialect tags qualifying one transcription |
| `HAS_USAGE_REGISTER` | `Wiktionary/WiktionaryEmit.cs` `WalkSense` | a sense's register is one reading |

**Not set-valued — do not convert:**

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
- `WordNet/WordNetDecomposer.cs` — multiple `HAS_DEFINITION` / `HAS_EXAMPLE` per
 synset are independent claims, each separately corroborable. Multi-valued is not
 set-valued.

**Single-valued and correct as edges:** `HAS_LINE_BREAK`, `HAS_EAST_ASIAN_WIDTH`,
`HAS_BLOCK`, `HAS_AGE`, `HAS_SCRIPT`, all from `Unicode/UnicodeDecomposer.cs`. A codepoint
has exactly one block.

**The test that separates the three cases**, since the relation name does not:

1. Would a second witness corroborate or refute the members *as a whole*? → set → bundle.
2. Does each member answer a differently-named question? → record → one typed edge per field
 (UD FEATS).
3. Is each member an independent claim about the subject? → multi-valued → one edge each
 (WordNet glosses).

---

`consensus.belief_distribution(subject, relation, k)` —
`extension/laplace_substrate/sql/functions/consensus/belief_distribution.sql.in`. The
normalised distribution over a subject's objects under one relation, from Glicko-2 log-odds
(`consensus.glicko2_logit`) rather than from a dot product. It is the probability counterpart
to `generation.adjudicated_row`'s exact ranking, and it returns the **empty set** when the
subject couples to nothing — the answer a softmax cannot give.

A collection changes what that distribution is *over*. With the edge fan, a distribution over
`HAS_FEATURE` is a distribution over individually-attested tags, which is not a
morphological analysis. With bundles it is a distribution over attested analyses,
which is.
