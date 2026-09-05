# Explore display-label contract

## Identity is not presentation

A Laplace entity's 128-bit content id is the exact navigation/click identity. Human-facing realization should prefer meaningful names, text, notation, and descriptive metadata, but **failure to produce a friendly label must never hide the identity that is known**.

After a bounded result set has been elected, human-facing Explore surfaces use the installed `realize.display_label_batch` projection. The display order is:

1. canonical / witnessed entity name;
2. domain-appropriate notation or title when that realization is lawfully available (for example chess SAN/UCI, a named opening, a file/document title, or another typed notation);
3. the entity's own Unicode surface when it is sentence-grain or shallower;
4. filename/title metadata when the containing artifact witnesses it;
5. a containment-proven textual preview (for example `HAS_DEFINITION`), following only the first ordered trunk-to-leaf branch until sentence-or-shallower grain;
6. canonical relation, type, source, or other lawful intrinsic descriptive metadata;
7. a typed identity fallback such as `<entity type> · <short canonical id>`; if type realization is unavailable, the shortened canonical id itself is the minimum steady-state label.

`id_hex` remains alongside the friendly label in every response and is what navigation uses. The full canonical id remains available in details/raw identity views even when a friendlier label is shown.

`Unrealized entity` / `Unresolved entity` is a diagnostic status or reason, **not an entity label replacement**. It may be shown transiently while realization is loading or alongside an explanation of why a preferred realization failed, but it must never be the only steady-state display for an entity whose canonical id is known.

This is deliberately different from inventing a name. The fallback says what is actually known and exposes the identity; it does not fabricate semantic meaning.

## Projection is containment-owned

Entity `type_id` may describe an entity but does not prove which decoder/rendering projection is lawful. Higher-tier content is rendered as text only when containment/provenance witnesses a textual role such as a definition or file metadata.

Audio, image, video, model and other non-text content therefore do not get guessed through a text renderer, but they still have useful realizations. Use lawful intrinsic/provider metadata plus identity, for example:

```text
Audio sample · 3.24 s · WAV · 44.1 kHz · stereo · a1b2c3d4…
Image · 1920×1080 · PNG · e5f60718…
Model artifact · safetensors · 2.1 GiB · 9abc0123…
```

Only fields actually witnessed by the substrate/provider may appear. A missing transcript does not justify `Unrealized entity`; it means no transcript is shown. If a transcript/title/caption is separately witnessed, it can participate as another realization without changing the canonical content identity.

If even intrinsic metadata is sparse, `<type> · <short id>` is still more truthful and useful than a generic placeholder.

## Bounded serving law

A visualization is a serving path. Naming a graph node must not recursively reconstruct a document, chapter, book, game or archive.

The higher-tier text preview follows exactly one first constituent per tier via the packed trajectory (`ST_PointN(..., 1)` + mantissa metadata) until it reaches tier <= 3. It never uses `ST_DumpPoints`, `generation.trajectory_unpacked_points`, or a descendants closure for label generation. Only that elected shallow chunk is passed to the batch Unicode renderer.

This preserves the useful content preview while keeping cost proportional to result count × composition depth rather than result count × full descendant size.

The identity fallback is also bounded: formatting a type plus shortened id must not trigger a recursive reconstruction when no lawful shallow realization was found.

## Unicode

Laplace renders actual Unicode content. The endpoint trims graph labels by Unicode text-element boundaries (`StringInfo.ParseCombiningCharacters`), not arbitrary UTF-16 code-unit slicing, so non-BMP characters and combining sequences are not cut into invalid display fragments.

## Cross-surface law

Explore graph, entity detail, geometry-neighbor, member/container, trajectory, Matchup/Connection, and any other entity-world surface consume the same realization projection/fallback law. No renderer may replace a failed label with its own private `Unrealized entity` placeholder, drop the id, or invent a semantic caption.

Changing language, theme, layout, or friendly label changes presentation only. It never remints canonical identity or changes graph topology/evidence.

## Acceptance

The regression suite must prove all of the following:

- tier-0 non-ASCII content renders exactly;
- sentence content renders exactly;
- a tier-4 definition document previews its first sentence without rendering the whole document;
- a deeper tier-5 container reaches the same preview by one bounded trunk-to-leaf spine;
- duplicate input ids preserve positional alignment;
- an entity with a missing preferred name returns lawful descriptive metadata and/or `<type> · <short id>` rather than a generic `Unrealized entity` label;
- an entity with no friendly metadata still exposes its short canonical id as the minimum steady-state display and its full id in identity metadata/details;
- textual content prefers a bounded reconstructed text/preview before falling back to an opaque id when lawful reconstruction is available;
- an audio fixture with no transcript renders audio type + witnessed intrinsic metadata + id and does not invent text;
- image/video/model/binary fixtures follow the same type/metadata/id law without being pushed through an unlawful text decoder;
- `Unrealized entity` / `Unresolved entity` may appear as a diagnostic status only when the UI also renders the actual entity identity/fallback;
- Explore graph/entity/geometry-neighbor/member/container surfaces consume the shared display projection rather than maintaining local fallback laws;
- a deliberate mutant that replaces a known id with the literal generic placeholder fails;
- a deliberate mutant that hides both friendly realization and canonical id fails.

A live post-deploy receipt should reproduce a mixed-tier consensus web such as the CILI concept graph at 8 hops / fanout 16 / capacity 1024 and verify that every visible node is a name, Unicode preview, typed/domain notation, descriptive metadata, or typed/short-id fallback. No steady-state node may display only `Unrealized entity`, and the exact canonical id must remain inspectable regardless of the friendly realization.

Related: #1404, #1505.
