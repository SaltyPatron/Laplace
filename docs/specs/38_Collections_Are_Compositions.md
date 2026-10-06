# 38 — Collections are compositions, not edge fans

Design law for set-valued facts. Companion to `08_Record_vs_Calculate_Spec.txt` (what may be derived) and
`36_Laplace_Forward_Pass.md` (where a distribution belongs in the program).

---

## 0. The requirement

A set-valued attribute is stored as ONE composition entity and ONE attestation, never as one
attestation per member. A fan of edges gives the set no id, so no witness can corroborate or
refute the set as a whole, and `attestations.context_id` — a single `bytea` — cannot hold a
set at all, which forces an emitter to drop all but one member.

## 1. Three shapes, one emitter

An emitter that writes a fact about a subject is choosing between these shapes.

| shape | example | correct storage |
|---|---|---|
| **ordered sequence** | word order in a sentence | trajectory geometry, read with `laplace_trajectory_constituents` |
| **enumerated property** | a codepoint's block, age, script, East Asian width, line break class | an enum mask bit at tier 0, set where Unicode asserts it |
| **single-valued attribute** | a record field, such as a UD feature (`Case=Nom`) | one claim per differently named field |
| **set-valued attribute** | a form's morphological analysis `{nominative, singular, masculine}` | **a composition entity, one claim** |

Writing shape 3's data with shape 2's loop — one attestation per tag — leaves the analysis itself with no id.

---

## 2. Why the fan is wrong, beyond its size

1. **The set has no identity.** `{nom, sg, masc}` is one morphological analysis. As three
 edges it is three independent claims and there is nothing to point at. The number of
   DISTINCT analyses in a corpus is orders of magnitude below the number of edges spent
   encoding them, so the same fact is re-derived once per form that carries it.
2. **Nothing can be attested about the set.** Glicko-2 adjudicates one claim composition.
 With no bundle entity there is no rating, no `witness_count`, and no refutation of
 *the analysis* — only of its members, which is a different claim. A second source that
 disagrees about the analysis as a whole has nowhere to put the disagreement.
3. **`context_id` is one slot.** `laplace.attestations.context_id` is a single `bytea`. A set
 of qualifiers (the dialect tags on one transcription) does not fit in it; writing one member
 loses the rest. A bundle id fits in the slot.
4. **The read is a self-join.** "forms that are nominative AND singular AND masculine" is an
 N-way self-join over the attestation relation, per join arm. As a composition it is one `@>`
 probe on `physicalities_constituents_gin`, the probe `containers_of.c` runs.

---

## 3. The pieces

A set uses existing primitives:

| piece | site |
|---|---|
| packed constituent geometry from ids | `Trajectory.Build(ReadOnlySpan<Hash128>)`, `app/Laplace.Core/Core/Trajectory.cs:5` |
| merkle id over children | `hash128_merkle`, content-derived, no float |
| **permutation-invariant placement** | `Math4d.KarcherMean`, `app/Laplace.Core/Core/Math4d.cs:16-22` — "all three internal accumulations run in a canonical order, so a placement is reproducible under constituent reordering" |
| membership probe | `physicalities_constituents_gin`, `IngestCommands.cs:799`; `laplace_trajectory_constituent_ids(trajectory) @> ARRAY[$1]` |

The Karcher-mean line is the load-bearing one. An unordered set lands at the same coordinate
regardless of member order, so a *set* is a well-defined composition in the geometry.

**Canonical order for a set is ascending by member id.** That is what makes the merkle id of a
set well-defined, which is what makes it content-addressed, which is what collapses the
writes into one entity per DISTINCT member set. Order is not a policy choice; it is the
deduplication mechanism.

---

## 4. The write API

One method on `SubstrateChangeBuilder` (`app/Laplace.Substrate/Crud/SubstrateChangeBuilder.cs`):

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
it gets its own type value and its own partial GIN.

**What this is not.** Not a CSV column, not a `text[]`, not JSON, not a bitmask. A collection
is an entity with a merkle id, a coordinate, and typed edges — the same as every other thing
in the substrate. A representation that cannot carry a rating is not a collection.

---

## 5. The read API

Two functions, both on shapes that already have index support:

- `consensus.set_members(p_subject bytea, p_relation text)` — the bundle's constituents, in
 canonical order, via `laplace_trajectory_constituents`.
- `consensus.subjects_with(p_relation text, p_members bytea[])` — GIN containment on the
 bundle, then the reverse edge.

`object_id` does not prune partitions, so `subjects_with` resolves the bundle first and
passes `type_id`.

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
 `Name=Value` entity. That is a record with named fields, and it is the *better* shape than a bundle: each field is
 independently adjudicable and independently queryable. `FEAT_Case` and its siblings are
 developer handles for registry slots; the feature relations are content-derived
 entities, and no identity is derived from those English labels.
- `ConceptNet/ConceptNetSource.cs`, `Atomic2020/Atomic2020Source.cs` — each source row is
 one claim, one relation per row. There is no bundle in the input to preserve.
- `WordNet/WordNetDecomposer.cs` — multiple definitions and examples per synset are
 independent claims, each separately corroborable. Multi-valued is not set-valued.

**Enumerated properties at tier 0:** a codepoint's line break class, East Asian width,
block, age and script, all from `Unicode/UnicodeDecomposer.cs`, are mask bits at tier 0,
set where Unicode asserts them. A codepoint has exactly one block. The current decomposer
writes them as per-codepoint `HAS_*` edges; that is the as-built form, not the law. The
relation names here are developer handles; no identity derives from them.

**The test that separates the three cases**, since the relation name does not:

1. Would a second witness corroborate or refute the members *as a whole*? → set → bundle.
2. Does each member answer a differently-named question? → record → one claim per
 differently named field (UD FEATS); a closed enumeration Unicode asserts of a codepoint is
 an enum mask bit at tier 0.
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
