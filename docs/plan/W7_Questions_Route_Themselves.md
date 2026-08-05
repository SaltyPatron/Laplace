# W7 — Questions route themselves (relation naming)

**Issue:** #756 · **Plan:** `COMPLETION_PLAN.md` R5 / Phase 5 · **Related:** W8
(`infer` C port), #575

---

## 0. Correction to a claim made 2026-08-02

An earlier session statement — *"'opening' cannot reach `HAS_ECO` today only
because nothing attests that name; the fix is attestations, not code"* — is
**wrong in both halves**, and the research that refutes it is below. Recorded
here rather than quietly dropped, because the plan's standard of evidence
requires it and because the wrong version is the appealing one.

1. **`prompt_coherence` consults no attested alias.** Name matching is pure C
   over the manifest canonical string. Depositing
   `(HAS_ECO, HAS_NAME_ALIAS, word_id('opening'))` writes a perfectly good
   consensus row that **nothing reads.** A C change is required.
2. **Even a perfect alias would not route this question**, because `HAS_ECO`
   hangs off a chess *position* entity, which is never a prompt token's synset —
   so the relation never becomes eligible in the first place.

## 1. How relation naming actually works

`prompt_coherence.c`, three passes, all bounded:

**Token universe (the precondition).** The only query populating tokens is
`:298-304`: `prompt_state($1) CROSS JOIN LATERAL senses(p.id)`. `tok_h` is
filled at `:363-367`. **A prompt word enters `tok_h` only if it resolves to an
existing entity *and* carries at least one synset.** A relation-naming word with
no witnessed sense is invisible regardless of anything else.

**Eligible relation types.** `PcTypeEntry` rows are created only inside
`pc_scan_edges` (`:198-203`) from `type_id` on edges whose subject or object is
a candidate synset. The naming pass iterates **only the types the candidate
senses actually have edges of** — never the manifest at large (`:195-197`).

**The name → id mint** (`:409-449`):
1. `laplace_relation_lookup` → `def->canonical` (the manifest string).
2. `last = strrchr(name,'_'); last = last ? last+1 : name` (`:424-425`) — the
   substring **after the last underscore**; no underscore means the whole name.
3. `len < 3 → skip` (`:426-428`) — the bound that stopped `IS_A` matching the
   article `"A"` and wrecking every election (`:41-45`).
4. lowercase in place, then `laplace_content_root_id(lower, len, &wid)`
   (`:431-435`) — byte-identical to SQL `word_id`.
5. Direct probe of `tok_h` (`:443`); on miss, one batched
   `IS_LEMMA_OF` hop (`:478-482`) whose semantics are `subject = lemma,
   object = inflected form` — this is what makes `"parts"` reach `HAS_PART`.

**Scoring** (`:559-564`): `subject_id = ANY($1) AND type_id = ANY($2)` —
**forward direction only**, credit `rank × eff`, and a token may not name a
relation for itself (`:607-610`).

### Why "opening" fails — three independent reasons

1. **String.** `HAS_ECO`'s last token is `ECO` → `content_root("eco")`, a
   different id from `content_root("opening")`. The only escape hatch is
   `IS_LEMMA_OF`, which is *morphological inflection* (`eco`↔`ecos`), not
   synonymy. No path exists.
2. **Topology — the deeper one.** `HAS_ECO` is deposited with the **final
   position id** as subject (`ChessOpeningsDecomposer.cs:110`). A chess position
   is never an English prompt token's synset, so `HAS_ECO` never enters `type_h`
   at all. Same for `GAME_HAS_OPENING`, whose subject is a line id
   (`ChessPgnDecomposer.cs:297-299`) — and whose last token *is* literally
   `"OPENING"`, so it would match the string and still score nothing.
3. **`tok_h` gating.** `"eco"` almost certainly has no synset, so even the
   canonical string cannot enter the token hash.

**Consequence for the plan:** `rel_mass` can only ever name relations that hang
off **word/synset** entities. Entity-grain relations (chess games, positions,
documents) are structurally out of its reach. R5 in the completion plan frames
this as "one attestation lane away" — true for the string half, **false for the
topology half**, and the plan should say so.

## 2. What writes relation names today — and why it doesn't connect

| Writer | Subject | Object | Reaches the matcher? |
|---|---|---|---|
| `BootstrapIntentBuilder` ctor (`:37-40`) | source id | content root of **source name** | no (not a relation type) |
| `BootstrapIntentBuilder.AddType` (`:59-61`) | entity-type id | content root of **type canonical** | no |
| `RelationTypeRegistry.SeedCanonical` (`:181-184`) | relation-type id | content root of the **full canonical string** | **no — see below** |

