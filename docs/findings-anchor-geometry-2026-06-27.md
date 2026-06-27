# Findings & Plan — anchor resolution, normalization, geometry (2026-06-27)

Session goal: make the substrate's cross-lingual links solid so ILI concepts reach their words in
every language (the "C-3PO / universal translator" core). Started as "why does i12345 have no German
word," became a structural audit of anchor resolution, normalization, and the geometry layer.

Companion design spec: `scratchpad/native-anchor-resolution-spec.md` (native anchor port).

## The mental model (corrected)

Identity is a **language/script/English-independent anchor stack**, none of it privileging one tongue:

    codepoint → script → language(ISO639) → concept(ILI) → sense → frame

- A concept (`i12345`) is **not** "superfine" — it's a language-neutral id carrying eng+deu+cmn… lemmas.
  Rendering it in one language is a *view*, never its identity.
- Codepoint identity is the Unicode scalar, not the glyph: Latin `H` U+0048 ≠ Cyrillic `Н` U+041D.
  Mixed-script text (`café Москва مرحبا`) is just a run of independent codepoint anchors.
- Anchor resolution currently lives in the **C# orchestrator** (`EtlWitness`/`SourceEntityIdConventions`);
  the native ETL path **skips anchors entirely** (`etl_ingest.c:161 /* anchors not native */`,
  gated by `NativeGrammarIngest.cs:21` `Anchor==None`). Moving it into the reusable native/SPI layer
  is the architectural goal — but it is an *investment*, not a fix; the anchoring works today.

## Data bugs found (these are bleeding NOW, invisibly — there is zero miss-telemetry)

| # | Bug | Magnitude | Status |
|---|-----|-----------|--------|
| Satellite `a`/`s` | OMW writes adjectives as `-a`; pwn30 stores satellites as `-s`; `IliMap.Key` made them different keys → silent miss | 80,923 rows / 3.02% across 33 langs; 10,693 satellite synsets (avg 0.98 synonyms vs 5–10); likely the `i12345` starvation | **FIXED** `IliMap.cs` Key collapses `a`/`s` (verified 0 offsets are both → safe) — needs build + OMW re-ingest to confirm |
| FN↔WN bridge | MapNet (24/3782 = 0.6%) and WordFrameNet (32/6564 = 0.5%) resolve vs pwn30 only; they use older WN versions (pwn30↔pwn31 share just 59 of 117,583 offsets) | DB `CORRESPONDS_TO = 0` — entire FrameNet↔WordNet bridge absent. Fix-data already ingested: CILI ships pwn15/16/17/20/21 maps as HAS_SYNSET_KEY, resolver ignores all but pwn30 | TODO (task #2) |
| Berber / 639-2 | `LanguageReference.Build` keys off 639-3 only; 639-2 collective codes & retirements not pulled (`iso-639-3_Retirements.tab` missing) | Tatoeba `ber` 693,684 sentences → `und`; `qcn` 8,069; `kzj` 5,572; others | TODO (task #4) |
| Non-blittable structs | `EtlConfigNative`/`EtlEdgeRuleNative` use `[MarshalAs] string` → managed layout (sizeof 104 vs native 112); `&cfg`/`fixed` passes a String ref where native reads `char*` | May be WHY ConceptNet/Atomic2020 native ingest are no-ops | TODO (task #6) — prereq for native anchor port |
| Probe buffer | `probe_pending` reserves `n*64` ids (`etl_ingest.c:221`) but writes true Σ(entity counts) unchecked (`:208`); OMW defs decompose into >64 nodes/row | Writes past the buffer on OMW/Wiktionary text — likely part of "gets close then crashes" | TODO (task #7) — fix = exact sizing like `StructuredGrammarIngest.cs:63-72` |

## Design realizations

**256-bit masks (POS/Sense/Deprel/relation-types) — discipline yes, representation no.**
The factoring discipline is real and working: `engine/manifest/relation_types.toml` centralizes 152
relation types (fits 256); the EDEP base-collapse (`UDDecomposer.cs:377-382`) factors UD's ~4000
enhanced deprels to base × separately-attested marker. But there is **no actual 256-bit field, no
popcount, no POS/sense mask** — types are int16 indices resolved by table scan. The satellite `s`→`ADJ`
fold (`pos_tags.toml`, locked by `PosReferenceTests.cs:20-21`) is correct for UPOS convergence; the
head/satellite distinction should be an **orthogonal flag** (a second plane), not an 18th POS value —
exactly what the masks are for. (task #5 = build the representation; the discipline already holds.)

**Content-addressing — principled, no refactor.** Two deliberate regimes, audited with zero violations:
- **content** (codepoint→…→ILI→synset→sense→frame): tree-composed (`RootId`/`laplace_content_root_id`).
- **abstract** (script, language, types): raw-blake3 of a versioned namespaced string + attestation edges.
They cohere because a codepoint id is simultaneously raw-blake3-of-UTF-8 **and** the tree-root of a
1-char string (the composer passes a lone child through unchanged) — so every content tree bottoms out
in the exact seeded codepoint atoms. Language as `OfCanonical("language:iso3")` is **correct** (a language
isn't text). Only consequence: the **native port must hash language with raw-blake3, concepts with the
tree path** — mixing them fragments ids. Cosmetic nit: `"language:iso3"` vs the versioned form others use.

**Hilbert collisions — locality, not identity.** The 4D centroid is order-independent, so anagrams
coincide: `cat`/`act`/`tac` and `dog`/`god` confirmed sharing a cell. Footprint: **176,575 colliding
cells, ~2.12M surplus entities, worst depth 499** (numeric synset-offset permutations dominate). Identity
is safe (Hash128 is order-dependent); only spatial locality is degenerate. Worth a research query +
3D viz to *navigate* the degeneracy (task #10), not necessarily eliminate it. M is the metadata/mantissa
pack → visualize X/Y/Z in 3D.

## Plan / sequencing

- **Batch A — C# resolver fixes, one build + one targeted re-ingest to verify all:**
  miss-telemetry (#1), satellite ✓ (#3 drop), FN↔WN multi-version map (#2), ISO 639-2/Berber (#4).
  Verify: i12345 gets German; `CORRESPONDS_TO > 0`; Berber no longer `und`.
- **Batch B — native lane:** probe-buffer exact sizing (#7), blittable structs (#6), then the native
  anchor port per the spec (language=raw-blake3, concept=tree; two maps loaded at session open).
- **Architecture:** build the 256-bit bit-plane representation (#5), then make the field-edge witness
  mask-driven so bespoke per-source native witnesses dissolve (#9).
- **Tooling:** the collision research query + 3D viz (#10).

Telemetry first so every other fix is measurable, not silent.
