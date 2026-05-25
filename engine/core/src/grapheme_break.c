#include "laplace/core/grapheme_break.h"

#include "laplace/core/codepoint_table.h"

/* === UAX#29 grapheme cluster break state machine ===
 *
 * For a given position `from`, scan forward through `codepoints` until
 * the boundary rules say "break here", then return that index.
 *
 * The rules are stateful in two ways:
 *  - Regional-indicator counting (GB12 + GB13): count the run of RI's
 *    starting at the most recent non-RI; pair them; break before any
 *    RI that would start a new pair.
 *  - Emoji ZWJ sequence (GB11): an Extended_Pictographic followed by
 *    any number of Extends, then ZWJ, joins to a following
 *    Extended_Pictographic.
 *
 * We track minimal state across each two-codepoint boundary decision +
 * a tiny lookback that captures the above two cases. */

static uint8_t gb(uint32_t cp) { return codepoint_table_gb(cp); }

/* True iff the boundary between `prev` and `curr` should NOT break,
 * given the running state (parity of preceding RI run + presence of an
 * emoji ZWJ sequence). */
typedef struct {
    /* Count of consecutive Regional_Indicator codepoints in the run
     * leading up to (and including) `prev`. 0 if `prev` is not RI. */
    size_t ri_run_len;
    /* True if the current ZWJ in `prev` is the trailing element of a
     * pattern \p{Extended_Pictographic} Extend* ZWJ. The next pictograph
     * (if any) joins via GB11. */
    int    in_emoji_zwj_seq;
} gb_state_t;

static int no_break(uint32_t prev, uint32_t curr, gb_state_t* st) {
    const uint8_t p = gb(prev);
    const uint8_t c = gb(curr);

    /* GB3: CR × LF */
    if (p == LAPLACE_GB_CR && c == LAPLACE_GB_LF) return 1;

    /* GB4: (Control | CR | LF) ÷  */
    if (p == LAPLACE_GB_CONTROL || p == LAPLACE_GB_CR || p == LAPLACE_GB_LF) return 0;

    /* GB5: ÷ (Control | CR | LF) */
    if (c == LAPLACE_GB_CONTROL || c == LAPLACE_GB_CR || c == LAPLACE_GB_LF) return 0;

    /* GB6: L × (L | V | LV | LVT) */
    if (p == LAPLACE_GB_L
        && (c == LAPLACE_GB_L || c == LAPLACE_GB_V
            || c == LAPLACE_GB_LV || c == LAPLACE_GB_LVT)) return 1;

    /* GB7: (LV | V) × (V | T) */
    if ((p == LAPLACE_GB_LV || p == LAPLACE_GB_V)
        && (c == LAPLACE_GB_V || c == LAPLACE_GB_T)) return 1;

    /* GB8: (LVT | T) × T */
    if ((p == LAPLACE_GB_LVT || p == LAPLACE_GB_T)
        && c == LAPLACE_GB_T) return 1;

    /* GB9: × (Extend | ZWJ) */
    if (c == LAPLACE_GB_EXTEND || c == LAPLACE_GB_ZWJ) return 1;

    /* GB9a: × SpacingMark */
    if (c == LAPLACE_GB_SPACINGMARK) return 1;

    /* GB9b: Prepend × */
    if (p == LAPLACE_GB_PREPEND) return 1;

    /* GB11: \p{Extended_Pictographic} Extend* ZWJ × \p{Extended_Pictographic}
     * State: in_emoji_zwj_seq is true when prev == ZWJ AND we're inside
     * the Extended_Pictographic-Extend*-ZWJ run. */
    if (st->in_emoji_zwj_seq
        && p == LAPLACE_GB_ZWJ
        && c == LAPLACE_GB_EXTENDED_PICTOGRAPHIC) {
        return 1;
    }

    /* GB12 + GB13: RI × RI iff the count of contiguous RIs ending at
     * `prev` is ODD (i.e. `prev` starts a new pair). If it's EVEN,
     * `prev` closes a pair and we break before `curr`. */
    if (p == LAPLACE_GB_REGIONAL_INDICATOR
        && c == LAPLACE_GB_REGIONAL_INDICATOR
        && (st->ri_run_len % 2) == 1) {
        return 1;
    }

    /* GB999: any ÷ any (default) */
    return 0;
}

