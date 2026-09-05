# Explore human-label live acceptance

After merge/deploy, verify the consensus web against a mixed-tier entity such as a CILI concept at 3D, 8 hops, fanout 16, capacity 1024.

Acceptance:

- no node's visible `label` is populated from its own `id_hex` fallback;
- named entities display their witnessed/canonical name;
- unnamed tier <= 3 text displays reconstructed Unicode content;
- unnamed higher-tier text displays a bounded first-descendant Unicode preview with an ellipsis, without reconstructing the full object;
- non-text or otherwise unrenderable unnamed entities display their type label when available, otherwise `unrealized entity`;
- clicking/opening continues to use the exact 128-bit `id_hex` independent of presentation;
- a 1024-node graph must remain a bounded serving read; label resolution must not invoke `constituents_closure` or scalar-per-node full rendering;
- labels containing non-BMP Unicode must truncate on character boundaries.

The graph screenshot that motivated this repair showed raw 128-bit identities occupying the primary visual label. That state is a contract failure even when the ids are valid and clickable.
