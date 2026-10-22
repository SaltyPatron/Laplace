/* ICU owns Unicode properties and normalization data; the scalar coordinate
 * mapping contains geometry and identity, not a second Unicode database. */
#include "laplace/core/codepoint_table.h"
#include "laplace/core/perfcache_format.h"
#include "laplace/tier0.h"
#include <unicode/uchar.h>
#include <unicode/unorm2.h>
#include <unicode/utf16.h>

uint8_t codepoint_table_gb(uint32_t cp) {
    if (!lp_unicode_scalar(cp)) return LAPLACE_GB_OTHER;
    switch (u_getIntPropertyValue((UChar32)cp, UCHAR_GRAPHEME_CLUSTER_BREAK)) {
        case U_GCB_CR: return LAPLACE_GB_CR;
        case U_GCB_LF: return LAPLACE_GB_LF;
        case U_GCB_CONTROL: return LAPLACE_GB_CONTROL;
        case U_GCB_EXTEND: return LAPLACE_GB_EXTEND;
        case U_GCB_ZWJ: return LAPLACE_GB_ZWJ;
        case U_GCB_REGIONAL_INDICATOR: return LAPLACE_GB_REGIONAL_INDICATOR;
        case U_GCB_PREPEND: return LAPLACE_GB_PREPEND;
        case U_GCB_SPACING_MARK: return LAPLACE_GB_SPACINGMARK;
        case U_GCB_L: return LAPLACE_GB_L;
        case U_GCB_V: return LAPLACE_GB_V;
        case U_GCB_T: return LAPLACE_GB_T;
        case U_GCB_LV: return LAPLACE_GB_LV;
        case U_GCB_LVT: return LAPLACE_GB_LVT;
        default: return u_hasBinaryProperty((UChar32)cp, UCHAR_EXTENDED_PICTOGRAPHIC)
            ? LAPLACE_GB_EXTENDED_PICTOGRAPHIC : LAPLACE_GB_OTHER;
    }
}

uint8_t codepoint_table_wb(uint32_t cp) {
    if (!lp_unicode_scalar(cp)) return LAPLACE_WB_OTHER;
    switch (u_getIntPropertyValue((UChar32)cp, UCHAR_WORD_BREAK)) {
        case U_WB_CR: return LAPLACE_WB_CR;
        case U_WB_LF: return LAPLACE_WB_LF;
        case U_WB_NEWLINE: return LAPLACE_WB_NEWLINE;
        case U_WB_EXTEND: return LAPLACE_WB_EXTEND;
        case U_WB_ZWJ: return LAPLACE_WB_ZWJ;
        case U_WB_REGIONAL_INDICATOR: return LAPLACE_WB_REGIONAL_INDICATOR;
        case U_WB_FORMAT: return LAPLACE_WB_FORMAT;
        case U_WB_KATAKANA: return LAPLACE_WB_KATAKANA;
        case U_WB_HEBREW_LETTER: return LAPLACE_WB_HEBREW_LETTER;
        case U_WB_ALETTER: return LAPLACE_WB_ALETTER;
        case U_WB_SINGLE_QUOTE: return LAPLACE_WB_SINGLE_QUOTE;
        case U_WB_DOUBLE_QUOTE: return LAPLACE_WB_DOUBLE_QUOTE;
        case U_WB_MIDNUMLET: return LAPLACE_WB_MIDNUMLET;
        case U_WB_MIDLETTER: return LAPLACE_WB_MIDLETTER;
        case U_WB_MIDNUM: return LAPLACE_WB_MIDNUM;
        case U_WB_NUMERIC: return LAPLACE_WB_NUMERIC;
        case U_WB_EXTENDNUMLET: return LAPLACE_WB_EXTENDNUMLET;
        case U_WB_WSEGSPACE: return LAPLACE_WB_WSEGSPACE;
        default: return LAPLACE_WB_OTHER;
    }
}

