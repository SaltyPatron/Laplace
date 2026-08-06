#include "laplace/core/image_decomposer.h"

#include <stdlib.h>

#include "laplace/core/tier_tree.h"

/* Decimal digit count for a channel byte (no leading zeros; 0 → one digit). */
static uint32_t channel_digit_count(uint8_t v) {
    if (v >= 100u) return 3u;
    if (v >= 10u) return 2u;
    return 1u;
}

/* MSD-first ASCII digit codepoints for v into out[0..n). Returns n. */
static uint32_t channel_digits(uint8_t v, uint32_t out[3]) {
    uint32_t n = channel_digit_count(v);
    uint32_t x = v;
    for (uint32_t i = 0; i < n; ++i) {
        uint32_t pow10 = 1u;
        for (uint32_t k = i + 1u; k < n; ++k) pow10 *= 10u;
        out[i] = (uint32_t)('0' + (x / pow10) % 10u);
    }
    return n;
}

int laplace_image_decomposer_run(
    const uint8_t* rgba,
    uint32_t       width,
    uint32_t       height,
    tier_tree_t**  out_tree) {
    if (!out_tree) return -1;
    *out_tree = NULL;
    if (width == 0 || height == 0) return -4;
    if (!rgba) return -1;

    size_t n_px = (size_t)width * (size_t)height;
    uint32_t patch_w = (width + LAPLACE_IMAGE_PATCH_SIZE - 1) / LAPLACE_IMAGE_PATCH_SIZE;
    uint32_t patch_h = (height + LAPLACE_IMAGE_PATCH_SIZE - 1) / LAPLACE_IMAGE_PATCH_SIZE;
    size_t n_patch = (size_t)patch_w * (size_t)patch_h;

    /* Worst case: 3 digits × 4 channels per pixel + Number/Channel/Pixel + patches/regions/image. */
    size_t cap = n_px * (12u + 4u + 4u + 1u) + n_patch + (size_t)patch_h + 8u;
    tier_tree_t* tree = tier_tree_new(cap);
    if (!tree) return -3;

    /*
     * Emit ALL tier-0 digit leaves first (patch-major pixel order, RGBA channels,
     * MSD-first digits) so each Number's children are a contiguous index range.
     * Then Numbers, Channels, Pixels, Patches, Regions, Image — same order.
     */
    uint32_t* digit_first = (uint32_t*)malloc(n_px * LAPLACE_IMAGE_CHANNEL_COUNT * sizeof(uint32_t));
    uint32_t* digit_count = (uint32_t*)malloc(n_px * LAPLACE_IMAGE_CHANNEL_COUNT * sizeof(uint32_t));
    if (!digit_first || !digit_count) {
        free(digit_first); free(digit_count);
        tier_tree_free(tree);
        return -3;
    }

    uint32_t leaf_ord = 0;
    for (uint32_t py = 0; py < patch_h; ++py) {
        for (uint32_t px = 0; px < patch_w; ++px) {
            uint32_t x0 = px * LAPLACE_IMAGE_PATCH_SIZE;
            uint32_t y0 = py * LAPLACE_IMAGE_PATCH_SIZE;
            uint32_t x1 = x0 + LAPLACE_IMAGE_PATCH_SIZE; if (x1 > width) x1 = width;
            uint32_t y1 = y0 + LAPLACE_IMAGE_PATCH_SIZE; if (y1 > height) y1 = height;
            for (uint32_t y = y0; y < y1; ++y) {
                for (uint32_t x = x0; x < x1; ++x) {
                    size_t pi = (size_t)y * width + x;
                    const uint8_t* p = rgba + pi * 4u;
                    for (uint32_t ch = 0; ch < LAPLACE_IMAGE_CHANNEL_COUNT; ++ch) {
                        uint32_t digs[3];
                        uint32_t n = channel_digits(p[ch], digs);
                        size_t slot = pi * LAPLACE_IMAGE_CHANNEL_COUNT + ch;
                        digit_first[slot] = leaf_ord;
                        digit_count[slot] = n;
                        for (uint32_t d = 0; d < n; ++d) {
                            uint32_t idx = tier_tree_add_leaf(
                                tree, LAPLACE_IMAGE_TIER_CODEPOINT, digs[d], leaf_ord, 1u);
                            if (idx == TIER_TREE_INVALID) {
                                free(digit_first); free(digit_count);
                                tier_tree_free(tree);
                                return -3;
                            }
                            leaf_ord++;
                        }
                    }
                }
            }
        }
    }

    uint32_t* number_idx = (uint32_t*)malloc(n_px * LAPLACE_IMAGE_CHANNEL_COUNT * sizeof(uint32_t));
    uint32_t* channel_idx = (uint32_t*)malloc(n_px * LAPLACE_IMAGE_CHANNEL_COUNT * sizeof(uint32_t));
    uint32_t* pixel_idx = (uint32_t*)malloc(n_px * sizeof(uint32_t));
    if (!number_idx || !channel_idx || !pixel_idx) {
        free(digit_first); free(digit_count);
        free(number_idx); free(channel_idx); free(pixel_idx);
        tier_tree_free(tree);
        return -3;
    }

    /* Numbers then Channels — same pixel×channel order as digit groups. */
    for (uint32_t py = 0; py < patch_h; ++py) {
        for (uint32_t px = 0; px < patch_w; ++px) {
            uint32_t x0 = px * LAPLACE_IMAGE_PATCH_SIZE;
            uint32_t y0 = py * LAPLACE_IMAGE_PATCH_SIZE;
            uint32_t x1 = x0 + LAPLACE_IMAGE_PATCH_SIZE; if (x1 > width) x1 = width;
            uint32_t y1 = y0 + LAPLACE_IMAGE_PATCH_SIZE; if (y1 > height) y1 = height;
            for (uint32_t y = y0; y < y1; ++y) {
                for (uint32_t x = x0; x < x1; ++x) {
                    size_t pi = (size_t)y * width + x;
                    for (uint32_t ch = 0; ch < LAPLACE_IMAGE_CHANNEL_COUNT; ++ch) {
                        size_t slot = pi * LAPLACE_IMAGE_CHANNEL_COUNT + ch;
                        uint32_t first = digit_first[slot];
                        uint32_t count = digit_count[slot];
                        uint32_t nidx = tier_tree_add_node(
                            tree, LAPLACE_IMAGE_TIER_NUMBER, first, count, first, count);
                        if (nidx == TIER_TREE_INVALID) goto oom;
                        number_idx[slot] = nidx;
                    }
                }
            }
        }
    }

    for (uint32_t py = 0; py < patch_h; ++py) {
        for (uint32_t px = 0; px < patch_w; ++px) {
            uint32_t x0 = px * LAPLACE_IMAGE_PATCH_SIZE;
            uint32_t y0 = py * LAPLACE_IMAGE_PATCH_SIZE;
            uint32_t x1 = x0 + LAPLACE_IMAGE_PATCH_SIZE; if (x1 > width) x1 = width;
            uint32_t y1 = y0 + LAPLACE_IMAGE_PATCH_SIZE; if (y1 > height) y1 = height;
            for (uint32_t y = y0; y < y1; ++y) {
                for (uint32_t x = x0; x < x1; ++x) {
                    size_t pi = (size_t)y * width + x;
                    for (uint32_t ch = 0; ch < LAPLACE_IMAGE_CHANNEL_COUNT; ++ch) {
                        size_t slot = pi * LAPLACE_IMAGE_CHANNEL_COUNT + ch;
                        /* Channel wraps one Number; same span → floor-collapse eligible. */
                        uint32_t first = number_idx[slot];
                        uint32_t span_off = digit_first[slot];
                        uint32_t span_len = digit_count[slot];
                        uint32_t cidx = tier_tree_add_node(
                            tree, LAPLACE_IMAGE_TIER_CHANNEL, first, 1u, span_off, span_len);
                        if (cidx == TIER_TREE_INVALID) goto oom;
                        channel_idx[slot] = cidx;
                    }
                }
            }
        }
    }

    /* Pixels — four contiguous Channel children (R,G,B,A) per pixel. */
    for (uint32_t py = 0; py < patch_h; ++py) {
        for (uint32_t px = 0; px < patch_w; ++px) {
            uint32_t x0 = px * LAPLACE_IMAGE_PATCH_SIZE;
            uint32_t y0 = py * LAPLACE_IMAGE_PATCH_SIZE;
            uint32_t x1 = x0 + LAPLACE_IMAGE_PATCH_SIZE; if (x1 > width) x1 = width;
            uint32_t y1 = y0 + LAPLACE_IMAGE_PATCH_SIZE; if (y1 > height) y1 = height;
            for (uint32_t y = y0; y < y1; ++y) {
                for (uint32_t x = x0; x < x1; ++x) {
                    size_t pi = (size_t)y * width + x;
                    size_t slot0 = pi * LAPLACE_IMAGE_CHANNEL_COUNT;
                    uint32_t first_ch = channel_idx[slot0];
                    uint32_t span_off = digit_first[slot0];
                    uint32_t span_end = digit_first[slot0 + LAPLACE_IMAGE_CHANNEL_COUNT - 1u]
                                       + digit_count[slot0 + LAPLACE_IMAGE_CHANNEL_COUNT - 1u];
                    uint32_t pidx = tier_tree_add_node(
                        tree, LAPLACE_IMAGE_TIER_PIXEL, first_ch, LAPLACE_IMAGE_CHANNEL_COUNT,
                        span_off, span_end - span_off);
                    if (pidx == TIER_TREE_INVALID) goto oom;
                    pixel_idx[pi] = pidx;
                }
            }
        }
    }

    free(digit_first); digit_first = NULL;
    free(digit_count); digit_count = NULL;
    free(number_idx); number_idx = NULL;
    free(channel_idx); channel_idx = NULL;

    uint32_t* patch_idx = (uint32_t*)malloc(n_patch * sizeof(uint32_t));
    if (!patch_idx) { free(pixel_idx); tier_tree_free(tree); return -3; }

    for (uint32_t py = 0; py < patch_h; ++py) {
        for (uint32_t px = 0; px < patch_w; ++px) {
            uint32_t x0 = px * LAPLACE_IMAGE_PATCH_SIZE;
            uint32_t y0 = py * LAPLACE_IMAGE_PATCH_SIZE;
            uint32_t x1 = x0 + LAPLACE_IMAGE_PATCH_SIZE; if (x1 > width) x1 = width;
            uint32_t y1 = y0 + LAPLACE_IMAGE_PATCH_SIZE; if (y1 > height) y1 = height;
            uint32_t count = (x1 - x0) * (y1 - y0);
            /* First pixel of this patch in emission order = pixel at (x0,y0). */
            /* Pixel nodes were added in the same patch-major walk → contiguous. */
            uint32_t first = pixel_idx[(size_t)y0 * width + x0];
            uint32_t idx = tier_tree_add_node(
                tree, LAPLACE_IMAGE_TIER_PATCH, first, count, first, count);
            if (idx == TIER_TREE_INVALID) {
                free(patch_idx); free(pixel_idx); tier_tree_free(tree); return -3;
            }
            patch_idx[py * patch_w + px] = idx;
        }
    }
    free(pixel_idx); pixel_idx = NULL;

    uint32_t* region_idx = (uint32_t*)malloc(patch_h * sizeof(uint32_t));
    if (!region_idx) { free(patch_idx); tier_tree_free(tree); return -3; }

    for (uint32_t py = 0; py < patch_h; ++py) {
        uint32_t first = patch_idx[py * patch_w];
        uint32_t count = patch_w;
        uint32_t idx = tier_tree_add_node(
            tree, LAPLACE_IMAGE_TIER_REGION, first, count, py, count);
        if (idx == TIER_TREE_INVALID) {
            free(patch_idx); free(region_idx); tier_tree_free(tree); return -3;
        }
        region_idx[py] = idx;
    }
    free(patch_idx);

    uint32_t first_region = region_idx[0];
    uint32_t n_regions = patch_h;
    free(region_idx);
    uint32_t root = tier_tree_add_node(
        tree, LAPLACE_IMAGE_TIER_IMAGE, first_region, n_regions, 0, (uint32_t)n_px);
    if (root == TIER_TREE_INVALID) { tier_tree_free(tree); return -3; }

    if (tier_tree_finalize(tree) != 0) { tier_tree_free(tree); return -3; }
    *out_tree = tree;
    return 0;

oom:
    free(digit_first); free(digit_count);
    free(number_idx); free(channel_idx); free(pixel_idx);
    tier_tree_free(tree);
    return -3;
}
