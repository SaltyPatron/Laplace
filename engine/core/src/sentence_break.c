#include "laplace/core/sentence_break.h"

#include "laplace/core/codepoint_table.h"

/* === UAX#29 sentence boundary state machine ===
 *
 * Rules (UAX#29):
 *   SB1:  sot ÷
 *   SB2:  ÷ eot
 *   SB3:  CR × LF
 *   SB4:  (Sep | CR | LF) ÷
 *   SB5:  × (Extend | Format)  (ignored)
 *   SB6:  ATerm × Numeric
 *   SB7:  (Upper|Lower) ATerm × Upper
 *   SB8:  ATerm Close* Sp* × ¬(OLetter|Upper|Lower|Sep|CR|LF|STerm|ATerm)*
 *          Lower
 *   SB8a: (STerm|ATerm) Close* Sp* × (SContinue|STerm|ATerm)
 *   SB9:  (STerm|ATerm) Close* × (Close | Sp | Sep | CR | LF)
 *   SB10: (STerm|ATerm) Close* Sp* × (Sp | Sep | CR | LF)
 *   SB11: (STerm|ATerm) Close* Sp* (Sep|CR|LF)? ÷
 *   SB998: any × any  (default no break for sentence) */

static uint8_t sb(uint32_t cp) { return codepoint_table_sb(cp); }

static int is_ignored(uint8_t p) {
    return p == LAPLACE_SB_EXTEND || p == LAPLACE_SB_FORMAT;
}

/* Previous significant property at-or-before idx (skipping Extend|Format),
 * or 0xFF if none. */
static uint8_t prev_sig_at_or_before(const uint32_t* cps, size_t idx) {
    for (;;) {
        uint8_t v = sb(cps[idx]);
        if (!is_ignored(v)) return v;
        if (idx == 0) return 0xFF;
        idx -= 1;
    }
}

/* Returns 1 iff the codepoints strictly before `i` form a tail of the
 * pattern (STerm|ATerm) Close* Sp*. Also reports via out parameters
 * whether the terminator was specifically ATerm (1=ATerm, 0=STerm) and
 * whether Sp* was non-empty (had_spaces) — those are needed by SB7/SB8.
 * Returns 0 if the pattern doesn't match.
 *
 * The walk skips Extend|Format. */
static int trailing_term_pattern(const uint32_t* cps, size_t i,
                                  int* out_is_aterm, int* out_had_spaces) {
    int phase_sp_ok = 1;        /* still in Sp* tail */
    int phase_close_ok = 1;     /* may transition into Close* */
    int had_sp = 0;
    if (i == 0) return 0;
    size_t k = i;
    while (k > 0) {
        k -= 1;
        uint8_t v = sb(cps[k]);
        if (is_ignored(v)) continue;
        if (phase_sp_ok && v == LAPLACE_SB_SP) { had_sp = 1; continue; }
        phase_sp_ok = 0;
        if (phase_close_ok && v == LAPLACE_SB_CLOSE) { continue; }
        phase_close_ok = 0;
        if (v == LAPLACE_SB_ATERM) { *out_is_aterm = 1; *out_had_spaces = had_sp; return 1; }
        if (v == LAPLACE_SB_STERM) { *out_is_aterm = 0; *out_had_spaces = had_sp; return 1; }
        return 0;
    }
    return 0;
}

/* SB8 lookahead: scanning forward from i (the codepoint right after Sp*),
 * skipping Extend|Format, return 1 iff we hit a Lower BEFORE hitting any
 * of {OLetter, Upper, Lower-of-other-kind-no, Sep, CR, LF, STerm, ATerm}.
 *
 * Actually per rule SB8: × ¬(OLetter|Upper|Lower|Sep|CR|LF|STerm|ATerm)*
 *                          Lower
 * — meaning: starting from `i`, scan forward through codepoints whose
 * property is NOT in {OLetter,Upper,Lower,Sep,CR,LF,STerm,ATerm}; if we
 * eventually arrive at a Lower codepoint, the rule fires (no break). */
static int sb8_lookahead_finds_lower(const uint32_t* cps, size_t n, size_t i) {
    for (size_t k = i; k < n; ++k) {
        uint8_t v = sb(cps[k]);
        if (is_ignored(v)) continue;
        if (v == LAPLACE_SB_OLETTER
         || v == LAPLACE_SB_UPPER
         || v == LAPLACE_SB_SEP
         || v == LAPLACE_SB_CR
         || v == LAPLACE_SB_LF
         || v == LAPLACE_SB_STERM
         || v == LAPLACE_SB_ATERM) {
            return v == LAPLACE_SB_LOWER ? 1 : 0;  /* Lower means rule fires */
        }
        if (v == LAPLACE_SB_LOWER) return 1;
        /* else: any other property, keep scanning */
    }
    return 0;
}

