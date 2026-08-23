# Conversation defects — measured 2026-08-23

Measured against the live `laplace` substrate (70.7M entities, 136.3M attestations,
124.4M consensus cells; foundation + OMW + UD + ConceptNet + Atomic2020 resident,
Wiktionary and ChessPgn `failed` 2026-08-22 on the connection-pool defect).

Every number below is a live measurement, not an inference. Nothing here is fixed
except D6.

## What `converse.chat` actually replies

| prompt | reply |
|---|---|
| `What is a glacier?` | "The concept ايه is one I hold." |
| `What is a dog?` | "It is a kind of domainendung, which is a kind of domain, which is a kind of taxon, which is a kind of rang taxinomique." + ~1,500 `and`-joined tokens |
| `The opposite of hot is` | "I is a kind of vokal, which is a kind of laut, which is a kind of sprachstil…" |
| `hello` | "Hello is a kind of greeting, which is a kind of social activity, which is a kind of social event, which is a kind of event." + every translation |

One of four produces a correct sentence. `hello` proves the frame machinery works
when the election lands; the other three are election and realization failures.

## D1 — prompt tokens resolve case-sensitively, which INVERTS the election

`converse.prompt_words` hashes the raw surface, so `What` and `what` are different
content ids. Capitalized forms hold almost no evidence:

| surface | consensus edges | | surface | edges |
|---|---|---|---|---|
| `France` | 224 | | `france` | 2,501 |
| `Water` | 1 | | `water` | 2,545 |
| `The` | 0 | | `the` | 406 |

`specificity = coherence / total_mass`, so a starved denominator INFLATES the score:

| token | total_mass | coherence | specificity |
|---|---|---|---|
| `What` | 5.81e12 | 8.66e11 | **0.1490** ← elected |
| `what` | 1.69e15 | 2.37e13 | 0.0140 |
| `glacier` | 3.90e14 | 4.08e11 | 0.0010 |

In English the sentence-initial word is nearly always a function word, so the
least-attested token wins by construction. Both "passing" eval probes pass for this
reason: `Water` wins on a single edge, `dog` is a one-token prompt. The eval is not
2/6 — it is 0/6 correct with 2 lucky.

The fix already exists as an installed operation and is not called:
`lexical.word_case_variants_batch(bytea[])` maps `What → what (260 edges)`,
`The → the (406)`, `Water → water (2,545)` in ~0.3 s. `prompt_words` is SQL, so
this is a one-place change the whole conversational path inherits.

STATUS: NOT FIXED.

## D2 — the one content word is tagged a foreign language, and loses for it

`converse.word_language` on `What is a glacier?`:

| token | language |
|---|---|
| What | (none) |
| is | **enm** (Middle English) |
| a | eng |
| **glacier** | **fra** (French) |
| ? | eng |

`prompt_coherence.c:1050-1070` ranks `lang_agree` ABOVE witness count, deliberately:
"a sense in a language the prompt is not written in is the wrong sense however well
attested it is". With the prompt electing English and `glacier` tagged French, the
correct topic is demoted by the very axis meant to protect addressing. This is W14's
"addressing failure" firing against the content word instead of for it.

STATUS: NOT FIXED. Note `docs/read-path.md:160-165` already records that
`attested_language` and `word_language` disagree (lobo → Spanish vs Portuguese).

## D3 — realization emits an unbounded translation family

`What is a dog?` returns ~1,500 `and`-joined surfaces — every pronoun in every
language the substrate holds. `taxonomy.bubble_up_batch(word_id('dog'))` returns
`hund, can, dog, hundur`: the ILI/synset family realized across languages with no
language pin at the realization step.