/* Update state AFTER deciding the boundary between `prev` and `curr`,
 * reflecting curr becoming the new prev for the next iteration. */
static void update_state(uint32_t prev, uint32_t curr, gb_state_t* st) {
    const uint8_t p = gb(prev);
    const uint8_t c = gb(curr);

    /* Regional-indicator run length: increments when curr is RI AND
     * (prev is also RI AND continuing); resets to 1 when curr is RI but
     * prev was not (start of new run); resets to 0 otherwise. */
    if (c == LAPLACE_GB_REGIONAL_INDICATOR) {
        if (p == LAPLACE_GB_REGIONAL_INDICATOR) st->ri_run_len += 1;
        else st->ri_run_len = 1;
    } else {
        st->ri_run_len = 0;
    }

    /* Emoji ZWJ sequence: enters the state when we see
     *   \p{Extended_Pictographic} -> any Extend* -> ZWJ
     * Stays in the state while consuming the trailing ZWJ. Falls out as
     * soon as something other than (Extend | ZWJ) follows the
     * Extended_Pictographic chain. */
    if (c == LAPLACE_GB_EXTENDED_PICTOGRAPHIC) {
        /* New emoji starts; tracking pending sequence starts at the next ZWJ */
        st->in_emoji_zwj_seq = 0;  /* will set true if Extend*-ZWJ chain develops */
        /* Special: if prev WAS in_emoji_zwj_seq and we joined via GB11,
         * we're still in an emoji chain — set true so a further ZWJ
         * continues the join. */
    }
    if (c == LAPLACE_GB_EXTEND) {
        /* Extends preserve a pending emoji-zwj state if we were tracking
         * one (an Extended_Pictographic earlier, with no break between).
         * If prev was Extended_Pictographic or already in the seq, stay in. */
        if (p == LAPLACE_GB_EXTENDED_PICTOGRAPHIC || st->in_emoji_zwj_seq) {
            /* still inside the pictograph cluster; pending ZWJ check */
        }
    }
    if (c == LAPLACE_GB_ZWJ) {
        /* ZWJ following a pictograph (possibly via Extends) enters the
         * GB11 join state. */
        if (p == LAPLACE_GB_EXTENDED_PICTOGRAPHIC
            || (p == LAPLACE_GB_EXTEND && st->in_emoji_zwj_seq)
            || (p == LAPLACE_GB_EXTEND && /* extend after pictograph anywhere in cluster */ 1)) {
            /* The cluster has been holding a pictograph; promote */
        }
        /* Simpler + correct: we are in_emoji_zwj_seq iff the most recent
         * non-(Extend|ZWJ) codepoint in the cluster was an
         * Extended_Pictographic. Track that explicitly. */
    }
}

/* The simpler invariant for in_emoji_zwj_seq, recomputed inline:
 *
 *   in_emoji_zwj_seq is TRUE at the moment of deciding the boundary
 *   between `prev` and `curr` iff there exists an index j ≤ prev's
 *   index such that:
 *     - codepoints[j] is Extended_Pictographic
 *     - every codepoint strictly between j and prev is Extend
 *     - codepoints[prev's index] is ZWJ
 *
 * We track this via a single boolean "saw_pictograph_in_cluster" that
 * becomes true when we cross an Extended_Pictographic, stays true while
 * we cross only Extend|ZWJ codepoints, and clears on any other
 * codepoint. Then in_emoji_zwj_seq is "saw_pictograph_in_cluster AND
 * gb(prev) == ZWJ". This is what the iterator below uses (we ignore
 * update_state's emoji-zwj logic; left as a comment for clarity). */

/* Helper: read InCB property */
static uint8_t incb(uint32_t cp) { return codepoint_table_incb(cp); }

