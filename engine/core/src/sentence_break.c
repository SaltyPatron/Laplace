#include "laplace/core/sentence_break.h"

#include "laplace/core/codepoint_table.h"

static uint8_t sb(uint32_t cp) { return codepoint_table_sb(cp); }

static int is_ignored(uint8_t p) {
    return p == LAPLACE_SB_EXTEND || p == LAPLACE_SB_FORMAT;
}

static int trailing_term_pattern(const uint32_t* cps, size_t i,
                                  int* out_is_aterm, int* out_had_spaces) {
    int phase_sp_ok = 1;
    int phase_close_ok = 1;
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
            return v == LAPLACE_SB_LOWER ? 1 : 0;
        }
        if (v == LAPLACE_SB_LOWER) return 1;
    }
    return 0;
}

static int sb7_matches(const uint32_t* cps, size_t prev_sig_idx, uint8_t c) {
    if (c != LAPLACE_SB_UPPER) return 0;
    if (prev_sig_idx == SIZE_MAX) return 0;
    if (sb(cps[prev_sig_idx]) != LAPLACE_SB_ATERM) return 0;
    if (prev_sig_idx == 0) return 0;
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
    if (from + 1 >= n) return n;

    size_t i = from + 1;
    while (i < n) {
        uint32_t prev = codepoints[i - 1];
        uint32_t curr = codepoints[i];
        uint8_t p_lit = sb(prev);
        uint8_t c     = sb(curr);

        if (p_lit == LAPLACE_SB_CR && c == LAPLACE_SB_LF) { i += 1; continue; }

        if (p_lit == LAPLACE_SB_SEP || p_lit == LAPLACE_SB_CR || p_lit == LAPLACE_SB_LF) {
            return i;
        }

        if (c == LAPLACE_SB_EXTEND || c == LAPLACE_SB_FORMAT) { i += 1; continue; }

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

        if (p == LAPLACE_SB_ATERM && c == LAPLACE_SB_NUMERIC) { i += 1; continue; }

        if (sb7_matches(codepoints, prev_sig_idx, c)) { i += 1; continue; }

        int is_aterm = 0, had_sp = 0;
        if (trailing_term_pattern(codepoints, i, &is_aterm, &had_sp)) {
            if (is_aterm && sb8_lookahead_finds_lower(codepoints, n, i)) {
                i += 1; continue;
            }
            if (c == LAPLACE_SB_SCONTINUE
             || c == LAPLACE_SB_STERM
             || c == LAPLACE_SB_ATERM) { i += 1; continue; }
            if (!had_sp
                && (c == LAPLACE_SB_CLOSE
                 || c == LAPLACE_SB_SP
                 || c == LAPLACE_SB_SEP
                 || c == LAPLACE_SB_CR
                 || c == LAPLACE_SB_LF)) {
                i += 1; continue;
            }
            if (c == LAPLACE_SB_SP
             || c == LAPLACE_SB_SEP
             || c == LAPLACE_SB_CR
             || c == LAPLACE_SB_LF) {
                i += 1; continue;
            }
            return i;
        }

        i += 1;
    }
    return n;
}
