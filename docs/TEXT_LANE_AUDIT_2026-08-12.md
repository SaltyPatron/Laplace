# Text lane audit — 2026-08-12

Scope: the UAX #29 kernel, its substrate-facing consumers, and what the live
`laplace` database shows they deposited. Per house rule, prose is evidence of
intent at best: every claim below carries a citation or a command. Kernel
measurements were made by running the built artifacts
(`build/engine/core/tests/laplace_core_tests`, direct calls into
`build/engine/core/liblaplace_core.so`); DB measurements against the live
`laplace` cluster mid-foundation-seed (so row counts are moving; mechanisms are
not).

Issues filed from this audit: #1039, #1040, #1041, #1042, #1043, #1044,
epic #1045. Comments updating #1010, #1019.

---

## 1. The kernel is conformant — the complaint is real but lives elsewhere

All three UAX #29 rule sets are fully implemented (grapheme incl. GB9c Indic
conjuncts and GB11 emoji ZWJ; word incl. WB4 ignores, WB6/7, WB11/12, WB13a/b,
WB15/16 RI parity; sentence incl. SB8 lookahead). Property tables are generated
from the UCD (`ucd.nounihan.flat.zip`, pinned `LAPLACE_UNICODE_VERSION "17.0.0"`
at `engine/CMakeLists.txt:91`), full 1,114,112-codepoint scope, packed
5b WB / 4b GB / 4b SB / 2b InCB / 8b ccc (`perfcache_format.h:28-52`).

Measured against the official conformance files in
`/vault/Data/UCD/Public/UCD/latest/ucd/auxiliary`:

```
build/engine/core/tests/laplace_core_tests --gtest_filter='*Break*UAX29*'
GraphemeBreak  pass=766   fail=0
WordBreak      pass=1944  fail=0
SentenceBreak  pass=512   fail=0
```

No TODO/FIXME/HACK/skips anywhere in the segmentation sources. Perfcache header
census (read from `build/engine/core/perfcache/laplace_t0_perfcache.bin`):
`ucd_version=17.0.0`, `record_count=0x110000`, ALetter 33,973, Extend 2,647,
Katakana 331, Numeric 784, HebrewLetter 75, RI 26, ExtendNumLet 11,
WSegSpace 14. These are the reference floors #1043's sanity census should pin.

## 2. Where it actually breaks: the consumer layer

| Defect | Issue | One-line mechanism |
|---|---|---|
| NFC offset-space mismatch; 3-byte heap overread returned as SQL `text` | #1039 | tree offsets are NFC-space (`text_decomposer.c:29-43`); `content_witness_batch.c:260` / `content_resolve.c:185` slice the caller's original buffer |
| Tiers assumed to nest; UAX #29 doesn't guarantee it | #1040 | 3,730 triples word-inside-grapheme, 56 sentence-inside-word → colliding ids, orphan nodes, sentences silently deduped away |
| Whitespace runs mint tier-2 Words | #1042 | `'a  b\r\nc'` → words `['a','  ','b','\r\n','c']`; filter is a hand-maintained list (`codepoint_table.c:276-287`), not `White_Space` |
| No provenance enforcement on the generated tables | #1043 | loader never checks `ucd_version`; two independent UCD mappings; grouped XML parses to all-Other silently |
| Identifiers text-decomposed as content | #1041 | live DB: `i35545` is a tier-2 Word, `02084071-n` a tier-3 Sentence |

`laplace_content_word_segment` — the entry point the whole substrate calls —
has zero tests (C or pg_regress). The conformance suite exercises the kernel;
nothing exercises the layer where all five defects live. ASCII inputs take none
of these paths, which is why an English-heavy seed looks healthy.

## 3. The framing that unifies them

The substrate's identity layer is language-agnostic by construction
(content-hash ids; language is an attested flag; CILI/ILI as the interlingual
spine). The intake repeatedly hard-codes "text behaves like English":

- hand-maintained whitespace list beside a generated-from-UCD perfcache;
- `pg_ascii_tolower` (`prompt_coherence.c:726-727`) and
  `btrim(lower(p_phrase), ' ?.!')` (`resolve_topic.sql.in:68`) as ad-hoc
  casing/trimming beside a native segmenter;
- no casefolding anywhere in the content lane while `CaseFolding.txt` /
  `SpecialCasing.txt` sit unread in `/vault/Data/UCD`;
- no dictionary breaking, so Thai/Lao/Khmer/Myanmar/CJK shatter per
  character/cluster — spec-literal UAX #29, but an undeclared decision that
  scriptio-continua languages get no real word tier;