Note `AddRelationType` (`:65-72`) emits the entity row only, no alias.

**The object id is the wrong id.** `ContentEmitter.Emit`
(`ContentEmitter.cs:9-14`) hashes the whole string including underscores, so the
attested object for `HAS_PART` is `content_root("HAS_PART")`. The C matcher
computes `content_root("part")`. **The existing attestation lane and the matcher
do not share a coordinate.** (`content_witness_root_id_underscored`
— `content_witness_batch.c:170-183` — expands `_`→space before hashing;
`ContentEmitter` does not use it.)

Also: `AllCanonical()` (`RelationTypeRegistry.cs:159-167`) walks canonicals
only, so the manifest's existing 23 `[[alias]]` surfaces get no alias row
either. And `canonical_names` / `seed_relation_types.sql.in` are a **rendering
table** feeding `realize_batch`; `prompt_coherence.c` never reads them.

**Proof by exhaustion:** every SPI statement in `prompt_coherence.c` is
`:300-303` (candidates), `:154-157` (edge scan), `:479-481` (`IS_LEMMA_OF`),
`:561-563` (rel_mass). None queries `HAS_NAME_ALIAS`, `canonical_names`, or the
alias table.

## 3. How it should work

### Option A1 — attested alias probe in C (recommended)

After the direct `tok_h` probe (`:443-449`), add a second batched read keyed on
**the relation type ids already in `type_h`** — not on name-content ids:

```sql
SELECT n.subject_id, n.object_id FROM laplace.consensus n
WHERE n.subject_id = ANY($1)
  AND n.type_id IN (relation_type_id('HAS_NAME'), relation_type_id('HAS_NAME_ALIAS'))
```

Probe `tok_h` with `object_id`; on hit set `te->named` / `namer_mask`. Served by
`consensus_subject_type_btree`. Run the existing `IS_LEMMA_OF` hop over alias
objects too, and "openings" reaches whatever "opening" reaches.

- The substrate's **own attested vocabulary becomes the routing table**; new
  aliases need zero code after this one change.
- Naturally multilingual — `word_id` is language-agnostic, so a Bulgarian alias
  for `HAS_PART` works.
- Cost: one indexed read per prompt, bounded by `|type_h|`.

### Option A2 — expand the C matcher over the manifest alias table

Walk `laplace_relation_alias_table` in the naming loop. No consensus read,
deterministic, ships with the binary — but vocabulary then lives in a build
artifact rather than the fold, every new word is codegen + rebuild + redeploy,
and mixing natural-language surfaces into that table changes what
`laplace_relation_resolve_surface` accepts on the **write** path, which
decomposers depend on (`RelationTypeRegistry.ResolveUncached:41-58`). Real blast
radius.

**Ship A1 alone.** Doing both accepts two sources of truth for the same fact.

### The writer for A1

- *Lawful lane:* add an optional `surfaces = ["opening", "eco code"]` array to
  `[[relation]]` in the manifest, expose it through codegen as a surface table,
  and have `SeedCanonical` emit one `HAS_NAME_ALIAS` per entry under
  `SourceTrust.SubstrateMandate`.
- *Cheaper lane:* a seed `.sql.in` doing `consensus_upsert` of
  `(relation_type_id('HAS_ECO'), relation_type_id('HAS_NAME_ALIAS'), word_id('opening'))`,
  registered in both manifests. `witness_precedes_chain.sql.in` shows
  `consensus_upsert` is a legitimate in-SQL fold entry point. **Unverified** that
  no CI rule forbids seeds writing consensus — check before committing.

### The topology half

Reaching entity-grain relations (chess, documents) needs a *different*
mechanism: the prompt must resolve to the entity, not to a word sense. That is
`resolve_ref`'s job (hex ids, FEN rewriting — GH #575) and is where the chess
surfaces live today. **Do not conflate the two halves in one issue.**

## 4. Missing relation families

**Capacity:** 233 canonicals, bits 0–209 assigned, cap 256 → **46 free bits.**
Additions are append-only, no reseed (ADR 0001).

