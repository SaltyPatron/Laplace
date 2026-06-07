#include "laplace/core/grapheme_break.h"

#include "laplace/core/codepoint_table.h"

static uint8_t gb(uint32_t cp) { return codepoint_table_gb(cp); }

typedef struct {
    size_t ri_run_len;
    int    in_emoji_zwj_seq;
} gb_state_t;

static int no_break(uint32_t prev, uint32_t curr, gb_state_t* st) {
    const uint8_t p = gb(prev);
    const uint8_t c = gb(curr);

    if (p == LAPLACE_GB_CR && c == LAPLACE_GB_LF) return 1;

    if (p == LAPLACE_GB_CONTROL || p == LAPLACE_GB_CR || p == LAPLACE_GB_LF) return 0;

    if (c == LAPLACE_GB_CONTROL || c == LAPLACE_GB_CR || c == LAPLACE_GB_LF) return 0;

    if (p == LAPLACE_GB_L
        && (c == LAPLACE_GB_L || c == LAPLACE_GB_V
            || c == LAPLACE_GB_LV || c == LAPLACE_GB_LVT)) return 1;

    if ((p == LAPLACE_GB_LV || p == LAPLACE_GB_V)
        && (c == LAPLACE_GB_V || c == LAPLACE_GB_T)) return 1;

    if ((p == LAPLACE_GB_LVT || p == LAPLACE_GB_T)
        && c == LAPLACE_GB_T) return 1;

    if (c == LAPLACE_GB_EXTEND || c == LAPLACE_GB_ZWJ) return 1;

    if (c == LAPLACE_GB_SPACINGMARK) return 1;

    if (p == LAPLACE_GB_PREPEND) return 1;

    if (st->in_emoji_zwj_seq
        && p == LAPLACE_GB_ZWJ
        && c == LAPLACE_GB_EXTENDED_PICTOGRAPHIC) {
        return 1;
    }

    if (p == LAPLACE_GB_REGIONAL_INDICATOR
        && c == LAPLACE_GB_REGIONAL_INDICATOR
        && (st->ri_run_len % 2) == 1) {
        return 1;
    }

    return 0;
}

static void update_state(uint32_t prev, uint32_t curr, gb_state_t* st) {
    const uint8_t p = gb(prev);
    const uint8_t c = gb(curr);

    if (c == LAPLACE_GB_REGIONAL_INDICATOR) {
        if (p == LAPLACE_GB_REGIONAL_INDICATOR) st->ri_run_len += 1;
        else st->ri_run_len = 1;
    } else {
        st->ri_run_len = 0;
    }

    if (c == LAPLACE_GB_EXTENDED_PICTOGRAPHIC) {
        st->in_emoji_zwj_seq = 0;
    }
    if (c == LAPLACE_GB_EXTEND) {
        if (p == LAPLACE_GB_EXTENDED_PICTOGRAPHIC || st->in_emoji_zwj_seq) {
        }
    }
    if (c == LAPLACE_GB_ZWJ) {
        if (p == LAPLACE_GB_EXTENDED_PICTOGRAPHIC
            || (p == LAPLACE_GB_EXTEND && st->in_emoji_zwj_seq)
            || (p == LAPLACE_GB_EXTEND && 1)) {
        }
    }
}

static uint8_t incb(uint32_t cp) { return codepoint_table_incb(cp); }

size_t laplace_grapheme_break_next(const uint32_t* codepoints, size_t n, size_t from) {
    if (from >= n) return n;
    if (from + 1 >= n) return n;

    size_t i = from + 1;
    gb_state_t st = { .ri_run_len = 0, .in_emoji_zwj_seq = 0 };

    int saw_indic_consonant = 0;
    int saw_indic_linker_after_consonant = 0;

    if (from > 0) {
        size_t k = from;
        while (k > 0 && gb(codepoints[k - 1]) == LAPLACE_GB_REGIONAL_INDICATOR) {
            st.ri_run_len += 1;
            k -= 1;
        }
    }

    int saw_pictograph_in_cluster = 0;
    if (from > 0) {
        size_t k = from;
        while (k > 0) {
            uint8_t pp = gb(codepoints[k - 1]);
            if (pp == LAPLACE_GB_EXTENDED_PICTOGRAPHIC) {
                saw_pictograph_in_cluster = 1;
                break;
            }
            if (pp == LAPLACE_GB_EXTEND || pp == LAPLACE_GB_ZWJ) {
                k -= 1;
                continue;
            }
            break;
        }
    }

    if (from > 0) {
        size_t k = from;
        int seen_linker = 0;
        while (k > 0) {
            uint8_t ip = incb(codepoints[k - 1]);
            if (ip == LAPLACE_INCB_LINKER) { seen_linker = 1; k -= 1; continue; }
            if (ip == LAPLACE_INCB_EXTEND) { k -= 1; continue; }
            if (ip == LAPLACE_INCB_CONSONANT) {
                saw_indic_consonant = 1;
                saw_indic_linker_after_consonant = seen_linker;
            }
            break;
        }
    }

    {
        uint8_t cp0_gb = gb(codepoints[from]);
        uint8_t cp0_incb = incb(codepoints[from]);
        if (cp0_gb == LAPLACE_GB_EXTENDED_PICTOGRAPHIC) saw_pictograph_in_cluster = 1;
        if (cp0_gb == LAPLACE_GB_REGIONAL_INDICATOR) {
            st.ri_run_len += 1;
            if (from == 0) st.ri_run_len = 1;
        }
        if (cp0_incb == LAPLACE_INCB_CONSONANT) {
            saw_indic_consonant = 1;
            saw_indic_linker_after_consonant = 0;
        }
    }

    while (i < n) {
        uint32_t prev = codepoints[i - 1];
        uint32_t curr = codepoints[i];
        uint8_t p = gb(prev);
        uint8_t c = gb(curr);

        st.in_emoji_zwj_seq = (saw_pictograph_in_cluster && p == LAPLACE_GB_ZWJ) ? 1 : 0;

        int gb9c_fires = (saw_indic_consonant
                          && saw_indic_linker_after_consonant
                          && incb(curr) == LAPLACE_INCB_CONSONANT);

        if (!gb9c_fires && !no_break(prev, curr, &st)) {
            return i;
        }

        if (c == LAPLACE_GB_REGIONAL_INDICATOR) {
            if (p == LAPLACE_GB_REGIONAL_INDICATOR) st.ri_run_len += 1;
            else st.ri_run_len = 1;
        } else {
            st.ri_run_len = 0;
        }

        if (c == LAPLACE_GB_EXTENDED_PICTOGRAPHIC) {
            saw_pictograph_in_cluster = 1;
        } else if (c == LAPLACE_GB_EXTEND || c == LAPLACE_GB_ZWJ) {
        } else {
            saw_pictograph_in_cluster = 0;
        }

        uint8_t curr_incb = incb(curr);
        if (curr_incb == LAPLACE_INCB_CONSONANT) {
            saw_indic_consonant = 1;
            saw_indic_linker_after_consonant = 0;
        } else if (curr_incb == LAPLACE_INCB_LINKER) {
            if (saw_indic_consonant) saw_indic_linker_after_consonant = 1;
        } else if (curr_incb == LAPLACE_INCB_EXTEND) {
        } else {
            saw_indic_consonant = 0;
            saw_indic_linker_after_consonant = 0;
        }

        i += 1;
    }
    return n;
}

__attribute__((unused))
static void __keep_update_state_alive(void) {
    gb_state_t s = {0};
    update_state(0, 0, &s);
}
