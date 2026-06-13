/*
 * perfcache_native.h -- the T0 oracle inside the backend.
 *
 * The perfcache blob (id/coord/hilbert/segmentation/NFC for all 1,114,112
 * codepoints, BLAKE3-CRC'd) is mmapped lazily via the
 * laplace_substrate.perfcache_path GUC. Boundary law: it serves ONLY the
 * placement-proof class (T0 + law products) -- never witnessed or consensus
 * data. It supplements the leaf layer; it shadows nothing.
 */
#pragma once

#include <stdbool.h>
#include <stdint.h>

/* GUC registration; call from _PG_init. */
void laplace_substrate_perfcache_init(void);

/* Lazy-load the blob named by the GUC. Returns false when the GUC is unset
 * (feature off -- callers keep their documented fallback); ERRORs when the
 * GUC names a missing/corrupt blob (configured-but-broken is a deploy bug). */
bool laplace_perfcache_ready(void);

/* T0 reverse lookup: 16-byte entity id -> codepoint. Returns false when the
 * perfcache is off, the id is not a codepoint id, or the codepoint is not
 * renderable (cp 0, surrogates) -- mirroring the retired codepoint_render
 * table's row set exactly. */
bool laplace_perfcache_codepoint_for_id(const uint8_t id[16], uint32_t *out_cp);
