#include "laplace/core/word_break.h"

#include "laplace/core/codepoint_table.h"

/* === UAX#29 word boundary state machine ===
 *
 * Key complication vs grapheme break: many rules consult "the previous
 * non-Extend|Format|ZWJ codepoint", not the literal `prev`. WB4 says
 * "ignore Extend|Format|ZWJ" between two codepoints when applying any
 * other rule.
 *
 * Implementation: maintain a sliding window of the last 2 non-ignored
 * codepoints (their wb property values), plus the immediately-prior
 * literal codepoint (for WB3 CR×LF detection). Walk forward; on each
 * step, classify curr and update the window.
 *
 * Rules implemented (UAX#29 v18):
 *   WB1:  sot ÷
 *   WB2:  ÷ eot
 *   WB3:  CR × LF
 *   WB3a: (Newline | CR | LF) ÷
 *   WB3b: ÷ (Newline | CR | LF)
 *   WB3c: ZWJ × \p{Extended_Pictographic}  (implemented below via the
 *         ExtPict table the grapheme-break codegen carries)
 *   WB3d: WSegSpace × WSegSpace
 *   WB4:  × (Extend | Format | ZWJ)  (already-walked context)
 *   WB5:  (ALetter|Hebrew_Letter) × (ALetter|Hebrew_Letter)
 *   WB6:  (ALetter|Hebrew_Letter) × (MidLetter|MidNumLet|Single_Quote)
 *           (ALetter|Hebrew_Letter)
 *   WB7:  (ALetter|Hebrew_Letter) (MidLetter|MidNumLet|Single_Quote) ×
 *           (ALetter|Hebrew_Letter)
 *   WB7a: Hebrew_Letter × Single_Quote
 *   WB7b: Hebrew_Letter × Double_Quote Hebrew_Letter
 *   WB7c: Hebrew_Letter Double_Quote × Hebrew_Letter
 *   WB8:  Numeric × Numeric
 *   WB9:  (ALetter|Hebrew_Letter) × Numeric
 *   WB10: Numeric × (ALetter|Hebrew_Letter)
 *   WB11: Numeric (MidNum|MidNumLet|Single_Quote) × Numeric
 *   WB12: Numeric × (MidNum|MidNumLet|Single_Quote) Numeric
 *   WB13: Katakana × Katakana
 *   WB13a: (ALetter|Hebrew|Numeric|Katakana|ExtendNumLet) × ExtendNumLet
 *   WB13b: ExtendNumLet × (ALetter|Hebrew|Numeric|Katakana)
 *   WB15: sot (RI RI)* RI × RI
 *   WB16: [^RI] (RI RI)* RI × RI
 *   WB999: any ÷ any
 */

static uint8_t wb(uint32_t cp) { return codepoint_table_wb(cp); }
static uint8_t gb(uint32_t cp) { return codepoint_table_gb(cp); }

/* True iff this property should be SKIPPED for the "previous" lookup
 * in rules WB5+ (the WB4 "Ignore" set: Extend, Format, ZWJ). */
static int is_ignored_for_prev(uint8_t p) {
    return p == LAPLACE_WB_EXTEND
        || p == LAPLACE_WB_FORMAT
        || p == LAPLACE_WB_ZWJ;
}

/* AHLetter helper: ALetter or Hebrew_Letter */
static int is_ahletter(uint8_t p) {
    return p == LAPLACE_WB_ALETTER || p == LAPLACE_WB_HEBREW_LETTER;
}

/* MidLetterOrSingleQuote: MidLetter | MidNumLet | Single_Quote */
static int is_midletter_or_sq(uint8_t p) {
    return p == LAPLACE_WB_MIDLETTER
        || p == LAPLACE_WB_MIDNUMLET
        || p == LAPLACE_WB_SINGLE_QUOTE;
}

/* MidNumOrSingleQuote: MidNum | MidNumLet | Single_Quote */
static int is_midnum_or_sq(uint8_t p) {
    return p == LAPLACE_WB_MIDNUM
        || p == LAPLACE_WB_MIDNUMLET
        || p == LAPLACE_WB_SINGLE_QUOTE;
}

/* AHLetter | Numeric | Katakana | ExtendNumLet */
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

/* Find the index of the most-recent "significant" codepoint at or before
 * idx, skipping WB4-ignored properties. Returns SIZE_MAX if none exists
 * (e.g. start of input). */
