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

const codepoint_entry_t* codepoint_table_lookup(uint32_t codepoint);

int codepoint_table_records(const codepoint_entry_t** out_records, uint64_t* out_count);

uint8_t codepoint_table_gb(uint32_t codepoint);
uint8_t codepoint_table_wb(uint32_t codepoint);
uint8_t codepoint_table_sb(uint32_t codepoint);
uint8_t codepoint_table_incb(uint32_t codepoint);
uint8_t codepoint_table_ccc(uint32_t codepoint);

int codepoint_table_decompose(uint32_t cp, const uint32_t** out_seq, uint32_t* out_len);

int codepoint_table_compose(uint32_t first, uint32_t second, uint32_t* out_composed);

/* Atom resolver for hash_composer_run (perfcache must be loaded). */
int codepoint_table_resolve_atom(uint32_t atom, hash128_t* out_id,
                                 double out_coord[4], hilbert128_t* out_hb);

/* THE separator law: Unicode White_Space (PropList.txt), reconstructed from
 * the banked WB classes plus the four codepoints White_Space adds beyond them
 * (TAB, NBSP, FIGURE SPACE, NARROW NO-BREAK SPACE). Never a regex --
 * [[:space:]] is ASCII-bound and silently keeps U+3000 IDEOGRAPHIC SPACE,
 * NBSP, the U+2000..200A family, etc. as units of order.
 * Requires the perfcache loaded; unloaded, only the four enumerated
 * codepoints answer (callers must ensure readiness). */
int laplace_codepoint_is_whitespace(uint32_t cp);

/* True iff utf8 is non-empty, valid UTF-8, and EVERY codepoint is White_Space
 * by the law above. The omniglottal separator test for the generation corpus:
 * a token is a word-order separator iff its reconstructed text is all
 * whitespace, in any script. Invalid UTF-8 -> 0 (not a separator). */
int laplace_text_is_all_whitespace(const uint8_t* utf8, size_t len);

#ifdef __cplusplus
}
#endif
