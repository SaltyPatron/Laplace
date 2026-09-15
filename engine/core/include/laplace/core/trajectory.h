#pragma once

#include <stdint.h>
#include <stddef.h>
#include "laplace/core/hash128.h"

#ifdef __cplusplus
extern "C" {
#endif

int trajectory_build_flagged(const hash128_t* entity_hashes,
                             const uint64_t*  flags,
                             size_t           n,
                             double*          out_xyzm);

int trajectory_build(const hash128_t* entity_hashes,
                     size_t           n,
                     double*          out_xyzm);

int trajectory_build_rle(const hash128_t* constituents,
                         size_t           n,
                         double*          out_xyzm,
                         size_t*          out_vertex_count);

int trajectory_build_flagged_rle(const hash128_t* entity_hashes,
                                 const uint64_t* flags,
                                 size_t n,
                                 double* out_xyzm,
                                 size_t* out_vertex_count);

int trajectory_constituent_count(const double* trajectory_xyzm,
                                 size_t        n_points,
                                 size_t*       out_count);

/* Classify physicality carriers before treating the run channel as RLE.
 * Testimony stores games there; factors store exact float bits. For a typed
 * payload out_ordinary_count is zero and the physicality type owns its declared
 * count. HAS_ATOM takes precedence over overlapping numeric flag bits.
 * Validates canonical packing and factor widths without expanding anything. */
int trajectory_manifest_scan(const double* trajectory_xyzm, size_t n_points,
    size_t* out_ordinary_count, int* out_typed_payload);

/* Shared physicality manifest law for native and managed admission. Content
 * (type1) requires an ordinary manifest and unchanged canonical entity ID.
 * Ordinary non-Content validates its count; typed factor/testimony payloads
 * retain their type-owned count without interpreting numeric bits as RLE.
 * Returns0, -1 invalid arguments, -3 malformed/count, -4 Content ID mismatch. */
int laplace_physicality_manifest_validate(const hash128_t* entity_id, int16_t type,
    const double* trajectory_xyzm, size_t n_points, int32_t n_constituents);

/* Visit stored vertices without expanding runs. Ordinals are the logical
 * prefix sum; a legacy zero run denotes one constituent. */
typedef int (*trajectory_vertex_visitor_t)(void* context, size_t ordinal,
    const hash128_t* entity_id, size_t run_length, uint64_t flags);
int trajectory_visit_vertices(const double* trajectory_xyzm, size_t n_points,
    trajectory_vertex_visitor_t visitor, void* context);

typedef int (*trajectory_constituent_visitor_t)(void*             context,
                                                size_t            ordinal,
                                                const hash128_t*   entity_id,
                                                uint64_t           flags);

int trajectory_visit_constituents(const double*                    trajectory_xyzm,
                                  size_t                           n_points,
                                  trajectory_constituent_visitor_t visitor,
                                  void*                            context);

int trajectory_constituents(const double* trajectory_xyzm,
                            size_t        n_points,
                            hash128_t*    out_hashes,
                            size_t        out_cap);

/* Recompute the canonical content identity represented by a packed/RLE manifest
 * without expanding it. One logical child collapses to that child; two or more
 * use the exact hash128_merkle byte stream. Returns 0 on success, -1 on invalid
 * input. out_count is the expanded logical constituent count. */
int trajectory_content_identity(const double* trajectory_xyzm,
                                size_t n_points,
                                hash128_t* out_id,
                                size_t* out_count);

int trajectory_equivalent(const double* left_xyzm,
                          size_t        left_points,
                          const double* right_xyzm,
                          size_t        right_points);

typedef struct trajectory_suffix_matcher trajectory_suffix_matcher_t;
typedef int (*trajectory_suffix_visitor_t)(void* context, size_t ordinal,
                                           size_t stride,
                                           const hash128_t* successor);

trajectory_suffix_matcher_t* trajectory_suffix_matcher_create(
    const hash128_t* context, size_t count, size_t minimum_stride);
void trajectory_suffix_matcher_free(trajectory_suffix_matcher_t* matcher);
int trajectory_match_suffixes(trajectory_suffix_matcher_t* matcher,
                              const void* packed_xyzm, size_t n_points,
                              trajectory_suffix_visitor_t visitor, void* context);
int trajectory_match_occurrences(trajectory_suffix_matcher_t* matcher,
                                 const void* packed_xyzm, size_t n_points,
                                 trajectory_suffix_visitor_t visitor, void* context);

typedef struct trajectory_ordinal_index trajectory_ordinal_index_t;
trajectory_ordinal_index_t* trajectory_ordinal_index_create(
    const void* packed_xyzm, size_t n_points);
void trajectory_ordinal_index_free(trajectory_ordinal_index_t* index);
int trajectory_ordinal_index_read(const trajectory_ordinal_index_t* index,
    size_t ordinal, hash128_t* entity_id, uint64_t* flags);

#ifdef __cplusplus
}
#endif
