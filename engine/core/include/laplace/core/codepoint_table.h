#pragma once

#include <stdint.h>
#include <stddef.h>
#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"
#include "laplace/core/perfcache_format.h"
#include "laplace/core/ucd_property_values.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef laplace_perfcache_record_t codepoint_entry_t;

int codepoint_table_load_perfcache(const char* path);

void codepoint_table_unload(void);

int codepoint_table_is_loaded(void);

/* Copy the checksum verified by load; no borrowed mmap pointer escapes. */
int codepoint_table_copy_receipt(hash128_t* out_receipt);

const codepoint_entry_t* codepoint_table_lookup(uint32_t codepoint);

int codepoint_table_records(const codepoint_entry_t** out_records, uint64_t* out_count);

uint8_t codepoint_table_gb(uint32_t codepoint);
uint8_t codepoint_table_wb(uint32_t codepoint);
uint8_t codepoint_table_sb(uint32_t codepoint);
uint8_t codepoint_table_incb(uint32_t codepoint);
uint8_t codepoint_table_ccc(uint32_t codepoint);

int codepoint_table_decompose(uint32_t cp, const uint32_t** out_seq, uint32_t* out_len);

int codepoint_table_compose(uint32_t first, uint32_t second, uint32_t* out_composed);


int codepoint_table_resolve_atom(uint32_t atom, hash128_t* out_id,
                                 double out_coord[4], hilbert128_t* out_hb);

int codepoint_table_lookup_id(const hash128_t* id, uint32_t* out_cp);

/* Explicit provider preparation under the floor publication gate. Returns 0
 * when ready, -1 without a loaded floor, -2 for budget/allocation failure.
 * The index belongs to the loaded floor; out_added_bytes is zero when reused.
 * Sorting is in place and performs no additional allocation. */
int codepoint_table_prepare_id_index(size_t maximum_additional_bytes, size_t* out_added_bytes);
int codepoint_table_id_index_ready(void);
size_t codepoint_table_id_index_bytes(void);

/* Positional floor membership, LSB first. This answers resolvability from the
 * mapped floor, never whether an entity/attestation row has been persisted.
 * Returns 0 on success, -1 for invalid buffers or an unloaded floor. */
int codepoint_table_presence_bitmap(const hash128_t* ids, size_t count,
                                    uint8_t* bitmap, size_t bitmap_bytes);








int laplace_codepoint_is_whitespace(uint32_t cp);





int laplace_text_is_all_whitespace(const uint8_t* utf8, size_t len);

#ifdef __cplusplus
}
#endif
