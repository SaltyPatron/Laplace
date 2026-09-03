# Dataset estate modernization

Date: 2026-09-03

Owner: #1403

Execution issues: #1471, #1472, #1473, #1043, #372, #605, #806

## Outcome

`/vault/Data` becomes a release-pinned, checksum-verifiable source estate. A person
starting with no session history must be able to tell:

1. what each directory is;
2. which upstream release it represents;
3. whether it is current, historical, superseded, equivalent packaging, excluded,
   unsupported, or absent;
4. which physical artifacts belong to the selected source;
5. which license and citation govern them;
6. which old path may be removed and what verified replacement permits removal; and
7. which GitHub issue owns the remaining work.

This pass establishes the data authority. It deliberately does not repair decomposers.
Decomposer fidelity is evaluated afterward against the immutable artifact hashes.

The machine-readable companion is
[`docs/source-estate.tsv`](../source-estate.tsv).

## Staging snapshot

`/vault/Data/.refresh-20260903` is a non-active holding area. Its current download
inventory contains 49 artifacts totaling 7,510,916,222 bytes. All 49 replay successfully
through `sha256sum -c DOWNLOADS.sha256`, and the manifest's file set exactly matches the
non-extracted files in the staging tree.

| Artifact | SHA-256 |
|---|---|
| `STAGING_MANIFEST.tsv` | `f994cbd838fd168e9fdbb8d9276f3030435378d960f3a154d63584a3f9b69643` |
| `DOWNLOADS.sha256` | `2d7ea093d9f6e1074aa6ed931fa7578e486fe2352b95d8e5efaaa3a5446cf9c7` |

This snapshot proves local byte identity only. It does not promote partial downloads,
unresolved licenses, unenumerated upstream families, or unvalidated extracted trees to
`admitted`.

## Disposition vocabulary

Use only these states. Do not invent softer synonyms.

| State | Meaning |
|---|---|
| `admitted` | Selected authoritative artifact; retain and ingest once. |
| `current-verify` | Likely current, but release/hash/license reconciliation remains. |
| `staged` | Downloaded outside the active path; validation or installation remains. |
| `equivalent-packaging` | Same source rows in another container/extracted form; retain if useful but never count as another witness. |
| `superseded` | Replaced for active use; delete after the named replacement passes its gate. |
| `historical` | Intentionally retained for time/version compatibility, never presented as current. |
| `excluded-with-reason` | Known artifact deliberately outside the selected estate. |
| `unsupported-with-why-not` | Desired artifact exists but rights, access, integrity, format, or capacity prevents admission. |
| `absent` | Required or proposed artifact is not installed. |

`latest`, a directory mtime, a Git branch name, and a mutable worktree are not release
identities.

## Order of work

Follow this order for every source. A later step does not excuse skipping an earlier one.

1. Read the upstream release page, directory listing, dataset card, license, and citation.
2. Enumerate the entire upstream artifact family, including sidecars and alternate
   packaging.
3. Mark every artifact with one disposition.
4. Download to `/vault/Data/.refresh-20260903/<source>` without overwriting the active
   installation.
5. Record URL, release/tag/commit, fetch UTC, bytes, SHA-256, upstream checksum when one
   exists, media type, license, citation, and selected role.
6. Test the container completely (`gzip -t`, `bzip2 -t`, `xz -t`, `unzip -t`, or archive
   listing) and parse the native data format far enough to prove it is not an error page or
   truncated payload.
7. Extract into a release-named directory. Archive and extracted tree are
   `equivalent-packaging`.
8. Generate a sorted file manifest for the extracted tree and reconcile it to the archive
   member list.
9. Rename the old active directory into the refresh area's `superseded/` holding area,
   install the validated new tree, and rerun the checks through the final path.
10. Write a removal receipt containing old path, file count, byte count, reason,
    replacement artifact hashes, and verification result.
