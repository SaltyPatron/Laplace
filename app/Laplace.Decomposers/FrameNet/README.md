# FrameNet source annotation structure

Fulltext and lexical-unit annotation sets use the same `ComposeFulltextAnno` implementation. The source sentence is retained before whitespace or Unicode normalization; source offsets are zero-based Unicode character positions with an inclusive end. The parser maps those positions to UTF-16 boundaries for .NET slicing. This follows the [NLTK FrameNet reader](https://www.nltk.org/_modules/nltk/corpus/reader/framenet.html): `_handle_luannotationset_elt` converts `lbl.end` to an exclusive end, and `_annotation_ascii` slices the Python Unicode sentence with those indices. Supplementary characters count once. Combining characters remain separate source characters.

Each annotation is a canonical ordered `Hash128.Merkle(EntityTier.Document, constituents)` structure with a `PhysicalityType.ParseStructure` (8) physicality. Text branches enter the existing native content composition pipeline. The structural trajectory never enters type 1 text continuation indexes.

## Version 2 trajectory

Marker identifiers below use `Hash128.OfCanonical` with the exact name shown. Other text fields use their native content root IDs. Offset fields use `Hash128.OfCanonical("framenet/character-offset/{raw_offset}/v1")`; those identifiers retain the source positions rather than .NET positions.

| Position | Field |
| --- | --- |
| 0 | `framenet/span-annotation/schema/v2` |
| 1 | Unchanged source sentence content ID |
| 2 | Frame ID, using the existing `CategoryAnchor` contract |
| 3 | Target text content ID, reconstructed from ordered source target spans |
| 4 | Source annotation status content ID, or `none` |
| Repeated | Layer records, in source order |
| Last | `framenet/span-annotation/layers-end/v2` |

Each layer contains its marker, name content ID, rank content ID or `none`, zero or more label records in source order, then its end marker. The markers are `framenet/span-annotation/layer/v2` and `framenet/span-annotation/layer-end/v2`.

Each label contains these six IDs:

| Position | Field |
| --- | --- |
| 0 | `framenet/span-annotation/label/v2` |
| 1 | Source label name content ID |
| 2 | Raw source start offset ID, or `none` |
| 3 | Raw source inclusive end offset ID, or `none` |
| 4 | Source `itype` content ID, or `none` |
| 5 | Frame-scoped role ID for an FE label, or `none` |

The label record occupies six IDs including its marker. `none` is `framenet/span-annotation/none/v2`. An FE label with no overt span keeps its `itype` and frame-scoped role ID; it is never converted to offset zero or silently dropped. FE, GF, PT, Target and other source layers retain their separate names, ranks, label order and spans. A shared span allows later consumers to compare the layers without pretending their labels are interchangeable.

The FE role uses the existing `RoleAnchor.Id(RoleIdentityKind.FrameNet, frameId, labelName)` contract, matching frame-level definitions. Label surface text is stored separately from that role identity. An annotation does not equate an FE role with an ISA instruction slot.

## Occurrence and evidence

The canonical structure excludes file name, source sentence reference and annotation-set reference. Those identify the occurrence, using the existing `framenet/annotation-occurrence/{annotationId}/{file-UTF8-hex}/{sentence-reference-UTF8-hex}/{annotation-reference-UTF8-hex}/v1` recipe.

The source witnesses `sentence HAS_PARSE annotation` and `annotation EVOKES_FRAME frame` with the same FrameNet source and occurrence context. Equal annotations from different occurrences converge on structure while retaining distinct evidence contexts. Changing a role binding, rank, null instantiation or source span changes the annotation structure.

Version 1 target-only structures remain historical identities. Version 2 explicitly carries role structure and frame identity. Native consumers must dispatch by schema ID and preserve unknown schema status; they must not reinterpret either version as a plain text trajectory.

## Unresolved source spans (version 3)

FrameNet 1.7 also contains coordinates that do not resolve against its stored
sentence. The retained source observation on 2026-09-17 includes BNC quote labels
with an end before their start, PENN labels at or beyond the sentence end, and
`lu10365.xml` AUTO_EDITED targets outside an ASCII sentence. In sentence
`1254500`, annotation `1955991`, the source has target `19..22` for the
16-character sentence `It rang a bell .`, alongside three valid target segments.
These values are retained exactly. They are not clipped, reindexed, or treated
as resolved intervals.

A label retains its source name, nullable start/end, instantiation type, layer
and rank. `ReadResolvedSpan` still requires both bounds, ordered and within the
unchanged sentence, before extracting text. Missing bounds, reversed bounds,
and bounds outside that sentence cannot be read through that API.

An annotation containing an unresolved label uses
`framenet/span-annotation/schema/v3`. It keeps the version 2 header and layer
markers and appends a seventh ID to each label record: its explicit resolution,
from `framenet/span-resolution/{state}/v1`. States are `resolved`,
`no-span`, `incomplete`, `reversed`, and `out-of-range`. The `no-span`
state preserves an absent overt span, including a legitimate FE null
instantiation. A Target label with no span cannot resolve a target.

If any Target segment is unresolved, `TargetText` is null and `TargetSpans`
contains no resolved target. All original Target labels remain in the layer
record, including otherwise valid segments. The target slot contains the
existing `none` marker; no partial target text or target-derived
`EVOKES_FRAME` testimony is created. Raw malformed BNC/PENN annotation sets
without a Target are also retained as version 3 structures. The frame slot is
the source frame context (in an LU file, the enclosing LU frame); its presence
does not itself emit an `EVOKES_FRAME` witness.

The existing `HAS_PARSE` source witness, occurrence identity, and
`ParseStructure` physicality preserve the unresolved annotation. Fully valid
annotations keep the exact version 2 trajectory, entity identity, content
emission, and witnesses. Valid targetless annotation handling is unchanged.
No native tuple ABI, PostgreSQL schema, offset identity, or source-prior rule
changes for this representation.
