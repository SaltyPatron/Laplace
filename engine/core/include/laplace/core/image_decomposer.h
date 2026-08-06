#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/tier_tree.h"

#ifdef __cplusplus
extern "C" {
#endif

/*
 * C API (P/Invoke-stable names — semantics deepened 2026-08-06):
 *   laplace_image_decomposer_run — still (rgba, w, h) → tier_tree; leaves are
 *   now Unicode digit codepoints, not packed-RGBA atoms. Managed spines that
 *   assumed tier-0 = Pixel / atom = packed color must read the new tier map.
 *   laplace_image_tree_build / laplace_image_root_id keep their entry points
 *   (see modality_witness.h); compose resolves T0 via codepoint_table.
 */

/* Fixed patch edge (pixels). Rock-stable — changing it reassigns every patch id. */
#define LAPLACE_IMAGE_PATCH_SIZE 8u

/* RGBA channel count in packaging recovery order (R, G, B, A). */
#define LAPLACE_IMAGE_CHANNEL_COUNT 4u

/* Image ladder tiers (witnessed infra, codepoint floor). */
#define LAPLACE_IMAGE_TIER_CODEPOINT 0u
#define LAPLACE_IMAGE_TIER_NUMBER    1u
#define LAPLACE_IMAGE_TIER_CHANNEL   2u
#define LAPLACE_IMAGE_TIER_PIXEL     3u
#define LAPLACE_IMAGE_TIER_PATCH     4u
#define LAPLACE_IMAGE_TIER_REGION    5u
#define LAPLACE_IMAGE_TIER_IMAGE     6u

/*
 * Image ladder (witnessed infra):
 *   tier 0 Codepoint — decimal digit chars of each channel byte (U+0030..U+0039)
 *   tier 1 Number    — ordered digits of one channel value (no leading zeros; "0" for zero)
 *   tier 2 Channel   — wraps one Number (R then G then B then A)
 *   tier 3 Pixel     — ordered channels
 *   tier 4 Patch     — LAPLACE_IMAGE_PATCH_SIZE × LAPLACE_IMAGE_PATCH_SIZE, clipped
 *   tier 5 Region    — one row of patches
 *   tier 6 Image     — all regions
 *
 * Packaging (media_decode → planar RGBA) is INPUT only. Identity is the
 * codepoint/number/channel tree, never blake3(rgba bytes) as tier-0.
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
