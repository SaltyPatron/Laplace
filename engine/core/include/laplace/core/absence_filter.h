#pragma once

/*
 * absence_filter — a no-false-negative Bloom membership filter over content ids.
 *
 * DIRECTION
 *
 * Only absence is a proof. Skipping rows believed present would let a false
 * positive drop a novel row, so the filter is never read that way:
 *
 *     maybe_present == false  ->  the id is not in the set; it is novel without
 *                                 an exact probe.
 *     maybe_present == true   ->  present or a false positive; the caller still
 *                                 performs the exact probe.
 *
 * A false positive costs one exact probe, never a row or a merge of a re-seen
 * observation.
 *
 * HASHING
 *
 * Ids are already BLAKE3 content hashes, so their bits are uniform and need no
 * further mixing. The k probes come from Kirsch-Mitzenmacher double hashing over
 * the two halves: g_i = lo + i*hi. h2 is forced odd so it is coprime with a
 * power-of-two m and the probe sequence cannot degenerate to a single slot.
 *
 * DETERMINISM
 *
 * Setting bits is commutative and idempotent, so a filter built from the same id
 * set is bit-identical regardless of insertion order, thread interleaving, or
 * how many times an id is added. That is what lets it be persisted as a blob and
 * appended to in place, and it is why it never needs a rebuild pass: growth is
 * OR-ing more bits into a capacity chosen up front, never a re-fold.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"

#ifdef __cplusplus
extern "C" {
#endif

#define LAPLACE_ABSENCE_MAGIC   0x464C4241u /* "ABLF" */
#define LAPLACE_ABSENCE_VERSION 1u

/* Header is written verbatim at the head of the blob; the bit array follows. */
typedef struct {
    uint32_t magic;
    uint32_t format_version;
    uint64_t bit_count;      /* m, always a power of two */
    uint32_t hash_count;     /* k */
    uint32_t _pad;
    uint64_t capacity;       /* n the parameters were sized for */
    uint64_t inserted;       /* adds performed; advisory, not a distinct count */
} laplace_absence_header_t;

typedef struct {
    laplace_absence_header_t hdr;
    uint64_t*                bits;      /* bit_count/64 words */
    size_t                   word_count;
    bool                     owns_bits; /* false when mapped from a blob */
} laplace_absence_filter_t;

/*
 * Size and allocate for `capacity` distinct ids at false-positive rate `fpr`
 * (0 < fpr < 1). bit_count is rounded UP to a power of two, which only ever
 * lowers the realised fpr. Returns 0 on success, -1 on bad argument, -2 on
 * allocation failure.
 */
int laplace_absence_create(laplace_absence_filter_t* f, uint64_t capacity, double fpr);

/* Release memory owned by the filter. Safe on a zeroed or mapped filter. */
void laplace_absence_destroy(laplace_absence_filter_t* f);

/* Add an id. Idempotent. Thread-safe against other adds and queries. */
void laplace_absence_add(laplace_absence_filter_t* f, const hash128_t* id);

/*
 * THE contract: false means the id is definitely NOT in the set. True means it
 * may be — the caller must still probe.
 */
bool laplace_absence_maybe_present(const laplace_absence_filter_t* f, const hash128_t* id);

/* Total bytes a blob for this filter occupies (header + bit array). */
size_t laplace_absence_blob_size(const laplace_absence_filter_t* f);

/*
 * Serialise into `dst` (>= laplace_absence_blob_size bytes). Returns 0 on
 * success, -1 on bad argument.
 */
int laplace_absence_save(const laplace_absence_filter_t* f, void* dst, size_t dst_len);

/*
 * Attach to a blob IN PLACE — no copy, so `src` must outlive the filter and stay
 * writable if the caller intends to keep adding. Returns 0 on success, -1 on bad
 * argument, -3 on magic/version/size mismatch.
 */
int laplace_absence_attach(laplace_absence_filter_t* f, void* src, size_t src_len);

/* Realised false-positive rate at the current fill. Diagnostics only. */
double laplace_absence_estimated_fpr(const laplace_absence_filter_t* f);

#ifdef __cplusplus
}
#endif
