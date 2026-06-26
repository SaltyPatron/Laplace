## Bucket: A8_decomp_lang (ISO/UD/Wiktionary/Tatoeba/Atomic2020/OpenSubtitles/Unicode decomposers + tests)

### Files read (all 30 bucket files read IN FULL)
- [x] app/Laplace.Decomposers.Atomic2020/Atomic2020Decomposer.cs
- [x] app/Laplace.Decomposers.Atomic2020/Atomic2020EtlRegistration.cs
- [x] app/Laplace.Decomposers.Atomic2020/Atomic2020GrammarWitness.cs
- [x] app/Laplace.Decomposers.Atomic2020/Laplace.Decomposers.Atomic2020.csproj
- [x] app/Laplace.Decomposers.ISO/ISODecomposer.cs
- [x] app/Laplace.Decomposers.ISO/LanguageGraph.cs
- [x] app/Laplace.Decomposers.ISO/Laplace.Decomposers.ISO.csproj
- [x] app/Laplace.Decomposers.OpenSubtitles.Tests/Laplace.Decomposers.OpenSubtitles.Tests.csproj
- [x] app/Laplace.Decomposers.OpenSubtitles.Tests/OpenSubtitlesDecomposerTests.cs
- [x] app/Laplace.Decomposers.OpenSubtitles/Laplace.Decomposers.OpenSubtitles.csproj
- [x] app/Laplace.Decomposers.OpenSubtitles/OpenSubtitlesDecomposer.cs
- [x] app/Laplace.Decomposers.OpenSubtitles/OpenSubtitlesZipIngest.cs
- [x] app/Laplace.Decomposers.Tatoeba/Laplace.Decomposers.Tatoeba.csproj
- [x] app/Laplace.Decomposers.Tatoeba/TatoebaDecomposer.cs
- [x] app/Laplace.Decomposers.Tatoeba/TatoebaGrammarWitness.cs
- [x] app/Laplace.Decomposers.Tatoeba/TatoebaRowFilter.cs
- [x] app/Laplace.Decomposers.UD/Laplace.Decomposers.UD.csproj
- [x] app/Laplace.Decomposers.UD/UDDecomposer.cs
- [x] app/Laplace.Decomposers.Unicode.Tests/AssemblyParallelization.cs
- [x] app/Laplace.Decomposers.Unicode.Tests/Laplace.Decomposers.Unicode.Tests.csproj
- [x] app/Laplace.Decomposers.Unicode.Tests/UnicodeDecomposerTests.cs
- [x] app/Laplace.Decomposers.Unicode.Tests/UnicodeSeedIntegrationTests.cs
- [x] app/Laplace.Decomposers.Unicode/Laplace.Decomposers.Unicode.csproj
- [x] app/Laplace.Decomposers.Unicode/UcdProperties.cs
- [x] app/Laplace.Decomposers.Unicode/UnicodeDecomposer.cs
- [x] app/Laplace.Decomposers.Wiktionary/Laplace.Decomposers.Wiktionary.csproj
- [x] app/Laplace.Decomposers.Wiktionary/WiktionaryDecomposer.cs
- [x] app/Laplace.Decomposers.Wiktionary/WiktionaryEtlRegistration.cs
- [x] app/Laplace.Decomposers.Wiktionary/WiktionaryGrammarWitness.cs
- [x] app/Laplace.Decomposers.Wiktionary/WiktionaryJsonFilter.cs

Cross-checked helpers (outside bucket, read to verify claims): `StructuredGrammarIngest.cs`,
`SourceEntityIdConventions.cs` (TatoebaSentence), `LanguageEntityId.cs`, `LanguageFilter.cs`,
`EntityTier.cs`.

---

### FINDINGS

#### F1 — TIER USED AS KIND (`EntityTier.Vocabulary = 5`), pervasive across the whole bucket
- FILE:LINE: `app/Laplace.SubstrateCRUD/EntityTier.cs:20` (`public const byte Vocabulary = 5;`) consumed by:
  - ISODecomposer.cs:77,88,103,111,119,145,146,159,165,185,186,197,203,232 (every language/code/script/variant/classifier entity)
  - Atomic2020Decomposer.cs:63,64 (NoneId + split enums)
  - TatoebaGrammarWitness.cs:52,53,77,78 (sentence-ref + language)
  - UDDecomposer.cs:281 (language) ; OpenSubtitlesZipIngest.cs:146,147 (languages)
  - UnicodeDecomposer.cs:89,90,92,180,181,187 + UcdProperties.cs:188–208 (all UCD classifiers/encodings/roles)