| Domain | Present | Missing |
|---|---|---|
| Spatial | `AT_LOCATION`, `LOCATED_NEAR` (symmetric), `CONTAINS`, `HAS_PART`, `HAS_MEMBER`; `HAS_REGION`/`HAS_PATCH` are **image** regions; `HAS_DOMAIN_REGION` is a WordNet *domain label*, not geography | `LOCATED_IN` (the containment complement), `PART_OF_COUNTRY`, `BORDERS`, `HAS_COORDINATES`, `FLOWS_THROUGH`, `HAS_ELEVATION`, `IN_CONTINENT` |
| Temporal | `ON_DATE`, `PRECEDES_IN_TIME`, `IS_BEFORE`, `IS_AFTER`, `HAS_AGE`, `HAS_EVENT` | — |
| Political/civic | **none** | `CAPITAL_OF`, `GOVERNED_BY`, `HEAD_OF_STATE`, `HAS_GOVERNMENT_TYPE`, `MEMBER_OF_ORGANIZATION`, `HAS_CURRENCY`, `HAS_OFFICIAL_LANGUAGE`, `HAS_POPULATION`, `HAS_AREA`, `HAS_TIMEZONE` |
| Biographical | **none** | `BORN_IN`, `BORN_ON`, `DIED_IN`, `DIED_ON`, `HAS_NATIONALITY`, `HAS_OCCUPATION`, `SPOUSE_OF`, `PARENT_OF`, `CHILD_OF`, `EDUCATED_AT`, `EMPLOYED_BY`, `HAS_TITLE`, `FOUNDED_BY` |

**Rank assignment matters more than it looks.** `rank` multiplies every
`rel_mass` and `coherence` contribution (`prompt_coherence.c:211-212,600-602`),
so a geographic family placed at `associative` (0.36) is outweighed by
`HAS_PART` on every prompt. Put `CAPITAL_OF`/`LOCATED_IN`/`BORN_IN` at
`partitive` (0.73) or `taxonomic` (0.90).

**Capacity decision.** ~30 flat relations leaves 16 bits. ADR 0001 §2 asks for a
completeness pass before piecemeal appends. Note that family structure does
**not** save bits — every `[[relation]]` still requires an explicit `bit = N`;
families save *declaration* burden via `ExpandRelationsWithFamily`, not capacity.

## 5. Where to look

| Concern | File |
|---|---|
| Token universe / `tok_h` | `src/prompt_coherence.c:298-304,363-367` |
| Type eligibility | `:195-203` |
| Name mint + lemma hop | `:409-449,466-514` |
| rel_mass scoring (forward only) | `:559-610` |
| Alias writers | `BootstrapIntentBuilder.cs:37-40,59-72`, `RelationTypeRegistry.cs:159-185` |
| The id mismatch | `ContentEmitter.cs:9-14` vs `content_witness_batch.c:170-183` |
| Rendering table (not consensus) | `readback/canonical_names.sql.in`, `generated/seed_relation_types.sql.in` |
| Entity-grain deposits | `ChessOpeningsDecomposer.cs:110`, `ChessPgnDecomposer.cs:297-299` |
| Bit capacity + law | `engine/manifest/relation_types.toml`, `docs/decisions/0001-highway-bit-order.md` |

## 6. Acceptance

1. A **word-grain** test first, since it isolates the string half:
   `prompt_coherence('what is a car made of')` returns
   `rel_type_id = relation_type_id('HAS_SUBSTANCE')` via an attested alias
   (`"made of"` / `"material"`). Today it returns NULL.
2. Regression: `'What are the parts of a car?'` still elects `car` then `parts`
   with `rel_type_id = HAS_PART`.
3. No new per-row SQL function call anywhere in the read
   (`prompt_coherence.sql.in:17-19`).
4. New relation families: codegen regenerates clean with no bit collision, and
   `git diff` on the highway manifest shows **only additions** — no bit moved.
5. The chess/entity-grain question is explicitly **out of scope** here and
   tracked under #575.

## 7. Risks

- **`tok_h` gating silently swallows aliases.** An alias word with no `senses()`
  row is ignored. Either seed a sense for alias surfaces or add a separate
  sense-free token hash for naming only.
- **Function-word capture, at scale.** The `len >= 3` bound exists because `"A"`
  named `IS_A` and wrecked elections. A natural-language alias list is a far
  wider attack surface — an alias like `"has"` reproduces that failure across
  every relation. Gate alias vocabulary on `len >= 3` **and** exclude
  high-`total_mass` function words.
- **Forward-only `rel_mass`** means an alias naming an object-side relation
  scores nothing. The manifest has a `flip` field; this pass ignores it.
- **Two sources of truth** if both A1 and A2 ship.