11. Delete only the exact superseded target named by the receipt.
12. Update `source-estate.tsv`, the owning issue, and the global installed manifest.

## Required installed layout

Each top-level dataset uses this shape unless the upstream release itself requires another
shape:

```text
/vault/Data/<Source>/
  PROVENANCE.md
  MANIFEST.tsv
  MANIFEST.sha256
  release/                 # immutable downloaded containers and upstream checksum files
  <release-name>/          # extracted native files, if extraction is useful
  derived/                 # optional transforms; never another source witness
```

`MANIFEST.tsv` has one row per physical artifact with these columns:

```text
source release artifact relative_path disposition upstream_url fetched_at_utc
bytes sha256 upstream_checksum media_type license citation language split
annotation_origin notes
```

Rows may omit inapplicable values but not the row. Directory manifests are sorted by raw
relative-path bytes. A manifest must distinguish source archives, extracted equivalents,
and transformations.

## Semantic and lexical authority — #1471

### OMW

Observed defect:

- `/vault/Data/OMW` and `/vault/Data/omw` are duplicate Git worktrees.
- Both point at the current repository commit but contain legacy OMW tab payloads rather
  than the formal OMW 2.0 release.
- Both are locally damaged: 1,417 tracked line-ending modifications, one tracked deletion,
  and one untracked checkpoint.

Replacement:

- OMW 2.0 combined WN-LMF archive, 55,846,636 bytes, SHA-256
  `c369a2ad773a31e182ac4cc753132fa7c31ad423586d6783bacce08090cb8d7d`.
- OMW 2.0 `index.toml`, 9,298 bytes, SHA-256
  `63fc6828e577647a14b21f429d589f240057fc0c668f684a40af7bdcc4c8c186`.
- The combined archive has 32 current language lexicons with their own licenses and
  citations. OMW's individually released historical English 1.5-3.1 compatibility
  packages are explicitly historical, not missing current languages.

Final state: one `/vault/Data/OMW`; no lowercase duplicate; no `.git`; no legacy tabs
presented as OMW 2.0.

### English WordNet and CILI

- Add Open English WordNet 2025+, SHA-256
  `31f4af16c54b532fd5484d4cc33aee588a31bb5b70683ae8197842fde5b586bc`.
- Keep Princeton WordNet 3.0 as a compatibility coordinate source, with its own identity.
- Replace the modified/25-commit-behind CILI checkout with pristine commit
  `a895d7ecb18019dda3443f98901e59d81ce8722b` and retain the immutable source archive.

### Legacy mapping retirement

No maintained one-for-one successor exists for MapNet 0.1, WordFrameNet/XWFN, or Predicate
Matrix 1.3. Do not rename one modern source as their exact successor. Replace their active
coverage with a set:

| Old active source | Why it is not current authority | Current coverage |
|---|---|---|
| MapNet 0.1 | 2009; FrameNet 1.3; WordNet 1.6; automatic mapping; restrictive redistribution terms | FrameBase 2.0 WordNet links, current native FrameNet/WordNet, SemLink 2.0 |
| WordFrameNet/XWFN | unversioned flat files; no local provenance/license; historical synset offsets | FrameBase 2.0, SemLink 2.0, OEWN/OMW identity |
| Predicate Matrix 1.3 | finished 2016 release; no maintained drop-in successor | SemLink 2.0, VerbAtlas 1.1, FrameBase 2.0, current FrameNet/VerbNet/PropBank/WordNet |

Preserve the old sources' existence, schema, license, size, and removal receipt in the
inventory. Remove the payloads only after each distinct field needed from them is either
covered by an admitted source or explicitly declared historical-only.

### Sources to admit or verify

- SemLink 2.0: verify the current upstream commit and archive; keep its VN/PB/FN/WN mapping
  identities and license limitations.
