#include "laplace/core/normalize_nfc.h"

#include <stdlib.h>
#include <string.h>

#include "laplace/core/codepoint_table.h"

/* Hangul syllable constants per Unicode standard §3.12 + UAX #15. */
#define HANGUL_S_BASE  0xAC00u
#define HANGUL_L_BASE  0x1100u
#define HANGUL_V_BASE  0x1161u
#define HANGUL_T_BASE  0x11A7u
#define HANGUL_L_COUNT 19u
#define HANGUL_V_COUNT 21u
#define HANGUL_T_COUNT 28u
#define HANGUL_N_COUNT (HANGUL_V_COUNT * HANGUL_T_COUNT)   /* 588 */
#define HANGUL_S_COUNT (HANGUL_L_COUNT * HANGUL_N_COUNT)   /* 11172 */

/* Returns 1 + decomposes the Hangul syllable into 2 or 3 jamo codepoints
 * via the arithmetic decomposition (no table). Returns 0 if cp is not
 * a precomposed syllable. */
static int hangul_decompose(uint32_t cp, uint32_t out[3], size_t* out_len) {
    if (cp < HANGUL_S_BASE || cp >= HANGUL_S_BASE + HANGUL_S_COUNT) return 0;
    uint32_t s_index = cp - HANGUL_S_BASE;
    out[0] = HANGUL_L_BASE + s_index / HANGUL_N_COUNT;
    out[1] = HANGUL_V_BASE + (s_index % HANGUL_N_COUNT) / HANGUL_T_COUNT;
    uint32_t t_index = s_index % HANGUL_T_COUNT;
    if (t_index != 0) {
        out[2] = HANGUL_T_BASE + t_index;
        *out_len = 3;
    } else {
        *out_len = 2;
    }
    return 1;
}

/* Try Hangul composition: L+V → LV, LV+T → LVT. Returns 1 + writes
 * composed codepoint to *out if composed, 0 otherwise. */
static int hangul_compose(uint32_t a, uint32_t b, uint32_t* out) {
    /* L + V → LV */
    if (a >= HANGUL_L_BASE && a < HANGUL_L_BASE + HANGUL_L_COUNT
        && b >= HANGUL_V_BASE && b < HANGUL_V_BASE + HANGUL_V_COUNT) {
        uint32_t l_index = a - HANGUL_L_BASE;
        uint32_t v_index = b - HANGUL_V_BASE;
        *out = HANGUL_S_BASE + (l_index * HANGUL_V_COUNT + v_index) * HANGUL_T_COUNT;
        return 1;
    }
    /* LV + T → LVT */
    if (a >= HANGUL_S_BASE && a < HANGUL_S_BASE + HANGUL_S_COUNT
        && ((a - HANGUL_S_BASE) % HANGUL_T_COUNT == 0)
        && b > HANGUL_T_BASE && b < HANGUL_T_BASE + HANGUL_T_COUNT) {
        *out = a + (b - HANGUL_T_BASE);
        return 1;
    }
    return 0;
}

/* Recursively decompose cp into the output array, growing as needed
 * via a caller-managed buffer pointer. Returns the number of codepoints
 * appended. */
static void decompose_into(uint32_t cp, uint32_t** buf, size_t* len, size_t* cap) {
    /* Hangul fast path */
    uint32_t hg[3];
    size_t hg_len;
    if (hangul_decompose(cp, hg, &hg_len)) {
        for (size_t i = 0; i < hg_len; ++i) decompose_into(hg[i], buf, len, cap);
        return;
    }
    /* Perf-cache canonical decomposition. The blob stores the FULL
     * recursive decomposition already; recursing here on terminal
     * codepoints simply appends them (each has no further mapping). */
    const uint32_t* seq = NULL;
    uint32_t length = 0;
    if (codepoint_table_decompose(cp, &seq, &length) && length > 0) {
        for (uint32_t i = 0; i < length; ++i) {
            decompose_into(seq[i], buf, len, cap);
        }
        return;
    }
    /* No decomposition: append cp */
    if (*len >= *cap) {
        size_t new_cap = (*cap) * 2;
        if (new_cap == 0) new_cap = 16;
        uint32_t* nb = (uint32_t*)realloc(*buf, new_cap * sizeof(uint32_t));
        if (!nb) return;  /* caller will detect via shorter len */
        *buf = nb;
        *cap = new_cap;
    }
    (*buf)[(*len)++] = cp;
}

/* Canonical reorder: insertion-sort within each non-starter run, sorting
 * by CCC ascending (stable for equal CCCs). */
