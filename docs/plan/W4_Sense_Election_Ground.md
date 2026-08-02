# W4 — The ground under every election: tier collision + sense priors

**Issues:** #752 (seam), #753 (priors) · **Plan:** `COMPLETION_PLAN.md` R2/R3 /
Phase 3 · **Blocks:** W5's `election_correctness` thresholds, and any honest
claim about answer quality

---

## 0. Two corrections to the recorded diagnosis

Both were found by running the query the specs never ran. They change what
should be built.

### 0.1 The tier collision is not what produced tonight's letter-A failure

`.scratchpad/38 §12b` and spec 37 §8.2 attribute the `senses('a')` union — and
therefore the *"A is the 1st letter of the Roman alphabet"* class of failure — to
73 ids holding both a tier-0 `Codepoint` row and a tier-2 `POS` row.

**Measured on the live substrate 2026-08-02:**

```sql
SELECT tier, count(*) FROM entities WHERE id = word_id('a') GROUP BY tier;
--  tier | count
--     0 |     1        ← ONE row. No tier-2 row exists.
```

There is no tier collision on this instance, because **the UD lane that mints
the colliding rows has never been ingested here** (`ingest_run_journal` shows
foundation lexical sources plus chess; no UD). Yet the letter-A failure was
reproduced repeatedly tonight.

**Therefore the tier collision is not a necessary cause of the failure it is
credited with.** The seam is a real code defect (§1) and it is *not* the thing
standing between this substrate and correct elections.

What actually produces the `a` sense set here — measured:

```sql
SELECT realize(type_id), count(*) FROM consensus WHERE subject_id = word_id('a') GROUP BY 1;
--  CONFUSABLE_WITH 22 | HAS_SENSE 7 | EVOKES_FRAME 4 | HAS_POS 1 | IS_SYNONYM_OF 1 | …

SELECT realize(synset_id), eff_mu FROM senses(word_id('a'));
--  a 1169.7 | a 1144.7 | amp 994.8 | antiophthalmic factor 994.8 | a 994.8 | a 994.8
--  | deoxyadenosine monophosphate 994.8
```

`'a'` legitimately carries **seven** `HAS_SENSE` edges from lexical sources —
the article, the vitamin, the nucleotide, the ampere. The election didn't pick a
letter out of a corrupted candidate set; it picked from a genuine, well-formed
one where **five of seven senses tie at 994.8** (§2.2 explains why). The
"letter" surfacing in the *reply* is a separate step: rendering a bare word id
prefers the Unicode `HAS_NAME` (`realize.sql.in:12-18` →
`resolve_name.sql.in:31-34`), producing *"LATIN SMALL LETTER A"*. That render
path needs no tier row at all.

**Consequence for sequencing:** the plan currently says A-before-B (fix the seam
before the priors). **Invert it.** The priors are the live defect on the seeded
substrate; the seam is a latent defect that appears when UD is seeded.

### 0.2 "Filter `senses()` by tier" is not implementable

`consensus` has **no tier column** (`schema/tables/consensus.sql.in:23-34`).
Neither does `attestations` or `physicalities`. **Tier exists only on
`entities`.** Edges attach to the *id*, and a colliding pair shares that id — so
no tier predicate can partition the edge set, and
`EXISTS(… WHERE id = p_word AND tier = 2)` is true for the id whichever lineage
minted the edge.

Neither spec 37 §8 nor `.scratchpad/38` §12b states this, and both imply a
read-side tier fix is available. It is not. The remedy is at ingest (§1) or via
an **object-type** predicate, never a tier one.

## 1. The seam — root cause, resolved