- FrameBase 2.0: enumerate both `schema/` and `instances/`. Select core, metaschema, DBpedia
  schemas, lemon annotations, Lexvo and WordNet links, clusters, manual extensions, and
  reification rules. Record unavailable instance dumps as unavailable, never silently
  absent.
- VerbAtlas 1.1: retain official ZIP and every mapping/table/license member.
- FrameNet 1.7, VerbNet 3.4 and PropBank 3.4 remain current source families until their own
  authorities publish replacements.

## Mutable corpora — #1472

### Universal Dependencies

Replace v2.17 with v2.18. The official treebank archive is 684,056,893 bytes with MD5
`e9bfd544a48eac63ea3bb41e80c78813`. Inventory all 2,501 extracted files; each treebank's
README/license and each `.conllu` file remain distinct artifacts.

### Tatoeba

The selected 2026-08-29 graph includes base sentences, links, detailed sentences, CC0
sentences, list membership, tags, detailed tags, transcriptions, user-language metadata,
sentence-author mappings, tag metadata, audio linkage, and the stable English audio ZIP.

The upstream `per_language/` tree and uncompressed duplicates are equivalent packaging.
Comments, contributions, wall, WWWJDIC, and Japanese-specific indices require explicit
include/exclude decisions. The 4,116,471,982-byte English audio ZIP has upstream
Last-Modified 2017-11-29; it is stable, not a stale January 2026 build.

### Wiktionary/Wiktextract

Admit the 2026-08-28 raw Wiktextract JSONL gzip (2,826,623,319 bytes) derived from the
2026-08-05 dump. Remove the older raw extraction after CRC, JSONL schema sampling, record
count, and hash verification. Remove the deprecated overlapping Kaikki per-language
postprocessed download. Raw Wiktextract itself is not deprecated.

### ATOMIC

Keep Atomic2020 as the latest human-authored ATOMIC release. Add ATOMIC10x as a separately
identified machine-generated extension: 1,535,887,049 bytes; MD5
`027c1c063750bbe8ef65000ccccf0960`. Do not replace or merge away Atomic2020 provenance.

### TWIC and Lichess openings

- Add TWIC 1651-1660 from the numbered official ZIPs and retain each ZIP/PGN pair as
  equivalent packaging.
- Replace the active Lichess openings snapshot with pinned commit
  `4b8622759e7ae6f93f011cc6c83a3823401ab45e`; retain source archive, TSVs, license, and
  commit receipt.

## Gutenberg mini-collection — #1472 and #806

A recursive full-file marker scan, not a depth-limited sample, classified all 204 files
formerly under `/vault/Data/test-data/text`:

- 195 Project Gutenberg texts moved to `/vault/Data/ProjectGutenberg/text`;
- 9 real fixtures remain in `/vault/Data/test-data/text`;
- the Pierre-Simon-Laplace acquisition subtree remains under `ProjectGutenberg`;
- `/vault/Data/.refresh-20260903/project-gutenberg-relocation.tsv` records SHA-256, old
  path, and new path for all 195 moves; all destination hashes verify.

This is a curated mini-collection. Never describe it as a complete Gutenberg mirror.
Inventory ebook ID, title, author, language, release date, update date, credits, and raw
header boundary where present; absence is recorded, not guessed from filenames.

The collection already contains a useful source-separability example: Webster and
Britannica attest Paris as France's capital, while *Alice in Wonderland* places an
explicitly self-corrected false geography recitation in fictional dialogue. Preserve all
three documents and their context.

## Unicode authority — #1043

`/vault/Data/UCD` is the only candidate authority. Reconcile its complete FTP-style
Unicode 17 graph, archives, extracted views, checksums, version markers, licenses,
conformance files, Unihan, emoji, security, normalization, and collation data against the
official release listing.

`/vault/Data/Unicode.BAD-DONOTUSE` is a 37 GiB abandoned partial extraction/mirror, not a
release. After UCD passes reconciliation, record its file/byte counts and delete that exact
tree. Unicode 18.0 is scheduled for 2026-09-16; on this audit date it is future, not missing.