size_t laplace_grapheme_break_next(const uint32_t* codepoints, size_t n, size_t from) {
    if (from >= n) return n;
    if (from + 1 >= n) return n;  /* GB2 — sole boundary is at n */

    size_t i = from + 1;
    gb_state_t st = { .ri_run_len = 0, .in_emoji_zwj_seq = 0 };

    /* GB9c state: track whether we've crossed an InCB=Consonant in this
     * cluster AND whether at least one InCB=Linker appeared after it.
     * Both required for the "× InCB=Consonant" continuation. */
    int saw_indic_consonant = 0;
    int saw_indic_linker_after_consonant = 0;

    /* Seed the state from history relative to `from`. The boundary we're
     * deciding is between codepoints[i-1] and codepoints[i].
     *
     * RI-run-length: count contiguous RI codepoints ending at i-1.
     * Pictograph-in-cluster: scan back from i-1 through Extend|ZWJ until
     * the start of the current cluster (or an Extended_Pictographic).
     *
     * "Start of current cluster" is tricky to define without re-running
     * the segmenter — but for the iterator's use, the caller invokes us
     * starting from a known boundary, so cluster history doesn't span
     * across `from`. We initialize state assuming `from` IS at a
     * boundary (the entry contract), then walk forward updating. */

    /* Walk RI history backward from from-1, stopping at the first
     * non-RI or at `from` itself. */
    if (from > 0) {
        size_t k = from;
        while (k > 0 && gb(codepoints[k - 1]) == LAPLACE_GB_REGIONAL_INDICATOR) {
            st.ri_run_len += 1;
            k -= 1;
        }
    }

    /* Pictograph-in-cluster history: walk back from from-1 through
     * Extend|ZWJ. If we reach an Extended_Pictographic, set true. */
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

    /* GB9c history: walk back from from-1 through InCB=Extend|Linker
     * codepoints. If we reach an InCB=Consonant with at least one
     * Linker on the way, set both flags. */
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

    /* Seed state from codepoints[from] itself — it's the start of the
     * cluster and its own properties set initial state for the very
     * next boundary decision. */
    {
        uint8_t cp0_gb = gb(codepoints[from]);
        uint8_t cp0_incb = incb(codepoints[from]);
        if (cp0_gb == LAPLACE_GB_EXTENDED_PICTOGRAPHIC) saw_pictograph_in_cluster = 1;
        if (cp0_gb == LAPLACE_GB_REGIONAL_INDICATOR) {
            /* Backward walk counted RIs strictly BEFORE from; add this one. */
            st.ri_run_len += 1;
            if (from == 0) st.ri_run_len = 1;  /* exact at sot */
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

        /* Compose the boolean GB11 state from the running flag. */
        st.in_emoji_zwj_seq = (saw_pictograph_in_cluster && p == LAPLACE_GB_ZWJ) ? 1 : 0;

        /* GB9c: × InCB=Consonant if we have a preceding
         * Consonant-(Extend|Linker)*-Linker-(Extend|Linker)* chain. */
        int gb9c_fires = (saw_indic_consonant
                          && saw_indic_linker_after_consonant
                          && incb(curr) == LAPLACE_INCB_CONSONANT);

        if (!gb9c_fires && !no_break(prev, curr, &st)) {
            return i;
        }

        /* Update RI run for the next iteration. */
        if (c == LAPLACE_GB_REGIONAL_INDICATOR) {
            if (p == LAPLACE_GB_REGIONAL_INDICATOR) st.ri_run_len += 1;
            else st.ri_run_len = 1;
        } else {
            st.ri_run_len = 0;
        }

        /* Update pictograph-in-cluster for the next iteration:
         *  - if curr is Extended_Pictographic, set true
         *  - if curr is Extend|ZWJ, preserve the flag
         *  - otherwise clear */
        if (c == LAPLACE_GB_EXTENDED_PICTOGRAPHIC) {
            saw_pictograph_in_cluster = 1;
        } else if (c == LAPLACE_GB_EXTEND || c == LAPLACE_GB_ZWJ) {
            /* preserve */
        } else {
            saw_pictograph_in_cluster = 0;
        }

        /* Update InCB history for the next iteration. */
        uint8_t curr_incb = incb(curr);
        if (curr_incb == LAPLACE_INCB_CONSONANT) {
            saw_indic_consonant = 1;
            saw_indic_linker_after_consonant = 0;
        } else if (curr_incb == LAPLACE_INCB_LINKER) {
            if (saw_indic_consonant) saw_indic_linker_after_consonant = 1;
        } else if (curr_incb == LAPLACE_INCB_EXTEND) {
            /* preserve */
        } else {
            saw_indic_consonant = 0;
            saw_indic_linker_after_consonant = 0;
        }

        i += 1;
    }
    return n;  /* GB2 */
}

/* update_state is unused (state-update logic is inlined in
 * laplace_grapheme_break_next); kept for readability of the rules. */
__attribute__((unused))
static void __keep_update_state_alive(void) {
    gb_state_t s = {0};
    update_state(0, 0, &s);
}