uint8_t codepoint_table_sb(uint32_t cp) {
    if (!lp_unicode_scalar(cp)) return LAPLACE_SB_OTHER;
    switch (u_getIntPropertyValue((UChar32)cp, UCHAR_SENTENCE_BREAK)) {
        case U_SB_CR: return LAPLACE_SB_CR;
        case U_SB_LF: return LAPLACE_SB_LF;
        case U_SB_EXTEND: return LAPLACE_SB_EXTEND;
        case U_SB_SEP: return LAPLACE_SB_SEP;
        case U_SB_FORMAT: return LAPLACE_SB_FORMAT;
        case U_SB_SP: return LAPLACE_SB_SP;
        case U_SB_LOWER: return LAPLACE_SB_LOWER;
        case U_SB_UPPER: return LAPLACE_SB_UPPER;
        case U_SB_OLETTER: return LAPLACE_SB_OLETTER;
        case U_SB_NUMERIC: return LAPLACE_SB_NUMERIC;
        case U_SB_ATERM: return LAPLACE_SB_ATERM;
        case U_SB_SCONTINUE: return LAPLACE_SB_SCONTINUE;
        case U_SB_STERM: return LAPLACE_SB_STERM;
        case U_SB_CLOSE: return LAPLACE_SB_CLOSE;
        default: return LAPLACE_SB_OTHER;
    }
}

uint8_t codepoint_table_incb(uint32_t cp) {
    if (!lp_unicode_scalar(cp)) return LAPLACE_INCB_NONE;
    switch (u_getIntPropertyValue((UChar32)cp, UCHAR_INDIC_CONJUNCT_BREAK)) {
        case U_INCB_EXTEND: return LAPLACE_INCB_EXTEND;
        case U_INCB_LINKER: return LAPLACE_INCB_LINKER;
        case U_INCB_CONSONANT: return LAPLACE_INCB_CONSONANT;
        default: return LAPLACE_INCB_NONE;
    }
}

uint8_t codepoint_table_ccc(uint32_t cp) {
    return lp_unicode_scalar(cp) ? u_getCombiningClass((UChar32)cp) : 0;
}
int laplace_codepoint_is_whitespace(uint32_t cp) {
    return lp_unicode_scalar(cp) && u_hasBinaryProperty((UChar32)cp, UCHAR_WHITE_SPACE);
}
uint32_t codepoint_table_flags(uint32_t cp) {
    return laplace_pc_pack_flags(codepoint_table_gb(cp), codepoint_table_wb(cp),
        codepoint_table_sb(cp), codepoint_table_incb(cp), codepoint_table_ccc(cp),
        (uint8_t)laplace_codepoint_is_whitespace(cp));
}

#ifdef _WIN32
#define PROPERTY_TLS __declspec(thread)
#else
#define PROPERTY_TLS _Thread_local
#endif

int codepoint_table_decompose(uint32_t cp, const uint32_t **sequence, uint32_t *count) {
    static PROPERTY_TLS uint32_t scalars[32];
    if (!sequence || !count || !lp_unicode_scalar(cp)) return 0;
    *sequence = NULL; *count = 0;
    UErrorCode status = U_ZERO_ERROR;
    const UNormalizer2 *nfc = unorm2_getNFCInstance(&status);
    UChar raw[32];
    int32_t length = U_FAILURE(status) ? -1
        : unorm2_getRawDecomposition(nfc, (UChar32)cp, raw, 32, &status);
    if (length < 0 || U_FAILURE(status)) return 0;
    for (int32_t at = 0; at < length;) {
        UChar32 value; U16_NEXT(raw, at, length, value);
        scalars[(*count)++] = (uint32_t)value;
    }
    *sequence = scalars; return 1;
}

int codepoint_table_compose(uint32_t first, uint32_t second, uint32_t *out) {
    if (!out || !lp_unicode_scalar(first) || !lp_unicode_scalar(second)) return 0;
    UErrorCode status = U_ZERO_ERROR;
    const UNormalizer2 *nfc = unorm2_getNFCInstance(&status);
    if (U_FAILURE(status)) return 0;
    UChar32 result = unorm2_composePair(nfc, (UChar32)first, (UChar32)second);
    if (result < 0) return 0;
    *out = (uint32_t)result; return 1;
}