Recorded as an open question (*"is the tier-2 POS row a correct attestation or a
witness-boundary defect? Not yet resolved"* — `.scratchpad/38:576-583`). It is
now answerable from the code.

**The minting site:** `HighwayNodeEmitter.Emit` (`HighwayNodeEmitter.cs:18,21`)
derives the id as `HighwayPerfcache.NodeHash(canonicalName)` — plain
`Blake3(utf8)` of the name (`HighwayPerfcache.cs:15,17-28`) — and adds it at
`EntityTier.Word` (= 2). The single production caller that passes single-character
names is `UdSentenceEmitter.cs:76`, emitting UD **XPOS** tags with
`PosReference.PosTypeId`. The sibling caller (`:93`) passes `"{feat}={val}"`,
always ≥3 chars, and cannot collide.

**Why only single characters collide — a law, not an accident:**
`hash_composer.c:23-24` takes a single child's id verbatim (`n == 1 ⇒ out = child[0]`),
and a codepoint's id is `blake3(utf8(cp))` (`unicode_seed.cpp:316-325`).
Therefore `content_root_id("a") == blake3("a") == NodeHash("a")`. Pinned as
regress law: `tests/sql/converse.sql:4`.

**Verdict: a witness-boundary defect at ingest.** The substrate already
implements the exact collapse the vocabulary lane skips —
`content_witness_batch.c:78-116`, whose own comment records fixing this bug in
the content lane: *"The old `tier <= 1` stop minted a tier-1 entity row for every
single-cp character (same id as the codepoint, wrong stored tier)."*
`HighwayNodeEmitter` never learned it. There is also an established namespaced-key
convention for vocabulary ids that XPOS bypasses — POS itself uses
`substrate/pos/<CANON>/v1` (`pos_law.c:144-156`), PropBank uses `ordinal/{n}/v1`.

**Why the duplicate survives the write path:** existence is probed keyed by
`(id, tier)` (`probes/entities_stored_bitmap.sql.in:11-14`, used at
`NpgsqlWorkingSetApply.cs:503`) and the upsert conflict target is `(id, tier)`.
An id stored at tier 0 probes as *absent* at tier 2. There is no floor collapse.

**Second, already-known collision class:** `identity_law_violations.sql.in:17-24`
records ~5,063 SemLink staging identifiers duplicated across word and sentence
tier. Any fix must cover both classes or scope itself explicitly.

### Fix options

| Option | What | Cost |
|---|---|---|
| **A (recommended)** | Namespace vocabulary ids — derive from `SubstrateCanonicalKeys.OfVersioned("xpos", tagset, name)`, matching `pos_law.c` | Changes ~36k XPOS ids and their edges. Consensus accumulates and there is **no backfill path** — this is a **reseed of the UD lane**, not a migration |
| B | Collapse to the floor: resolve through `ContentEmitter.RootId(name)` and skip the tier-2 row when it matches, mirroring `content_witness_batch.c:109-116` | Ids stay stable, no reseed — but leaves `blake3('a')` typed `Codepoint` while carrying inbound `HAS_XPOS`, so the mass corruption survives. Half a fix |
| C | Read-side **object-type** predicate (require `WordNet_Sense`/`WordNet_Synset`) | An `entities` lookup per candidate on the hot path, and it discards Wiktionary/PropBank sense lineages. Diagnostic only |

**Independent one-line fix, correct under any option:** `prompt_words.sql.in:8-11`
has no `DISTINCT`, so an id at two tiers yields **two rows for one token**,
which fans out through `prompt_state` into `prompt_coherence`'s candidate query
(`prompt_coherence.c:300-303`). Duplicates tie and are dropped by the memcmp
anchor, so elections are unchanged — but `n_cand` inflates. Make it
`DISTINCT ON (ws.ord)`.

## 2. The priors — the live defect

### 2.1 Current comparator

`prompt_coherence.c:632-695`, within one `ord`: `share` (coherence/total_mass)
→ `rel_mass` → `denote_mu` → `total_mass` → `memcmp(syn)`.

Note a divergence worth fixing or documenting: the *outer* cross-token election
(`chat.sql.in:233-238`) sorts `specificity, rel_mass, peers, ord, denote_mu,
synset_id`. `peers`/`ord` exist only in SQL; `total_mass` only in C. **The two
rankings are not the same key list.**

### 2.2 Why `denote_mu` is a constant — the exact mechanism

`WordNetDecomposer.cs:330-332` passes `magnitude: s.TagCount` into
`laplace_score_fp`, which is `0.5 * (1 + v/(m+|v|))` (`score.c:7-10`). **For the
overwhelming majority of WordNet senses `tag_cnt = 0`, so score = 0.5 exactly —
a draw** — identical rating, identical `eff_mu`, identical `denote_mu`. Measured
above: five of `a`'s seven senses tie at 994.8.

**And WordNet ships the discriminator, which the parser skips.** `index.sense`
is `sense_key synset_offset sense_number tag_cnt`, and
`WordNetDecomposer.cs:636-638` does `idx += line[idx..].IndexOf(' ') + 1` —
**stepping over field 3, `sense_number`, without reading it.** `sense_number` is
never 0 and is always distinct within a lemma. `index.noun/verb/adj/adv`, which
also carry ordering, are not read at all (`:41,186`).

### 2.3 The band blocker — resolved

`laplace_relation_def_t` has no band field, and `relation_highway_band()` is
`require_highway_table`-gated (`highway_mask.c:301-316`). Three routes:

**Route 1 — recommended: add `band` to the generated relation table.** Band is
already computed at codegen (`_RANK_BANDS`, `_rank_to_band`,
`codegen-attestation-law.py:919-941`) for the highway blob. Three edits to the
codegen script (never the generated C — `CLAUDE.md`) and the field arrives in
the `def` that `laplace_relation_lookup` **already returns on every edge**.
**Zero per-edge cost, zero gating, no error path.** Appending a field is safe:
no C code constructs the struct.

**Route 2 — call `highway_table_relation_by_hash` natively**
(`highway_table.h:44-47`): an in-memory open-addressed probe, ~1.1 probes
average, no SPI. Precedent exists — `generate_walk.c:216-229` calls
`highway_table_mask_and` directly, ungated. **Do not call
`laplace_highway_ready()`**: it `ereport(ERROR)`s when the GUC path is set but
the blob is bad (`perfcache.c:109-115`), which would turn a misconfigured host
into a hard failure on every chat prompt. Use `highway_table_is_loaded()`, or
treat a non-zero return as "unbanded." Caveat: on a backend that never touched a
highway function the table may be unloaded, making bands silently absent — which
is why Route 1 is better.

**Route 3 — derive band from `rank`** (the 13 values are 1:1 with the 13 bands).
Zero plumbing, but an implicit invariant nothing enforces. Stopgap only.

**Cross-cutting:** dynamic relations (`DEP_*`, `FEAT_*`, `EDEP_*`) are not in
`k_relations`, so lookup fails and `rank` is already `0.0` — they contribute
nothing today and would contribute no band. Decide explicitly whether that is
correct.

### 2.4 Signal that already exists and is unused

- **`witnesses`** — `bubble_up.sql.in:50` computes it and uses it as a sort key;
  `prompt_coherence`'s candidate query selects only `(ord, id, synset_id,
  eff_mu)`. **A salience signal sitting one column away from the comparator.**
- **`bubble_up.score`** = `base_eff_mu × (1 + ln(1 + domain_hits))` — computed,
  then discarded by `senses()` (`senses.sql.in:9-11`).
- **`senses_with_context`** already runs band-gated lanes
  (`senses_with_context.sql.in:79,111,117`) — the content-band machinery exists
  in SQL, and hard-errors without the perfcache, which is exactly the failure
  Route 2 must avoid.

## 3. What to consider

| Decision | Recommendation |
|---|---|
| **Order: seam vs priors** | **Priors first** (inverting the plan). The seam is absent from the seeded substrate; the priors are the live defect. Re-sequence Phase 3. |
| Where the sense prior goes | **(a) fold the ordinal into the existing `HAS_SENSE` magnitude** — one line at `WordNetDecomposer.cs:332`, no manifest bit, no read-side change, and it directly kills the 994.8 tie. Transform the ordinal (`1/rank`) rather than passing it raw, since `score.c` saturates. **(b)** a `HAS_SENSE_RANK` edge to an `ordinal/{n}/v1` entity (PropBank precedent) is the first-class, refutable version — add it after (a) proves the signal. **Never** mint the ordinal as a bare digit: `NodeHash("1")` lands on the codepoint `1`, reproducing §1's bug. |
| If (b): manifest work | explicit unique `bit = N` (codegen hard-fails otherwise); **do not put it in the `HAS_SENSE` family** or `bubble_up`'s `fam` set will treat rank objects as senses; declare it in `WordNetSource.BuildRelations()` or the decomposer gate faults the native path. Owes no reseed. |
| Band route | **Route 1.** |
| Comparator placement | insert content-band mass between `rel_mass` and `denote_mu`, leaving specificity and relation-naming untouched; resolve the band **once per type** at `PcTypeEntry` creation (`:198-203`), never per edge. |
| Raw content mass vs share | measure both. A `total_mass`-denominated share re-introduces the mass-shaped failure documented three times in this file's history. |

## 4. Where to look

| Concern | Citation |
|---|---|
| Minting site | `HighwayNodeEmitter.cs:18,21`, `HighwayPerfcache.cs:15-28`, `UdSentenceEmitter.cs:76,93` |
| Why single chars collide | `hash_composer.c:23-24`, `unicode_seed.cpp:316-325`, pinned `tests/sql/converse.sql:4` |
| The collapse the content lane already does | `content_witness_batch.c:78-116` |
| Namespaced-key convention | `pos_law.c:144-156`, `PropBankDecomposer.cs:28,121` |
| No tier on consensus | `schema/tables/consensus.sql.in:23-34` vs `entities.sql.in:8-16` |
| Sense chain | `lexical/senses.sql.in:6-12` → `taxonomy/bubble_up.sql.in:27-42,63-72,131-153` → `lexical/lexical_peers.sql.in:12-18` |
| Score saturation | `WordNetDecomposer.cs:330-332`, `engine/core/src/score.c:7-10` |
| The skipped field | `WordNetDecomposer.cs:636-638`, roster `:41,186` |
| Comparator | `prompt_coherence.c:632-695`; type entries `:198-203`; candidate query `:300-303` |
| Band at codegen | `scripts/codegen-attestation-law.py:313-372,393-397,919-941` |
| Native band probe | `engine/core/include/laplace/core/highway_table.h:42-47`, precedent `generate_walk.c:216-229` |
| Tests that pin current behavior | `tests/sql/converse.sql:44-57,105,140-143`, `tests/sql/chat_loop.sql:37-49` (pins an **exact reply string** over single-character fixtures), `tests/sql/identity_law.sql:5` |

## 5. Acceptance

1. `dog → canine`, `trumpet → instrument`, `car → automobile`, plus ten held-out
   ambiguous words; `pawn`/`glacier`/`napoleon` unregressed.
2. `denote_mu` measurably **non-constant** across the senses of one lemma —
   re-run the `senses(word_id('a'))` query above and show the ties broken.
3. `prompt_words('what is a dog')` returns exactly one row for `a`.
4. Seam: `identity_law_violations()` returns zero `multi_tier_entity` rows on a
   reseeded substrate **and** a regress fixture deliberately inserts a colliding
   pair, so `identity_law.sql` can actually fail. Today it runs against a fresh
   bootstrap DB and is green while the defect is live — a detector that cannot
   fail.
5. `chat_loop.out` / `converse.out` regenerated only if fixture semantics
   changed, with the diff explained line by line — they pin the whole read stack
   including an exact reply string.
6. No new SPI inside `pc_scan_edges`' per-row loop; wall clock within the
   measured 1.4–3.9 s envelope.

## 6. Risks

- **The recorded diagnosis was wrong about causation** (§0.1). Re-check any
  other claim that leans on it before building — including this document.
- **`identity_law_violations()` timed out** on the live box when run without a
  bound (statement timeout). Whatever gate uses it needs a bounded form.
- Option A is a **reseed** of the UD lane; the fold has no backfill path, and
  re-ingest doubles observation counts unless the source marker guard is used.
- Do **not** "fix" the collision by mixing tier into the hash. `word_id`/
  `canonical_id` equality for one-character strings is pinned law
  (`converse.sql:4`), and `entities.sql.in:1-3` plus `CLAUDE.md` both forbid it.
- A read-side filter added to `senses()` must not introduce `SET search_path` or
  `STRICT` — either kills inlining (#617, `senses.sql.in:3-5`).