/* SB7 helper: the codepoint two positions before sig-prev was Upper|Lower,
 * the next was ATerm, and curr (already known) is Upper. */
static int sb7_matches(const uint32_t* cps, size_t prev_sig_idx, uint8_t c) {
    if (c != LAPLACE_SB_UPPER) return 0;
    if (prev_sig_idx == SIZE_MAX) return 0;
    if (sb(cps[prev_sig_idx]) != LAPLACE_SB_ATERM) return 0;
    if (prev_sig_idx == 0) return 0;
    /* walk back one significant codepoint */
    size_t k = prev_sig_idx;
    while (k > 0) {
        k -= 1;
        uint8_t v = sb(cps[k]);
        if (is_ignored(v)) continue;
        return v == LAPLACE_SB_UPPER || v == LAPLACE_SB_LOWER;
    }
    return 0;
}

size_t laplace_sentence_break_next(const uint32_t* codepoints, size_t n, size_t from) {
    if (from >= n) return n;
    if (from + 1 >= n) return n;  /* SB2 */

    size_t i = from + 1;
    while (i < n) {
        uint32_t prev = codepoints[i - 1];
        uint32_t curr = codepoints[i];
        uint8_t p_lit = sb(prev);
        uint8_t c     = sb(curr);

        /* SB3: CR × LF (literal) */
        if (p_lit == LAPLACE_SB_CR && c == LAPLACE_SB_LF) { i += 1; continue; }

        /* SB4: (Sep | CR | LF) ÷ (literal) */
        if (p_lit == LAPLACE_SB_SEP || p_lit == LAPLACE_SB_CR || p_lit == LAPLACE_SB_LF) {
            return i;
        }

        /* SB5: × (Extend|Format) */
        if (c == LAPLACE_SB_EXTEND || c == LAPLACE_SB_FORMAT) { i += 1; continue; }

        /* From here, use the previous significant property. */
        uint8_t p = 0xFF;
        size_t prev_sig_idx = SIZE_MAX;
        if (i > 0) {
            size_t k = i - 1;
            for (;;) {
                uint8_t v = sb(codepoints[k]);
                if (!is_ignored(v)) { p = v; prev_sig_idx = k; break; }
                if (k == 0) break;
                k -= 1;
            }
        }

        /* SB6: ATerm × Numeric */
        if (p == LAPLACE_SB_ATERM && c == LAPLACE_SB_NUMERIC) { i += 1; continue; }

        /* SB7: (Upper|Lower) ATerm × Upper */
        if (sb7_matches(codepoints, prev_sig_idx, c)) { i += 1; continue; }

        /* Detect tail (STerm|ATerm) Close* Sp* ending at i-1. If so,
         * SB8/SB8a/SB9/SB10/SB11 may fire. */
        int is_aterm = 0, had_sp = 0;
        if (trailing_term_pattern(codepoints, i, &is_aterm, &had_sp)) {
            /* SB8: ATerm Close* Sp* × ¬(...)* Lower
             * Only ATerm matters here, not STerm. */
            if (is_aterm && sb8_lookahead_finds_lower(codepoints, n, i)) {
                i += 1; continue;
            }
            /* SB8a: (STerm|ATerm) Close* Sp* × (SContinue|STerm|ATerm) */
            if (c == LAPLACE_SB_SCONTINUE
             || c == LAPLACE_SB_STERM
             || c == LAPLACE_SB_ATERM) { i += 1; continue; }
            /* SB9: (STerm|ATerm) Close* × (Close|Sp|Sep|CR|LF)
             *     — applies only if Sp* was empty. */
            if (!had_sp
                && (c == LAPLACE_SB_CLOSE
                 || c == LAPLACE_SB_SP
                 || c == LAPLACE_SB_SEP
                 || c == LAPLACE_SB_CR
                 || c == LAPLACE_SB_LF)) {
                i += 1; continue;
            }
            /* SB10: (STerm|ATerm) Close* Sp* × (Sp|Sep|CR|LF) */
            if (c == LAPLACE_SB_SP
             || c == LAPLACE_SB_SEP
             || c == LAPLACE_SB_CR
             || c == LAPLACE_SB_LF) {
                i += 1; continue;
            }
            /* SB11: (STerm|ATerm) Close* Sp* (Sep|CR|LF)? ÷
             * The optional (Sep|CR|LF) preceded by the term pattern
             * would have been consumed by SB4 ÷ already, so here at
             * this point a break is the right action. */
            return i;
        }

        /* SB998: default any × any (no break) */
        i += 1;
    }
    return n;  /* SB2 */
}