- SEVERITY: CRITICAL
- CATEGORY: invention-violation (invariant 3 — tier = compositional depth ONLY, never a category)
- CLAIM: Every "vocabulary"-class entity is stamped at a fixed tier `5` regardless of compositional
  depth. Languages, ISO codes, UCD scripts/blocks/ages, UD feature values, Tatoeba sentence-refs and
  ATOMIC schema sentinels all get `tier=5`. This is exactly the "encode a category in the depth axis"
  violation; the project's own CLAUDE.md names `EntityTier.Vocabulary = 5` as "the live violation."
  Kind belongs in `type_id`/physicality/trust, not in tier. (Codepoints, by contrast, correctly use
  `tier:0` — UnicodeDecomposer.cs:285,441.)
- VERIFIED: read the constant definition; traced every `EntityTier.Vocabulary` call site in the bucket.
- CONFIDENCE: high

#### F2 — Tatoeba: shared mutable `HashSet<long>` written from parallel compose workers (data race)
- FILE:LINE: TatoebaGrammarWitness.cs:65 (`_allowedIds?.Add(id);`), allocated TatoebaDecomposer.cs:56,
  one witness instance shared at TatoebaDecomposer.cs:60 → StructuredGrammarIngest.IngestFileAsync.
- SEVERITY: HIGH
- CATEGORY: correctness (race condition)
- CLAIM: When a language filter is active, `allowedSentenceIds` is a plain `HashSet<long>` mutated by
  `_allowedIds.Add(id)` inside `WalkSentence`. The single witness instance is invoked concurrently by
  the parallel compose consumers in `IngestFileParallelAsync` (default compose workers = min(4,…) per
  `ResolveComposeWorkers`, StructuredGrammarIngest.cs:114-125; `DrainAndWalk`/`ComposeRow` all call the
  shared `witness.WalkRow`). Concurrent `HashSet.Add` is undefined behavior: lost entries, corrupted
  internal buckets, possible infinite loop/crash. The links pass then reads this set
  (TatoebaRowFilter.MatchesLinkFilter) to gate `IS_TRANSLATION_OF`, so corruption silently drops valid
  translation edges. The class comment (TatoebaDecomposer.cs:48-55) defends the buffer as "one
  intentional cross-pass state" but never addresses within-pass thread-safety.
- VERIFIED: witness is a single instance (TatoebaDecomposer.cs:60); confirmed parallel consumers share
  the witness param and call WalkRow concurrently (StructuredGrammarIngest.cs:419-516); `_allowedIds`
  is `HashSet<long>` not concurrent. Only triggers with an active language filter; unfiltered runs leave
  `_allowedIds` null (no-op).
- CONFIDENCE: high (race), high (parallel default unless LAPLACE_INGEST_COMPOSE_WORKERS=1)

#### F3 — Tatoeba: entity identity derived from the source's sentence id (provenance in the id)
- FILE:LINE: SourceEntityIdConventions.cs:201-202 `TatoebaSentence(long) => OfCanonical($"tatoeba/sentence/{id}")`;
  used TatoebaGrammarWitness.cs:49,75,76.
- SEVERITY: HIGH
- CATEGORY: invention-violation (invariant 1 — NO source/position/index/id in any entity id)
- CLAIM: The sentence-reference entity id is a function of Tatoeba's numeric sentence id, i.e. source
  provenance baked into the id namespace. Worse for convergence: `IS_TRANSLATION_OF` is emitted between
  these provenance-keyed ref nodes (extId↔extId, WalkLink lines 75-80), NOT between the content roots.
  The actual sentence content is content-addressed separately and only linked by `HAS_EXTERNAL_ID`
  (line 58-59). Contrast OpenSubtitles, which emits `IS_TRANSLATION_OF` directly between content roots
  idA↔idB (OpenSubtitlesZipIngest.cs:71-72) — the correct content-addressed convergence shape. Two
  identical sentences from different sources will not converge their translation edges under the Tatoeba
  scheme. This is the previously-flagged "Tatoeba keying entity on source id" pattern.
- VERIFIED: read TatoebaSentence definition; traced subject/object of the two attestation types.
- CONFIDENCE: high

#### F4 — UD: default run is UNSCOPED — ingests every treebank (all languages, ~4.3 GB)
- FILE:LINE: UDDecomposer.cs:556-570 (`EffectiveLanguages` → `LanguageFilter.ForSource("UDDecomposer")`),
  LanguageFilter.cs:17-22 (`ForSource` returns null when no env var set).
