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

/* Canonical ordered-manifest packing: adjacent vertices coalesce only when
 * both identity and the complete mantissa flag payload match. `out_xyzm` must
 * hold n vertices; `out_vertex_count` is the compact stored count. */
int trajectory_build_flagged_rle(const hash128_t* entity_hashes,
                                 const uint64_t* flags,
                                 size_t n,
                                 double* out_xyzm,
                                 size_t* out_vertex_count);

/* Return the exact expanded constituent count represented by an RLE manifest.
 * This lets bindings allocate an output without treating stored vertices as
 * logical constituents. */
int trajectory_constituent_count(const double* trajectory_xyzm,
                                 size_t        n_points,
                                 size_t*       out_count);

/* Visit every logical constituent in source order.  `ordinal` is one-based
 * and is derived from the prefix sum of run lengths, never from the stored
 * vertex index or its redundant packed ordinal.  This is the one expansion
 * kernel used by bindings which need positions as well as ids and flags. */
typedef int (*trajectory_constituent_visitor_t)(void*             context,
                                                size_t            ordinal,
                                                const hash128_t*   entity_id,
                                                uint64_t           flags);

int trajectory_visit_constituents(const double*                    trajectory_xyzm,
                                  size_t                           n_points,
                                  trajectory_constituent_visitor_t visitor,
                                  void*                            context);

/* Expand the stored RLE manifest to its full ordered constituent sequence. */
int trajectory_constituents(const double* trajectory_xyzm,
                            size_t        n_points,
                            hash128_t*    out_hashes,
                            size_t        out_cap);

/* Compare two stored manifests by their full logical constituent streams.
 * RLE chunking and plain-vs-compressed storage are representation details;
 * identity, source order, multiplicity, and the complete flags payload are
 * semantic. Returns 1 when equal, 0 when unequal, and -1 for invalid inputs. */
int trajectory_equivalent(const double* left_xyzm,
                          size_t        left_points,
                          const double* right_xyzm,
                          size_t        right_points);

/* Match all suffix lengths of an ordered context in one pass over a packed
 * manifest. The callback receives the longest suffix starting at each occurrence
 * which has a successor. Across manifests, retaining the greatest reported
 * stride produces exact longest-suffix backoff without repeated index probes.
 * The visitor also receives stride zero / NULL successor at packed-vertex
 * boundaries so an embedding can interrupt long scans without a match.
 * Input vertices may be unaligned (for example a WKB payload). Runs and repeated
 * identities remain occurrences; workspace is proportional to context length. */
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

/* Same ordered matcher, including occurrences ending at the manifest boundary.
 * Such occurrences have a NULL successor. Create with minimum_stride=count
 * for exact contiguous containment; no set membership or label comparison can
 * establish this predicate. */
int trajectory_match_occurrences(trajectory_suffix_matcher_t* matcher,
                                 const void* packed_xyzm, size_t n_points,
                                 trajectory_suffix_visitor_t visitor, void* context);

/* Indexed ordinal access without expanding run-length encoded constituents.
 * The packed input is borrowed and must outlive the index. Construction visits
 * each stored vertex once; lookup is logarithmic in stored vertex count.
 * read returns 0 for a constituent, 1 past the end, -1 for invalid arguments. */
typedef struct trajectory_ordinal_index trajectory_ordinal_index_t;
trajectory_ordinal_index_t* trajectory_ordinal_index_create(
    const void* packed_xyzm, size_t n_points);
void trajectory_ordinal_index_free(trajectory_ordinal_index_t* index);
int trajectory_ordinal_index_read(const trajectory_ordinal_index_t* index,
    size_t ordinal, hash128_t* entity_id, uint64_t* flags);

#ifdef __cplusplus
}
#endif
