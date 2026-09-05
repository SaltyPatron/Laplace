# Explore display-label contract

The substrate entity id is identity metadata, not presentation content.

Human-facing Explore surfaces carry the exact entity id separately for navigation and use this display order for labels:

1. witnessed/canonical entity name;
2. the entity's own Unicode content when it is cheap to render directly;
3. a bounded Unicode preview for unnamed higher-tier compositions;
4. the entity-type label;
5. the explicit abstention `unrealized entity`.

A 128-bit id must never be substituted as a visible label merely because realization abstained.

## Bounded preview law

Visualization is a serving path. It must not recursively reconstruct an entire document, game, archive, or other high-tier object just to draw a node label.

`NpgsqlDisplayReads.DisplayLabelsAsync` therefore follows only the first ordered constituent from each unresolved high-tier root until it reaches a tier <= 3 chunk, then renders that bounded chunk. The full id remains available as `id_hex` and remains the click/navigation identity.

The label is truncated with PostgreSQL character semantics, not .NET UTF-16 indexing, so non-BMP Unicode is not split at the UI boundary.

## Modality behavior

A named audio/image/video/model/other non-text entity uses its name. If it has no usable textual preview, its canonical type is the friendly fallback. No modality is represented to a person by an arbitrary content hash.

This contract is enforced by `DisplayLabelContractTests` and is intended to be shared by graph, geometry-neighbor, and future visualization surfaces rather than reimplemented per component.