- SEVERITY: MEDIUM
- CATEGORY: invention-violation / perf (charter: "UD must be scoped")
- CLAIM: With no `LAPLACE_LANG_UDDecomposer` (or global) env var, `EffectiveLanguages` returns null and
  `ListTreebankFiles` enumerates and ingests EVERY `*.conllu` under `ud-treebanks-v2.17` (all languages).
  The code logs "no language filter — ingesting all N treebank files (multilingual)" and the comment
  (lines 566-569) explicitly endorses full multilingual as "the intended omniglottal path, not an
  accident to guard against." So the default behavior contradicts the audit's scoping requirement; it is
  gated only by an env var that defaults off. Not a silent data drop (it ingests more, not less), but a
  default-unscoped 4.3 GB pull on a box meant to run on a Pi.
- VERIFIED: traced `ForSource` (null unless env), `EffectiveLanguages`, `ListTreebankFiles`.
- CONFIDENCE: high

#### F5 — UD: 3–4 near-duplicate emit/orchestration code paths in one file (fork)
- FILE:LINE: UDDecomposer.cs:88-152 (single-thread, reader==null AND reader!=null branches) and
  155-238 (multi-worker, reader==null AND reader!=null branches); same `EmitSentence` driven four ways.
- SEVERITY: MEDIUM
- CATEGORY: fork / altitude (invariant 8 — converge, don't fork)
- CLAIM: The decomposer carries four parallel orchestration lanes (serial/parallel × reader-present/absent),
  each re-implementing the batch/yield/period-boundary loop. The `reader==null` "byte-for-byte original
  one-pass" lanes are a retained legacy path alongside the two-phase containment lanes. This is the
  "flag-gated parallel lanes" disease the project warns against; it should converge to one path.
- VERIFIED: read all four branches.
- CONFIDENCE: high (that the duplication exists); med (on whether reader==null is still reachable in prod)

#### F6 — Dual ingest registration: bespoke decomposer AND generic EtlWitnessFactory for same source
- FILE:LINE: Atomic2020EtlRegistration.cs:14-17 and WiktionaryEtlRegistration.cs:12-16 register witnesses
  with `EtlWitnessFactory`, while Atomic2020Decomposer/WiktionaryDecomposer also implement their own
  `DecomposeAsync`/`StreamTriplesAsync` driving `StructuredGrammarIngest` directly.
- SEVERITY: MEDIUM
- CATEGORY: fork
- CLAIM: Two code paths can run the same source — the per-source `IDecomposer` and a generic
  `EtlDecomposer` that looks up the registered witness. The registration doc-comments say the goal is "the
  one EtlDecomposer drives the real Transform through the single StructuredGrammarIngest pipeline," which
  implies the bespoke `DecomposeAsync` is the lane meant to be retired. Until one is deleted this is a
  convergence violation (which lane actually executes is decided by the runner, outside this bucket).
- VERIFIED: read both registration files + both decomposers.
- CONFIDENCE: med (existence high; which-runs unverified without runner)

#### F7 — ISO 639: invented `substrate/iso639/...` namespaces for scope/type/variant enums
- FILE:LINE: ISODecomposer.cs:109,117,196 (`substrate/iso639/scope/{Scope}/v1`,
  `substrate/iso639/type/{Type}/v1`, `substrate/iso639/variant/{subtag}/v1`); LanguageGraph.cs:11-12.
- SEVERITY: LOW
- CATEGORY: invention-violation (invariant 6 — anchor on real external inventory ids, not invented
  `substrate/...` namespace)
- CLAIM: ISO 639-3 scope (Individual/Macrolanguage/Special) and type (Living/Extinct/…) enum values are
  anchored under an invented `substrate/iso639/...` namespace rather than the standard's own
  identifiers. Minor — these are small fixed schema enums — but it is the invented-namespace pattern the
  invariant warns against. (Languages themselves are correctly anchored: `LanguageEntityId.FromIso639_3`
  = `language:{iso3}`, the real ISO code — invariant 6 satisfied for the language entities.)
- VERIFIED: read the id construction; read LanguageEntityId.cs.
- CONFIDENCE: high (it's an invented namespace); low (on severity — defensible as schema)

#### F8 — Test coverage gap: Atomic2020, ISO, Tatoeba, UD, Wiktionary have no test project in bucket
- SEVERITY: LOW
- CATEGORY: other (coverage)
- CLAIM: Only Unicode and OpenSubtitles ship tests. The five sources with the highest historical
  "scaffolded-to-imagined-format / silent-drop" risk (per project memory) are untested here. (Format
  parsing for all five was manually verified against the real formats — see Positives — so this is a
  regression-risk gap, not a present silent-drop.)
- VERIFIED: bucket file listing; only two `*.Tests` projects present.
- CONFIDENCE: high

#### F9 — UD comment asserts "ON CONFLICT dedups" — contradicts invariant 7
- FILE:LINE: UDDecomposer.cs:22-23 ("content-addressed + ON CONFLICT dedups").
- SEVERITY: INFO
- CATEGORY: disparagement/claim (verify, don't trust)
- CLAIM: The comment states the apply relies on `ON CONFLICT`, which invariant 7 explicitly forbids
  ("NO ON CONFLICT, NO per-row anti-join"). Whether the actual apply path uses ON CONFLICT is in the
  writer/SPI layer (out of this bucket) — flagging the comment as a claim to verify there, and as a
  doc/code-intent defect if the real path does honor the top-down dedup.
- VERIFIED: read the comment; apply path not in this bucket.
- CONFIDENCE: med

---

### POSITIVES (verified, counter to general suspicion)

- **Wiktionary STREAMS the 9 GB file correctly (invariant 7 satisfied).** WiktionaryDecomposer.DecomposeAsync
  routes through `StructuredGrammarIngest.IngestFileAsync` (modality "json"), which reads via a
  `FileStream` with a 1–4 MB buffer and a native row iterator (`FeedRawLines`), parsing ONE JSONL record
  at a time to its own AST, walking it, then disposing the AST (StructuredGrammarIngest.cs:163-321 serial,
  367-541 parallel). The file is never held whole; the AST is never materialized for the whole corpus.
  The whole-file `IngestJsonDocumentAsync` (line 679, `File.ReadAllBytesAsync`) exists but Wiktionary does
  NOT call it. The pre-filter `WiktionaryJsonFilter.MatchesLanguageFilter` uses a bounded `Utf8JsonReader`
  on a single line. VERIFIED by reading both ingest paths. CONFIDENCE: high.

- **All seven sources parse REAL formats — no imagined-format scaffolding, no silent field-drop.** ISO
  639-3 `.tab` (7 cols), macrolanguages/retirements/name-index `.tab`, IANA subtag-registry block format,
  CLDR supplementalData.xml; CoNLL-U 10-col incl. MWT `id-id` and empty-node `id.x`; Tatoeba TSV
  sentences/links (tab-separated despite `.csv` extension — correct for Tatoeba); ATOMIC head/rel/tail
  TSV; wiktextract JSONL schema (word/lang_code/pos/senses[].glosses/examples/sounds[].ipa/forms/
  etymology_templates.args); OPUS OpenSubtitles `*.txt.zip` paired moses files; full UCD layout
  (UnicodeData.txt + Scripts/Blocks/DerivedAge/LineBreak/EastAsianWidth/extracted DerivedJoiningType+
  NumericType/BidiMirroring/emoji-data/NameAliases/confusables + ucdxml + uca allkeys). VERIFIED field
  indices against each real format. CONFIDENCE: high.

- **Language entities anchor on the real ISO 639 code** (`language:{iso3}`, LanguageEntityId.cs) — invariant 6
  satisfied for languages. Relation types route through `RelationTypeRegistry`/`PosReference` (UPOS)
  canonical inventory names, not invented per-source namespaces.

- **OpenSubtitles models translation correctly** (content-root ↔ content-root IS_TRANSLATION_OF) — the
  content-addressed convergence shape (contrast F3).

- **Tests are real, not fake.** UnicodeSeedIntegrationTests drops+recreates a fresh DB and asserts real
  work (>1.1M resolvable codepoints, content_count ≥ total, S³ radius=1.0 on U+0041) — the correct
  fresh-DB pattern, not "rows_new=0 on a populated DB." UnicodeDecomposerTests asserts 1,114,112 codepoint
  entities at tier 0 on the S³ surface. OpenSubtitlesDecomposerTests asserts real edge/content counts on a
  fixture zip and pair-allowlist filtering. No asserts-nothing / mocked-core tests found.

---

### Bucket summary
- CRITICAL: 1 (F1 tier-as-kind, pervasive)
- HIGH: 2 (F2 Tatoeba HashSet race; F3 Tatoeba identity-from-provenance)
- MEDIUM: 3 (F4 UD default-unscoped; F5 UD duplicated lanes; F6 dual registration fork)
- LOW: 2 (F7 ISO invented enum namespace; F8 test-coverage gap)
- INFO: 1 (F9 UD "ON CONFLICT" comment vs invariant 7)

**Single worst issue:** F1 — `EntityTier.Vocabulary = 5` stamps a *kind* into the *depth* axis on
essentially every entity these seven language decomposers emit. It is the project's own named live
invariant-3 violation and it is bucket-wide, so it corrupts the tier semantics for the entire
language/convergence-index layer. (Runner-up: F2, a genuine data race that silently drops Tatoeba
translation edges on any language-filtered run.)