- consumer tests that are ASCII-only.

Space-delimited languages appearing to work is what hid this. Per-script
segmentation strategy is data (`Scripts.txt` selects the module), which is the
recipe argument — see #1045.

## 4. Live-DB spot checks (2026-08-12, mid-seed)

```sql
-- every tier-0 codepoint is placed; few carry properties (#1044)
SELECT count(*) FROM laplace.physicalities p
JOIN laplace.entities e ON e.id = p.entity_id AND e.tier = 0;   -- 1,114,240
SELECT count(DISTINCT c.subject_id) FROM laplace.consensus c
JOIN laplace.entities e ON e.id = c.subject_id AND e.tier = 0;  -- 361,078

-- identifiers recorded as content (#1041)
SELECT e.tier, cn.name FROM laplace.entities e
LEFT JOIN laplace.canonical_names cn ON cn.id = e.type_id
WHERE e.id = laplace.word_id('i35545');       -- 2 | Word
--    laplace.word_id('02084071-n')           -- 3 | Sentence
```

## 5. Reseed sequencing (the part that is time-sensitive)

The document seed following the current OMW foundation seed is a reseed already
being paid for. Changes that alter ids are cheapest inside it and cripplingly
expensive after it:

1. **Blockers that poison ids** — #1039 (offset space), #1040 (nesting
   policy + invariant gate), #1041 (reference-vs-content disposition per
   curated field). Fix before any document ingest.
2. **Id-changing decisions to make now** — #1010 ladder depth
   (line/paragraph; UAX #14 source data confirmed present), #1042 whitespace
   disposition (if runs stop being constituents, byte-exact reconstruction
   needs `run_length` on the vertex, which the trajectory format already
   carries).
3. **Cost-only decisions** — #1044 placement admission (does not change ids;
   changes what every reseed pays for).

Longer arc: #1045 (recipes + staging/conform zone + verification gates) is
where the fixes land as architecture rather than patches.

---

## Addendum — same day, evening session (live measurements)

Issues filed from this addendum: #1048 (fray census), #1049 (Pillar-0 completion),
#1050 (one declared mean).

- **`tac` coord drift is gone.** §Placement of MODEL_INGESTION_DESIGN records
  `tac` differing from `cat`/`act` below 1e-12 from float accumulation order.
  Live today all three anagram coords are **bit-identical** (and share one
  hilbert index) — the canonical visitation order in `math4d.c:218-245`
  supersedes that claim. Prose lags code; this line is the correction.
- **Metric family split confirmed live.** `ST_HausdorffDistance` = exactly 0 for
  all three anagram pairs (set metric — order-blind); `ST_FrechetDistance`
  separates all three (0.351 / 4.505 / 4.813). Caveat recorded: trajectory
  vertices are mantissa-packed id bits at fixed exponent (`mantissa.c:45-70`),
  so Fréchet *magnitudes* over them are hash noise — separation is meaningful,
  gradation is not. Hausdorff-0 ∧ Fréchet-positive is a working anagram sensor.
- **Radial structure by tier** (1% sample, coords): tier 0 pinned at norm
  1.000000 exactly; nothing anywhere above 1.0 (glome bound holds by convexity);
  tier 4 max 0.848. Tier-2/3 rows at norm exactly 1.0 are **repeated-single-atom
  words** — whitespace runs (#1042) and doublings like `XX`: a mean of identical
  points is the point. "Norm 1.0" certifies *atom or repeated-atom run*, and
  must be read as such in any geometric query.
- **Collapse law census.** Exactly **10 ids** exist at more than one tier in the
  ~9M-row entities table (single codepoints `u A N D V P n X v $` at {0,2}) — a
  lexical-lane leak past the collapse gate, filed as a fray class in #1048.
  `'a'` itself is clean: one row, tier 0.
- **Collision doctrine** (recorded so the framing is not re-litigated): the
  architecture is a declared-equivalence cascade — identity collides never,
  trajectory on identical content, coord on multiset, hilbert on neighborhood.
  Collisions at a projection layer are discovered equivalences (the query
  mechanism), not defects. Every identity defect in this audit is a **failure to
  collide** (`▁cat`/`cat`, NFC/NFD, case, gauge) — under-collision, not over.
  A collision is a bug only where a layer's declared contract is injectivity,
  or where the equivalence a layer certifies is undeclared (the norm-1.0
  confusion above).