`chat.sql.in:77-82` documents this exact failure ("converse.walk('dog') returned a
fluent Bulgarian description of a dog… the mesh working exactly as designed,
surfacing at the wrong moment") and the guard did not hold here. Separately there is
no CAP: a reply may carry a four-figure token list.

STATUS: NOT FIXED. Two distinct defects — language pin at REALIZE, and a bound.

## D4 — six copies of one election, synchronized by a string-matching test

The topic election is one semantic fact (OP6 STEER + OP7 SELECT) implemented six
times: `converse.sql.in:57`, `converse_walk.sql.in:63`, `resolve_topic.sql.in:75`,
`infer.sql.in:44`, `orient_topic.sql.in:90`, and mirrored in Python at
`scripts/eval-generation.py:155` (`_elector_key`). They are kept in step only by
`ElectorArchitectureGateTests` asserting the six ORDER BY strings match.

This violates spec 37's implementation law ("There is one canonical implementation
per operation fact… Endpoint-specific helpers delegate to the same program") and is
the `docs/sql-cohesion-audit-2026-08-18.md` verdict in miniature ("semantic
operations… are still governed in different places").

W4 already recorded the consequence: "the gate is honest and the thing it guards is
hollow".

STATUS: NOT FIXED.

## D5 — the election ranks on one scalar, against the charter

`docs/INVENTION.md:282-285`: "no ranking may be decided on a single-token scalar."
W15: "an election is convergence across independent evidence classes, not an
extremum on one axis", with the rule "agreement across classes 1–3; class 4 breaks
ties; class 5 never ranks meaning."

What exists is `specificity DESC` plus five tiebreaks. W15's failed-scalar table
(denote_mu → function words; total_mass → degree; ord DESC → SVO; highway popcount →
the tier-0 floor; container IDF → rare words; coherence/total_mass → 0 without a
direct edge) applies to the leading key as much as the retired ones.

STATUS: NOT FIXED — and it must not be "fixed" by a seventh scalar.

## D6 — 82% of WordNet senses folded to an identical draw (FIXED)

`index.sense` is `sense_key synset_offset sense_number tag_cnt`. The parser read the
key and offset, STEPPED OVER `sense_number`, and used `tag_cnt` alone as the
HAS_SENSE magnitude. Measured on WordNet-3.0: **171,463 of 206,941 senses (82%)**
carry `tag_cnt = 0` → magnitude 0 → `laplace_score_fp` returns exactly 0.5, a DRAW →
every sense of such a lemma folds to an identical rating.

Consequence, measured live: eff_mu spread across a token's senses is **0.0** for
`what` and `the`, 15.6 for `is`, against 95.9 `chess` / 80.4 `pawn` / 77.5 `dog` /
76.8 `france`. The tokens the election cannot separate are exactly the ones the
corpus said nothing discriminating about.

FIX: magnitude is now `tag_cnt + 1/sense_number` — occurrence evidence stays
dominant (the added term is bounded by 1), and the 82% gain a discriminator. Both
signals come from the same witnessed line.

PROVEN on a throwaway isolate (`just decomposer-test wordnet`, GATES OK):

| lemma | senses | distinct ratings | spread |
|---|---|---|---|
| a | 7 | **7** | 46.1 |
| dog | 8 | **8** | 55.7 |
| pawn | 5 | 4 | 24.4 |

W4 recorded a 5-way exact tie at 994.8 on `a`. The ties are broken. This satisfies
W4 acceptance item 2 ("`denote_mu` measurably non-constant across the senses of one
lemma").

STATUS: FIXED (commit `f2ef1fae`). Takes effect on the next wordnet seed.

## D7 — the substrate cannot reach the France answer at all

`prompt_coherence('The capital of France is')` returns specificity 0, rel_mass 0,
peers 0 and no typed relation for `france`. There is nothing to rank.
`prompt_coherence.c:1122` records the same: "france remains #1099's stranded fact,
unreachable by any ranking — the fact is stranded in gloss prose".

CONSEQUENCE: the eval gate requires 6/6 exact and therefore CANNOT pass by any
ranking change. #1099's gloss-relation analyzer is a precondition.

STATUS: NOT FIXED, and not fixable in the elector.

## D8 — evidence is constant across 96.8% of the substrate

Per-observation score (`sum_score_fp1e9 / observation_count`) over 139,559,212
attestations:

| score | rows | share |
|---|---|---|
| exactly 1.000 (`confirm:true`) | 112,004,747 | 80.26% |
| exactly 0.750 (magnitude 1.0, arena 1.0) | 20,028,204 | 14.35% |
| exactly 0.500 (the D6 draw) | 2,086,022 | 1.50% |
| exactly 0.000 | 1,027,106 | 0.74% |
| genuinely varying | 4,413,133 | **3.16%** |

116,169,744 of 127,023,784 consensus cells (91.5%) have exactly one witness. Static
census: 199 `NativeAttestation.Categorical` call sites, 5 pass a magnitude, 191
default `confirm:true`.

`HAS_RATING` (chess Elo) measures **1 distinct rating value** — Elo is stringified
into an opaque content entity (`ChessPgnDecomposer.cs:775-779`). In a Glicko-2
substrate the relation whose object IS a rating carries zero information. Opponent μ
is also pinned to the constant 1500 (`consensus_fold_math.h:20`).

Positive controls prove the path works: ConceptNet's real edge weights vary
(AT_LOCATION 35.8%); Stockfish `HAS_EVAL` yields 2,754 distinct ratings from one
witness.

STATUS: D6 converts the 1.50% bucket. The 80% has no magnitude slot filled at all.

## Order of operations

1. D6 is fixed but inert until `wordnet` is reseeded.
2. D1 and D2 are addressing defects and land before any election change — until a
   token resolves to the entity that holds its evidence, no ranking is meaningful.
3. D4 (collapse six bodies into one) is the precondition for D5, so the judgement
   can be changed in one place instead of six.
4. D5 per W15: agreement across evidence classes, never a seventh scalar.
5. D7 is #1099 and gates the eval regardless of 1–5.
6. D8 is the ingest-side program the read path depends on.