static void canonical_reorder(uint32_t* buf, size_t len) {
    if (len < 2) return;
    /* Walk and find runs where all CCCs > 0; sort each run. */
    size_t i = 0;
    while (i < len) {
        if (codepoint_table_ccc(buf[i]) == 0) { i += 1; continue; }
        size_t j = i;
        while (j < len && codepoint_table_ccc(buf[j]) != 0) j += 1;
        /* sort buf[i..j) by CCC */
        for (size_t k = i + 1; k < j; ++k) {
            uint32_t kv = buf[k];
            uint8_t  kc = codepoint_table_ccc(kv);
            size_t   m = k;
            while (m > i && codepoint_table_ccc(buf[m - 1]) > kc) {
                buf[m] = buf[m - 1];
                m -= 1;
            }
            buf[m] = kv;
        }
        i = j;
    }
}

/* Canonical compose: walk left to right. Maintain the most recent
 * "starter" (CCC == 0 codepoint, that's still a candidate to absorb
 * following non-starters). For each subsequent codepoint X:
 *   - If X is a non-starter, try compose(starter, X) — if it succeeds,
 *     replace starter with the composed value (do not advance write
 *     pointer), drop X. If it fails, write X as-is.
 *   - If X is a starter, try compose(starter, X) — if it succeeds,
 *     replace starter with composed value. If not, the OLD starter is
 *     "done", advance write pointer; X becomes the new starter.
 *
 * Blocking rule per UAX #15: a candidate (starter, X) is "blocked" if
 * there's any non-starter between them with CCC >= CCC(X). We track the
 * highest CCC seen since the last starter; if X's CCC <= that, skip the
 * compose attempt. */
static size_t canonical_compose(uint32_t* buf, size_t len) {
    if (len == 0) return 0;
    size_t   out_pos = 0;
    uint32_t starter = 0;
    uint8_t  last_ccc = 0;       /* highest CCC since starter (blocking rule) */
    size_t   starter_pos = 0;    /* index in buf[] where starter currently lives */
    int      has_starter = 0;

    size_t read = 0;
    while (read < len) {
        uint32_t cp = buf[read];
        uint8_t  cc = codepoint_table_ccc(cp);

        if (!has_starter) {
            /* No starter yet (we may be in leading non-starters). Output cp as-is. */
            buf[out_pos] = cp;
            if (cc == 0) {
                has_starter = 1;
                starter = cp;
                starter_pos = out_pos;
                last_ccc = 0;
            }
            out_pos += 1;
            read += 1;
            continue;
        }

        /* has_starter: try compose(starter, cp). The blocking rule:
         * Per UAX #15 PRI #29: X is blocked from S if any non-starter
         * intervened (last_ccc > 0) AND last_ccc >= cc. When cc == 0
         * (X is a starter), the comparison "last_ccc >= 0" is trivially
         * true, so any non-starter between S and X blocks composition. */
        int blocked = (last_ccc > 0 && last_ccc >= cc);
        uint32_t composed;
        int composed_ok = 0;
        if (!blocked) {
            if (hangul_compose(starter, cp, &composed)) composed_ok = 1;
            else if (codepoint_table_compose(starter, cp, &composed)) composed_ok = 1;
        }

        if (composed_ok) {
            /* Replace the starter in-place with the composed value. */
            starter = composed;
            buf[starter_pos] = composed;
            /* Do NOT advance out_pos; do NOT update last_ccc (compose only
             * combines starter + this codepoint, no other change). */
            read += 1;
            continue;
        }

        /* No compose. Write cp. If cp is a starter, it becomes the new
         * "active" starter. */
        buf[out_pos] = cp;
        if (cc == 0) {
            /* New starter takes over. */
            starter = cp;
            starter_pos = out_pos;
            last_ccc = 0;
        } else {
            if (cc > last_ccc) last_ccc = cc;
        }
        out_pos += 1;
        read += 1;
    }

    return out_pos;
}

size_t laplace_normalize_nfc(
    const uint32_t* in, size_t in_len, uint32_t* out, size_t out_cap) {
    if (in_len == 0) return 0;

    /* Stage 1: full decompose into a growing arena buffer. */
    size_t cap = in_len * 2 + 16;
    size_t len = 0;
    uint32_t* buf = (uint32_t*)malloc(cap * sizeof(uint32_t));
    if (!buf) return 0;
    for (size_t i = 0; i < in_len; ++i) decompose_into(in[i], &buf, &len, &cap);

    /* Stage 2: canonical reorder. */
    canonical_reorder(buf, len);

    /* Stage 3: canonical compose. */
    size_t composed_len = canonical_compose(buf, len);

    /* Write out (or report required size). */
    if (out == NULL || out_cap < composed_len) {
        size_t need = composed_len;
        free(buf);
        return need;
    }
    memcpy(out, buf, composed_len * sizeof(uint32_t));
    free(buf);
    return composed_len;
}
