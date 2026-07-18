#include "laplace/core/word_break.h"

#include "laplace/core/codepoint_table.h"

static uint8_t wb(uint32_t cp) { return codepoint_table_wb(cp); }
static uint8_t gb(uint32_t cp) { return codepoint_table_gb(cp); }

static int is_ignored_for_prev(uint8_t p) {
    return p == LAPLACE_WB_EXTEND
        || p == LAPLACE_WB_FORMAT
        || p == LAPLACE_WB_ZWJ;
}

static int is_ahletter(uint8_t p) {
    return p == LAPLACE_WB_ALETTER || p == LAPLACE_WB_HEBREW_LETTER;
}

static int is_midletter_or_sq(uint8_t p) {
    return p == LAPLACE_WB_MIDLETTER
        || p == LAPLACE_WB_MIDNUMLET
        || p == LAPLACE_WB_SINGLE_QUOTE;
}

static int is_midnum_or_sq(uint8_t p) {
    return p == LAPLACE_WB_MIDNUM
        || p == LAPLACE_WB_MIDNUMLET
        || p == LAPLACE_WB_SINGLE_QUOTE;
}

static int is_wb13a_left(uint8_t p) {
    return is_ahletter(p)
        || p == LAPLACE_WB_NUMERIC
        || p == LAPLACE_WB_KATAKANA
        || p == LAPLACE_WB_EXTENDNUMLET;
}
static int is_wb13b_right(uint8_t p) {
    return is_ahletter(p)
        || p == LAPLACE_WB_NUMERIC
        || p == LAPLACE_WB_KATAKANA;
}

static size_t prev_significant_idx(const uint32_t* cps, size_t idx_inclusive_upper) {
    size_t k = idx_inclusive_upper;
    for (;;) {
        if (!is_ignored_for_prev(wb(cps[k]))) return k;
        if (k == 0) return SIZE_MAX;
        k -= 1;
    }
}

static size_t prev_significant_before(const uint32_t* cps, size_t idx) {
    if (idx == 0) return SIZE_MAX;
    return prev_significant_idx(cps, idx - 1);
}

