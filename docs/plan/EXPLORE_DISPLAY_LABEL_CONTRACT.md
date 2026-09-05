# Explore display-label contract

## Identity is not presentation

A Laplace entity's 128-bit content id is the exact navigation/click identity. It is never a human-facing label fallback.

After a bounded result set has been elected, human-facing Explore surfaces use the installed `realize.display_label_batch` projection. The display order is:

1. canonical / witnessed entity name;
2. the entity's own Unicode surface when it is sentence-grain or shallower;
3. filename metadata when the file envelope witnesses it;
4. a containment-proven textual preview (for example `HAS_DEFINITION`), following only the first ordered trunk-to-leaf branch until sentence-or-shallower grain;
5. canonical relation, type and/or source description;
6. explicit `Unrealized entity` abstention.

`id_hex` remains alongside that label in every response and is what navigation uses.

## Projection is containment-owned

Entity `type_id` may describe an entity but does not prove which decoder/rendering projection is lawful. Higher-tier content is rendered as text only when containment/provenance witnesses a textual role such as a definition or file metadata. Audio, image, video, model and other opaque content therefore use their witnessed/canonical name or descriptive metadata rather than being guessed through a text renderer.

## Bounded serving law

A visualization is a serving path. Naming a graph node must not recursively reconstruct a document, chapter, book, game or archive.

The higher-tier text preview follows exactly one first constituent per tier via the packed trajectory (`ST_PointN(..., 1)` + mantissa metadata) until it reaches tier <= 3. It never uses `ST_DumpPoints`, `generation.trajectory_unpacked_points`, or a descendants closure for label generation. Only that elected shallow chunk is passed to the batch Unicode renderer.

This preserves the useful content preview while keeping cost proportional to result count × composition depth rather than result count × full descendant size.

## Unicode

Laplace renders actual Unicode content. The endpoint trims graph labels by Unicode text-element boundaries (`StringInfo.ParseCombiningCharacters`), not arbitrary UTF-16 code-unit slicing, so non-BMP characters and combining sequences are not cut into invalid display fragments.

## Acceptance

The regression suite must prove all of the following:

- tier-0 non-ASCII content renders exactly;
- sentence content renders exactly;
- a tier-4 definition document previews its first sentence without rendering the whole document;
- a deeper tier-5 container reaches the same preview by one bounded trunk-to-leaf spine;
- duplicate input ids preserve positional alignment;
- a missing/opaque entity returns friendly metadata or explicit abstention, never its content hash;
- Explore graph/entity/geometry-neighbor/member/container surfaces consume the shared display projection rather than maintaining local fallback laws.

A live post-deploy receipt should reproduce a mixed-tier consensus web such as the CILI concept graph at 8 hops / fanout 16 / capacity 1024 and verify that visible node labels are names, Unicode previews, descriptive labels, or explicit abstentions while the exact hash remains available only as identity metadata.
