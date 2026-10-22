#include "laplace/core/text_decomposer.h"
#include "laplace/core/utf8.h"
#include <string.h>

int native_text_tree_build(const uint8_t *, size_t, tier_tree_t **);

int laplace_text_decomposer_validate_roundtrip(const tier_tree_t* tree) {
    if (!tree) return -1;

    size_t text_len = 0;
    const uint8_t* text = tier_tree_text(tree, &text_len);
    size_t cursor = 0;
    size_t leaves = 0;
    size_t node_n = tier_tree_node_count(tree);

    for (size_t i = 0; i < node_n; ++i) {
        tier_node_view_t v;
        if (tier_tree_get_node(tree, (uint32_t)i, &v) != 0) return -2;
        if (v.tier != 0) continue;

        uint8_t encoded[4];
        size_t encoded_len = laplace_utf8_encode(v.atom, encoded);
        if (v.text_range_off != cursor
            || v.text_range_len != encoded_len
            || cursor + encoded_len > text_len
            || !text
            || memcmp(text + cursor, encoded, encoded_len) != 0) {
            return -2;
        }
        cursor += encoded_len;
        leaves++;
    }

    if (text_len == 0) return leaves == 0 ? 0 : -2;
    return (leaves > 0 && cursor == text_len) ? 0 : -2;
}

int laplace_text_decomposer_run(const uint8_t *utf8, size_t len, tier_tree_t **out_tree) {
    return native_text_tree_build(utf8, len, out_tree);
}

int laplace_text_decomposer_run_source(const uint8_t *utf8, size_t len, tier_tree_t **out_tree) {
    return native_text_tree_build(utf8, len, out_tree);
}