size_t laplace_word_break_next(const uint32_t* codepoints, size_t n, size_t from) {
    if (from >= n) return n;
    if (from + 1 >= n) return n;

    size_t ri_run_len = 0;

    {
        size_t k = from;
        while (1) {
            if (wb(codepoints[k]) == LAPLACE_WB_REGIONAL_INDICATOR) ri_run_len += 1;
            else { ri_run_len = (wb(codepoints[from]) == LAPLACE_WB_REGIONAL_INDICATOR) ? 1 : 0; break; }
            if (k == 0) break;
            k -= 1;
        }
    }

    /*
     * WB-class slide: the previous position's class (`p_lit`, i.e. wb(cps[i-1]))
     * is carried across iterations instead of re-derived. On advance_no_break `i`
     * grows by 1, so the next iteration's p_lit is exactly this iteration's `c`.
     * This drops one codepoint_table lookup per hot-loop step (main derived both
     * wb(cps[i-1]) and wb(cps[i]) every step) with ZERO heap allocation and values
     * byte-identical to calling wb() directly. The prev_significant / lookahead
     * scans keep calling wb() as before, so all UAX#29 rule logic is unchanged.
     */
    uint8_t p_lit = wb(codepoints[from]);
    size_t i = from + 1;
    while (i < n) {
        uint32_t curr = codepoints[i];
        uint8_t c     = wb(curr);

        if (p_lit == LAPLACE_WB_CR && c == LAPLACE_WB_LF) {
            goto advance_no_break;
        }

        if (p_lit == LAPLACE_WB_NEWLINE || p_lit == LAPLACE_WB_CR || p_lit == LAPLACE_WB_LF) {
            return i;
        }
        if (c == LAPLACE_WB_NEWLINE || c == LAPLACE_WB_CR || c == LAPLACE_WB_LF) {
            return i;
        }

        if (p_lit == LAPLACE_WB_ZWJ && gb(curr) == LAPLACE_GB_EXTENDED_PICTOGRAPHIC) {
            goto advance_no_break;
        }

        if (p_lit == LAPLACE_WB_WSEGSPACE && c == LAPLACE_WB_WSEGSPACE) {
            goto advance_no_break;
        }

        if (c == LAPLACE_WB_EXTEND || c == LAPLACE_WB_FORMAT || c == LAPLACE_WB_ZWJ) {
            goto advance_no_break;
        }

        size_t prev_sig_idx = prev_significant_before(codepoints, i);
        uint8_t p = (prev_sig_idx == SIZE_MAX) ? 0xFF : wb(codepoints[prev_sig_idx]);

        if (p == 0xFF) {
            return i;
        }

        if (is_ahletter(p) && is_ahletter(c)) goto advance_no_break;

        if (is_ahletter(p) && is_midletter_or_sq(c)) {
            size_t j = i + 1;
            while (j < n && is_ignored_for_prev(wb(codepoints[j]))) j += 1;
            if (j < n && is_ahletter(wb(codepoints[j]))) goto advance_no_break;
        }

        if (is_midletter_or_sq(p) && is_ahletter(c)) {
            size_t pp_idx = (prev_sig_idx == 0) ? SIZE_MAX
                          : prev_significant_idx(codepoints, prev_sig_idx - 1);
            if (pp_idx != SIZE_MAX && is_ahletter(wb(codepoints[pp_idx]))) goto advance_no_break;
        }

        if (p == LAPLACE_WB_HEBREW_LETTER && c == LAPLACE_WB_SINGLE_QUOTE) goto advance_no_break;

        if (p == LAPLACE_WB_HEBREW_LETTER && c == LAPLACE_WB_DOUBLE_QUOTE) {
            size_t j = i + 1;
            while (j < n && is_ignored_for_prev(wb(codepoints[j]))) j += 1;
            if (j < n && wb(codepoints[j]) == LAPLACE_WB_HEBREW_LETTER) goto advance_no_break;
        }

        if (p == LAPLACE_WB_DOUBLE_QUOTE && c == LAPLACE_WB_HEBREW_LETTER) {
            size_t pp_idx = (prev_sig_idx == 0) ? SIZE_MAX
                          : prev_significant_idx(codepoints, prev_sig_idx - 1);
            if (pp_idx != SIZE_MAX && wb(codepoints[pp_idx]) == LAPLACE_WB_HEBREW_LETTER) goto advance_no_break;
        }

        if (p == LAPLACE_WB_NUMERIC && c == LAPLACE_WB_NUMERIC) goto advance_no_break;

        if (is_ahletter(p) && c == LAPLACE_WB_NUMERIC) goto advance_no_break;

        if (p == LAPLACE_WB_NUMERIC && is_ahletter(c)) goto advance_no_break;

        if (is_midnum_or_sq(p) && c == LAPLACE_WB_NUMERIC) {
            size_t pp_idx = (prev_sig_idx == 0) ? SIZE_MAX
                          : prev_significant_idx(codepoints, prev_sig_idx - 1);
            if (pp_idx != SIZE_MAX && wb(codepoints[pp_idx]) == LAPLACE_WB_NUMERIC) goto advance_no_break;
        }

        if (p == LAPLACE_WB_NUMERIC && is_midnum_or_sq(c)) {
            size_t j = i + 1;
            while (j < n && is_ignored_for_prev(wb(codepoints[j]))) j += 1;
            if (j < n && wb(codepoints[j]) == LAPLACE_WB_NUMERIC) goto advance_no_break;
        }

        if (p == LAPLACE_WB_KATAKANA && c == LAPLACE_WB_KATAKANA) goto advance_no_break;

        if (is_wb13a_left(p) && c == LAPLACE_WB_EXTENDNUMLET) goto advance_no_break;

        if (p == LAPLACE_WB_EXTENDNUMLET && is_wb13b_right(c)) goto advance_no_break;

        if (p == LAPLACE_WB_REGIONAL_INDICATOR
            && c == LAPLACE_WB_REGIONAL_INDICATOR
            && (ri_run_len % 2) == 1) {
            goto advance_no_break;
        }

        return i;

advance_no_break:
        if (c == LAPLACE_WB_REGIONAL_INDICATOR) {
            ri_run_len += 1;
        } else if (!is_ignored_for_prev(c)) {
            ri_run_len = 0;
        }
        p_lit = c;
        i += 1;
    }
    return n;
}
