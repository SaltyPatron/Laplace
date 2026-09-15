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