static size_t prev_significant_idx(const uint32_t* cps, size_t idx_inclusive_upper) {
    /* idx_inclusive_upper is the last index we may consider. */
    size_t k = idx_inclusive_upper;
    for (;;) {
        if (!is_ignored_for_prev(wb(cps[k]))) return k;
        if (k == 0) return SIZE_MAX;
        k -= 1;
    }
}

/* Find the significant codepoint BEFORE `idx` (exclusive), skipping
 * ignored. Returns SIZE_MAX if none. */
static size_t prev_significant_before(const uint32_t* cps, size_t idx) {
    if (idx == 0) return SIZE_MAX;
    return prev_significant_idx(cps, idx - 1);
}

size_t laplace_word_break_next(const uint32_t* codepoints, size_t n, size_t from) {
    if (from >= n) return n;
    if (from + 1 >= n) return n;  /* WB2 sole boundary at n */

    /* RI-run-length: count contiguous RIs ending at (just-before-curr).
     * Reset when a non-RI significant codepoint appears. */
    size_t ri_run_len = 0;

    /* Seed RI count from history before `from` + from itself. */
    {
        size_t k = from;
        while (1) {
            if (wb(codepoints[k]) == LAPLACE_WB_REGIONAL_INDICATOR) ri_run_len += 1;
            else { ri_run_len = (wb(codepoints[from]) == LAPLACE_WB_REGIONAL_INDICATOR) ? 1 : 0; break; }
            if (k == 0) break;
            k -= 1;
        }
    }

    size_t i = from + 1;
    while (i < n) {
        uint32_t prev = codepoints[i - 1];
        uint32_t curr = codepoints[i];
        uint8_t p_lit = wb(prev);
        uint8_t c     = wb(curr);

        /* WB3: CR × LF (literal — Extend|Format|ZWJ NOT skipped here). */
        if (p_lit == LAPLACE_WB_CR && c == LAPLACE_WB_LF) {
            /* no break — fall through to advance */
            goto advance_no_break;
        }

        /* WB3a: (Newline | CR | LF) ÷  */
        if (p_lit == LAPLACE_WB_NEWLINE || p_lit == LAPLACE_WB_CR || p_lit == LAPLACE_WB_LF) {
            return i;
        }
        /* WB3b: ÷ (Newline | CR | LF) */
        if (c == LAPLACE_WB_NEWLINE || c == LAPLACE_WB_CR || c == LAPLACE_WB_LF) {
            return i;
        }

        /* WB3c: ZWJ × \p{Extended_Pictographic} */
        if (p_lit == LAPLACE_WB_ZWJ && gb(curr) == LAPLACE_GB_EXTENDED_PICTOGRAPHIC) {
            goto advance_no_break;
        }

        /* WB3d: WSegSpace × WSegSpace */
        if (p_lit == LAPLACE_WB_WSEGSPACE && c == LAPLACE_WB_WSEGSPACE) {
            goto advance_no_break;
        }

        /* WB4: × (Extend | Format | ZWJ) — never break before these. */
        if (c == LAPLACE_WB_EXTEND || c == LAPLACE_WB_FORMAT || c == LAPLACE_WB_ZWJ) {
            goto advance_no_break;
        }

        /* For WB5+: use the previous significant codepoint (skipping
         * Extend|Format|ZWJ). */
        size_t prev_sig_idx = prev_significant_before(codepoints, i);
        uint8_t p = (prev_sig_idx == SIZE_MAX) ? 0xFF : wb(codepoints[prev_sig_idx]);

        if (p == 0xFF) {
            /* No significant prior codepoint — fall through to WB999. */
            return i;
        }

        /* WB5: AHLetter × AHLetter */
        if (is_ahletter(p) && is_ahletter(c)) goto advance_no_break;

        /* WB6: AHLetter × (MidLetter|MidNumLet|Single_Quote) AHLetter */
        if (is_ahletter(p) && is_midletter_or_sq(c)) {
            /* Look ahead past Extend|Format|ZWJ to next significant. */
            size_t j = i + 1;
            while (j < n && is_ignored_for_prev(wb(codepoints[j]))) j += 1;
            if (j < n && is_ahletter(wb(codepoints[j]))) goto advance_no_break;
        }

        /* WB7: AHLetter (MidLetter|MidNumLet|Single_Quote) × AHLetter */
        if (is_midletter_or_sq(p) && is_ahletter(c)) {
            /* Previous significant before p must be AHLetter. */
            size_t pp_idx = (prev_sig_idx == 0) ? SIZE_MAX
                          : prev_significant_idx(codepoints, prev_sig_idx - 1);
            if (pp_idx != SIZE_MAX && is_ahletter(wb(codepoints[pp_idx]))) goto advance_no_break;
        }

        /* WB7a: Hebrew_Letter × Single_Quote */
        if (p == LAPLACE_WB_HEBREW_LETTER && c == LAPLACE_WB_SINGLE_QUOTE) goto advance_no_break;

        /* WB7b: Hebrew_Letter × Double_Quote Hebrew_Letter */
        if (p == LAPLACE_WB_HEBREW_LETTER && c == LAPLACE_WB_DOUBLE_QUOTE) {
            size_t j = i + 1;
            while (j < n && is_ignored_for_prev(wb(codepoints[j]))) j += 1;
            if (j < n && wb(codepoints[j]) == LAPLACE_WB_HEBREW_LETTER) goto advance_no_break;
        }

        /* WB7c: Hebrew_Letter Double_Quote × Hebrew_Letter */
        if (p == LAPLACE_WB_DOUBLE_QUOTE && c == LAPLACE_WB_HEBREW_LETTER) {
            size_t pp_idx = (prev_sig_idx == 0) ? SIZE_MAX
                          : prev_significant_idx(codepoints, prev_sig_idx - 1);
            if (pp_idx != SIZE_MAX && wb(codepoints[pp_idx]) == LAPLACE_WB_HEBREW_LETTER) goto advance_no_break;
        }

        /* WB8: Numeric × Numeric */
        if (p == LAPLACE_WB_NUMERIC && c == LAPLACE_WB_NUMERIC) goto advance_no_break;

        /* WB9: AHLetter × Numeric */
        if (is_ahletter(p) && c == LAPLACE_WB_NUMERIC) goto advance_no_break;

        /* WB10: Numeric × AHLetter */
        if (p == LAPLACE_WB_NUMERIC && is_ahletter(c)) goto advance_no_break;

        /* WB11: Numeric (MidNum|MidNumLet|Single_Quote) × Numeric */
        if (is_midnum_or_sq(p) && c == LAPLACE_WB_NUMERIC) {
            size_t pp_idx = (prev_sig_idx == 0) ? SIZE_MAX
                          : prev_significant_idx(codepoints, prev_sig_idx - 1);
            if (pp_idx != SIZE_MAX && wb(codepoints[pp_idx]) == LAPLACE_WB_NUMERIC) goto advance_no_break;
        }

        /* WB12: Numeric × (MidNum|MidNumLet|Single_Quote) Numeric */
        if (p == LAPLACE_WB_NUMERIC && is_midnum_or_sq(c)) {
            size_t j = i + 1;
            while (j < n && is_ignored_for_prev(wb(codepoints[j]))) j += 1;
            if (j < n && wb(codepoints[j]) == LAPLACE_WB_NUMERIC) goto advance_no_break;
        }

        /* WB13: Katakana × Katakana */
        if (p == LAPLACE_WB_KATAKANA && c == LAPLACE_WB_KATAKANA) goto advance_no_break;

        /* WB13a: (AHLetter|Numeric|Katakana|ExtendNumLet) × ExtendNumLet */
        if (is_wb13a_left(p) && c == LAPLACE_WB_EXTENDNUMLET) goto advance_no_break;

        /* WB13b: ExtendNumLet × (AHLetter|Numeric|Katakana) */
        if (p == LAPLACE_WB_EXTENDNUMLET && is_wb13b_right(c)) goto advance_no_break;

        /* WB15 + WB16: RI × RI when prev RI run is odd. */
        if (p == LAPLACE_WB_REGIONAL_INDICATOR
            && c == LAPLACE_WB_REGIONAL_INDICATOR
            && (ri_run_len % 2) == 1) {
            goto advance_no_break;
        }

        /* WB999: any ÷ any */
        return i;

advance_no_break:
        /* Update RI run length. RI counter increments when curr is RI
         * (regardless of prev); reset to 0 when curr is a non-ignored
         * non-RI codepoint. WB4-ignored codepoints (Extend|Format|ZWJ)
         * do NOT reset the RI counter (so e.g. RI + Extend + RI still
         * pairs the RIs). */
        if (c == LAPLACE_WB_REGIONAL_INDICATOR) {
            ri_run_len += 1;
        } else if (!is_ignored_for_prev(c)) {
            ri_run_len = 0;
        }
        i += 1;
    }
    return n;  /* WB2 */
}
