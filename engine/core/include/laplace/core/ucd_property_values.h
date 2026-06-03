#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Fixed UAX#29 / UAX#15 property-value ids — the canonical numbering the
 * perf-cache emitter packs into laplace_perfcache_record_t.flags and the
 * runtime state machines compare against. FIXED (not discovery-order
 * interned) so emitter + loader + state machines agree byte-for-byte
 * regardless of build.
 *
 * Ranges chosen to fit the flags bitfield widths in perfcache_format.h:
 *   GB  4 bits (0..15)   WB 5 bits (0..31)   SB 4 bits (0..15)
 *   InCB 2 bits (0..3)   CCC 8 bits (raw 0..254) */

/* Grapheme_Cluster_Break (15 values) */
enum {
    LAPLACE_GB_OTHER = 0,
    LAPLACE_GB_CR,
    LAPLACE_GB_LF,
    LAPLACE_GB_CONTROL,
    LAPLACE_GB_EXTEND,
    LAPLACE_GB_ZWJ,
    LAPLACE_GB_REGIONAL_INDICATOR,
    LAPLACE_GB_PREPEND,
    LAPLACE_GB_SPACINGMARK,
    LAPLACE_GB_L,
    LAPLACE_GB_V,
    LAPLACE_GB_T,
    LAPLACE_GB_LV,
    LAPLACE_GB_LVT,
    LAPLACE_GB_EXTENDED_PICTOGRAPHIC,
};

/* Word_Break (up to 31; UAX#29 17.0 uses these) */
enum {
    LAPLACE_WB_OTHER = 0,
    LAPLACE_WB_CR,
    LAPLACE_WB_LF,
    LAPLACE_WB_NEWLINE,
    LAPLACE_WB_EXTEND,
    LAPLACE_WB_ZWJ,
    LAPLACE_WB_REGIONAL_INDICATOR,
    LAPLACE_WB_FORMAT,
    LAPLACE_WB_KATAKANA,
    LAPLACE_WB_HEBREW_LETTER,
    LAPLACE_WB_ALETTER,
    LAPLACE_WB_SINGLE_QUOTE,
    LAPLACE_WB_DOUBLE_QUOTE,
    LAPLACE_WB_MIDNUMLET,
    LAPLACE_WB_MIDLETTER,
    LAPLACE_WB_MIDNUM,
    LAPLACE_WB_NUMERIC,
    LAPLACE_WB_EXTENDNUMLET,
    LAPLACE_WB_WSEGSPACE,
};

/* Sentence_Break (15 values) */
enum {
    LAPLACE_SB_OTHER = 0,
    LAPLACE_SB_CR,
    LAPLACE_SB_LF,
    LAPLACE_SB_EXTEND,
    LAPLACE_SB_SEP,
    LAPLACE_SB_FORMAT,
    LAPLACE_SB_SP,
    LAPLACE_SB_LOWER,
    LAPLACE_SB_UPPER,
    LAPLACE_SB_OLETTER,
    LAPLACE_SB_NUMERIC,
    LAPLACE_SB_ATERM,
    LAPLACE_SB_SCONTINUE,
    LAPLACE_SB_STERM,
    LAPLACE_SB_CLOSE,
};

/* Indic_Conjunct_Break (4 values) */
enum {
    LAPLACE_INCB_NONE = 0,
    LAPLACE_INCB_EXTEND,
    LAPLACE_INCB_LINKER,
    LAPLACE_INCB_CONSONANT,
};

#ifdef __cplusplus
}
#endif