## Chess tablebases — #605

`/vault/Data/Games/Chess/syzygy/3-4-5` is verified complete against the Sesse manifest:

- 290 files: 145 WDL (`.rtbw`) plus 145 DTZ (`.rtbz`);
- 983,957,920 bytes;
- every MD5 passes.

Here “3-5 men” includes both kings and therefore means one to three non-king pieces plus
kings. King versus king is automatically drawn. Six-men is absent, approximately 149 GB
and feasible within the currently free 1.8 TiB. Seven-men is approximately 18.4 TB and is
`excluded-with-reason: capacity`.

## Encyclopedic/geographic estate — #372

GeoNames is useful but incomplete and too structured to be the sole inference proof.
Select its complete geographic evidence graph: allCountries, alternateNamesV2, hierarchy,
country/admin/feature/language/time-zone metadata, and selected shapes. The deprecated
`alternateNames.zip` is excluded; country ZIPs are equivalent packaging.

Also acquire:

- current pinned English Wikipedia pages-articles multistream XML, index, status,
  checksums, siteinfo, and license metadata;
- current pinned full Wikidata statements with ranks, qualifiers and references; a truthy
  RDF dump may be retained as explicit derived/equivalent packaging, not substituted
  silently;
- current Natural Earth Admin-0 countries and populated places as an independent curated
  public-domain witness.

All must work offline. A live API query, hand-built capitals file, or prompt-specific
fixture does not satisfy the dataset requirement.

## Harm, toxicity, bias contrast, and prosocial data — #1473

The selected core is Civil Comments, ToxiGen, Measuring Hate Speech, Social Bias Frames,
ProsocialDialog, Social Chemistry 101, RealToxicityPrompts, and HateCheck. Add authoritative
Multilingual HateCheck, SGHateCheck, and XSTest after license/artifact verification. BBQ,
CrowS-Pairs, and StereoSet are evaluation-only sources if admitted.

Dataset records must preserve continuous scores, label dimensions, target/group fields,
annotation origin, annotator disagreement, rationale spans, counter-speech, negation,
quotation, reclaimed usage, and benign identity mentions. Identity terms and groups are
not negative data. Harmful assertions, intents, or behaviors are the negative evidence.

HateXplain remains excluded pending explicit dataset redistribution terms. A repository's
code license is not assumed to license its data. Kaggle click-through datasets are external
prerequisites, not files to scrape around access terms.

## Known-valid sources that still need release manifests

Do not delete these merely because other sources are stale:

- ConceptNet 5.7;
- OpenSubtitles v2024;
- FrameNet 1.7;
- VerbNet 3.4;
- PropBank 3.4;
- Princeton WordNet 3.0 compatibility files;
- ISO/IANA/CLDR/Glottolog estate;
- Tree-sitter grammars;
- code-authority repositories;
- the verified Syzygy 3-5 set.

Each still requires a pinned release/commit, URL, license, selected artifact graph, hashes,
and explicit equivalent/excluded packaging in the global manifest.

## Global completion gate

- [ ] Every row in `docs/source-estate.tsv` has a final disposition and issue.
- [ ] Every admitted source has `PROVENANCE.md`, `MANIFEST.tsv`, and `MANIFEST.sha256`.
- [ ] Archive integrity and native schema/row-count checks pass through final installed
      paths.
- [ ] No active dataset contains `.git`, editor state, checkpoints, partial downloads, or
      an HTML error page disguised as data.
- [ ] No current source exists twice under case variants or alternate packaging that could
      double-vote.
- [ ] Every deletion has a removal receipt naming its validated replacement.
- [ ] Every known upstream sidecar has an explicit disposition.
- [ ] GitHub issue bodies and this table agree on versions, hashes, paths, and ownership.
- [ ] Only after this gate passes does #1153 evaluate decomposer fidelity.
