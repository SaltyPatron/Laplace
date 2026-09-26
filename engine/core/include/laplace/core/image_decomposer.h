#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/tier_tree.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * C API:
 *   laplace_image_decomposer_run — (rgba, w, h) → tier_tree whose leaves are
 *   Unicode digit codepoints, not packed-RGBA atoms.
 *   laplace_image_tree_build / laplace_image_root_id (modality_witness.h)
 *   compose it, resolving T0 through codepoint_table.
 */

/* Working-partition edge (pixels). This is a physical decomposition plan, not
 * identity salt: a square/subpatch id is determined only by its ordered constituent
 * pixel ids, so the same 2x2 is the same entity inside an 8x8, another image, or
 * another cache profile. */
#define LAPLACE_IMAGE_PATCH_SIZE 8u

/* RGBA channel count in packaging recovery order (R, G, B, A). */
#define LAPLACE_IMAGE_CHANNEL_COUNT 4u

/* Image ladder tiers above the shared codepoint floor. */
#define LAPLACE_IMAGE_TIER_CODEPOINT 0u
#define LAPLACE_IMAGE_TIER_NUMBER    1u
#define LAPLACE_IMAGE_TIER_CHANNEL   2u
#define LAPLACE_IMAGE_TIER_PIXEL     3u
#define LAPLACE_IMAGE_TIER_PATCH     4u
#define LAPLACE_IMAGE_TIER_REGION    5u
#define LAPLACE_IMAGE_TIER_IMAGE     6u

/*
 * Image ladder:
 *   tier 0 Codepoint — decimal digit chars of each channel byte (U+0030..U+0039)
 *   tier 1 Number    — ordered digits of one channel value (no leading zeros; "0" for zero)
 *   tier 2 Channel   — wraps one Number (R then G then B then A)
 *   tier 3 Pixel     — ordered channels
 *   tier 4 Patch     — current working 8x8 partition (not the only canonical patch scale)
 *   tier 5 Region    — current working row of patches
 *   tier 6 Image     — current working root
 *
 * Canonical subpatches at 2x2, 3x3, ... are global content entities whenever
 * their exact ordered pixel composition is materialized. Parent/tier/scale is
 * occurrence/physicality state, never hash salt.
 *
 * Packaging (media_decode → planar RGBA) is INPUT only. Identity is the
 * codepoint/number/channel tree, never blake3(rgba bytes) as tier-0.
 *
 * Number/Pixel/Patch/Region/Image roots are deterministic, so a derived mmap
 * perfcache can hold them; video and other consumers reuse those exact roots
 * rather than a second image identity.
 *
 * Leaf order rock lock: patch-major (patch grid row-major; within a patch,
 * pixels row-major; within a pixel, channels R,G,B,A; within a channel, MSD-first
 * digits). Returns 0 on success.
 */
int laplace_image_decomposer_run(
    const uint8_t* rgba,
    uint32_t       width,
    uint32_t       height,
    tier_tree_t**  out_tree);

#ifdef __cplusplus
}
#endif
