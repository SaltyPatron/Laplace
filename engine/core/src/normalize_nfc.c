#include "laplace/core/normalize_nfc.h"

#include <stdlib.h>
#include <string.h>

#include "laplace/core/codepoint_table.h"

static int utf8_decode(const uint8_t* p, size_t remaining,
                       uint32_t* out_cp, size_t* out_consumed) {
    if (remaining == 0) return -1;
    uint8_t b0 = p[0];
    if (b0 < 0x80) { *out_cp = b0; *out_consumed = 1; return 0; }
    if ((b0 & 0xE0) == 0xC0) {
        if (remaining < 2) return -1;
        uint8_t b1 = p[1];
        if ((b1 & 0xC0) != 0x80) return -1;
        uint32_t cp = ((uint32_t)(b0 & 0x1F) << 6) | (b1 & 0x3F);
        if (cp < 0x80) return -1;
        *out_cp = cp; *out_consumed = 2; return 0;
    }
    if ((b0 & 0xF0) == 0xE0) {
        if (remaining < 3) return -1;
        uint8_t b1 = p[1], b2 = p[2];
        if ((b1 & 0xC0) != 0x80 || (b2 & 0xC0) != 0x80) return -1;
        uint32_t cp = ((uint32_t)(b0 & 0x0F) << 12)
                    | ((uint32_t)(b1 & 0x3F) << 6)
                    | (b2 & 0x3F);
        if (cp < 0x800) return -1;
        if (cp >= 0xD800 && cp <= 0xDFFF) return -1;
        *out_cp = cp; *out_consumed = 3; return 0;
    }
    if ((b0 & 0xF8) == 0xF0) {
        if (remaining < 4) return -1;
        uint8_t b1 = p[1], b2 = p[2], b3 = p[3];
        if ((b1 & 0xC0) != 0x80 || (b2 & 0xC0) != 0x80 || (b3 & 0xC0) != 0x80) return -1;
        uint32_t cp = ((uint32_t)(b0 & 0x07) << 18)
                    | ((uint32_t)(b1 & 0x3F) << 12)
                    | ((uint32_t)(b2 & 0x3F) << 6)
                    | (b3 & 0x3F);
        if (cp < 0x10000 || cp > 0x10FFFF) return -1;
        *out_cp = cp; *out_consumed = 4; return 0;
    }
    return -1;
}

static size_t utf8_encode(uint32_t cp, uint8_t out[4]) {
    if (cp < 0x80) { out[0] = (uint8_t)cp; return 1; }
    if (cp < 0x800) {
        out[0] = 0xC0 | (uint8_t)(cp >> 6);
        out[1] = 0x80 | (uint8_t)(cp & 0x3F);
        return 2;
    }
    if (cp < 0x10000) {
        out[0] = 0xE0 | (uint8_t)(cp >> 12);
        out[1] = 0x80 | (uint8_t)((cp >> 6) & 0x3F);
        out[2] = 0x80 | (uint8_t)(cp & 0x3F);
        return 3;
    }
    out[0] = 0xF0 | (uint8_t)(cp >> 18);
    out[1] = 0x80 | (uint8_t)((cp >> 12) & 0x3F);
    out[2] = 0x80 | (uint8_t)((cp >> 6) & 0x3F);
    out[3] = 0x80 | (uint8_t)(cp & 0x3F);
    return 4;
}

#define HANGUL_S_BASE  0xAC00u
#define HANGUL_L_BASE  0x1100u
#define HANGUL_V_BASE  0x1161u
#define HANGUL_T_BASE  0x11A7u
#define HANGUL_L_COUNT 19u
#define HANGUL_V_COUNT 21u
#define HANGUL_T_COUNT 28u
#define HANGUL_N_COUNT (HANGUL_V_COUNT * HANGUL_T_COUNT)
#define HANGUL_S_COUNT (HANGUL_L_COUNT * HANGUL_N_COUNT)

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

static int hangul_compose(uint32_t a, uint32_t b, uint32_t* out) {
    if (a >= HANGUL_L_BASE && a < HANGUL_L_BASE + HANGUL_L_COUNT
        && b >= HANGUL_V_BASE && b < HANGUL_V_BASE + HANGUL_V_COUNT) {
        uint32_t l_index = a - HANGUL_L_BASE;
        uint32_t v_index = b - HANGUL_V_BASE;
        *out = HANGUL_S_BASE + (l_index * HANGUL_V_COUNT + v_index) * HANGUL_T_COUNT;
        return 1;
    }
    if (a >= HANGUL_S_BASE && a < HANGUL_S_BASE + HANGUL_S_COUNT
        && ((a - HANGUL_S_BASE) % HANGUL_T_COUNT == 0)
        && b > HANGUL_T_BASE && b < HANGUL_T_BASE + HANGUL_T_COUNT) {
        *out = a + (b - HANGUL_T_BASE);
        return 1;
    }
    return 0;
}

static void decompose_into(uint32_t cp, uint32_t** buf, size_t* len, size_t* cap) {
    uint32_t hg[3];
    size_t hg_len;
    if (hangul_decompose(cp, hg, &hg_len)) {
        for (size_t i = 0; i < hg_len; ++i) decompose_into(hg[i], buf, len, cap);
        return;
    }
    const uint32_t* seq = NULL;
    uint32_t length = 0;
    if (codepoint_table_decompose(cp, &seq, &length) && length > 0) {
        for (uint32_t i = 0; i < length; ++i) {
            decompose_into(seq[i], buf, len, cap);
        }
        return;
    }
    if (*len >= *cap) {
        size_t new_cap = (*cap) * 2;
        if (new_cap == 0) new_cap = 16;
        uint32_t* nb = (uint32_t*)realloc(*buf, new_cap * sizeof(uint32_t));
        if (!nb) return;
        *buf = nb;
        *cap = new_cap;
    }
    (*buf)[(*len)++] = cp;
}

static void canonical_reorder(uint32_t* buf, size_t len) {
    if (len < 2) return;
    size_t i = 0;
    while (i < len) {
        if (codepoint_table_ccc(buf[i]) == 0) { i += 1; continue; }
        size_t j = i;
        while (j < len && codepoint_table_ccc(buf[j]) != 0) j += 1;
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

static size_t canonical_compose(uint32_t* buf, size_t len) {
    if (len == 0) return 0;
    size_t   out_pos = 0;
    uint32_t starter = 0;
    uint8_t  last_ccc = 0;
    size_t   starter_pos = 0;
    int      has_starter = 0;

    size_t read = 0;
    while (read < len) {
        uint32_t cp = buf[read];
        uint8_t  cc = codepoint_table_ccc(cp);

        if (!has_starter) {
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

        int blocked = (last_ccc > 0 && last_ccc >= cc);
        uint32_t composed;
        int composed_ok = 0;
        if (!blocked) {
            if (hangul_compose(starter, cp, &composed)) composed_ok = 1;
            else if (codepoint_table_compose(starter, cp, &composed)) composed_ok = 1;
        }

        if (composed_ok) {
            starter = composed;
            buf[starter_pos] = composed;
            read += 1;
            continue;
        }

        buf[out_pos] = cp;
        if (cc == 0) {
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

    size_t cap = in_len * 2 + 16;
    size_t len = 0;
    uint32_t* buf = (uint32_t*)malloc(cap * sizeof(uint32_t));
    if (!buf) return 0;
    for (size_t i = 0; i < in_len; ++i) decompose_into(in[i], &buf, &len, &cap);

    canonical_reorder(buf, len);

    size_t composed_len = canonical_compose(buf, len);

    if (out == NULL || out_cap < composed_len) {
        size_t need = composed_len;
        free(buf);
        return need;
    }
    memcpy(out, buf, composed_len * sizeof(uint32_t));
    free(buf);
    return composed_len;
}

int laplace_normalize_nfc_utf8(
    const uint8_t* utf8, size_t len, uint8_t** out_utf8, size_t* out_len) {
    if (!out_utf8 || !out_len) return -1;
    *out_utf8 = NULL;
    *out_len  = 0;
    if (!utf8 && len > 0) return -1;
    if (len == 0) return 0;

    size_t cap = len + 1;
    uint32_t* raw = (uint32_t*)malloc(cap * sizeof(uint32_t));
    if (!raw) return -3;
    size_t raw_n = 0;
    size_t off = 0;
    while (off < len) {
        if (raw_n >= cap) {
            cap *= 2;
            uint32_t* n = (uint32_t*)realloc(raw, cap * sizeof(uint32_t));
            if (!n) { free(raw); return -3; }
            raw = n;
        }
        uint32_t cp;
        size_t consumed;
        if (utf8_decode(utf8 + off, len - off, &cp, &consumed) != 0) {
            free(raw);
            return -2;
        }
        raw[raw_n++] = cp;
        off += consumed;
    }

    size_t need = laplace_normalize_nfc(raw, raw_n, NULL, 0);
    if (need == 0) { free(raw); return -3; }
    uint32_t* nfc = (uint32_t*)malloc(need * sizeof(uint32_t));
    if (!nfc) { free(raw); return -3; }
    size_t nfc_n = laplace_normalize_nfc(raw, raw_n, nfc, need);
    free(raw);
    if (nfc_n == 0) { free(nfc); return -3; }

    size_t out_cap = nfc_n * 4;
    uint8_t* buf = (uint8_t*)malloc(out_cap);
    if (!buf) { free(nfc); return -3; }
    size_t out_pos = 0;
    uint8_t enc[4];
    for (size_t i = 0; i < nfc_n; ++i) {
        size_t n = utf8_encode(nfc[i], enc);
        if (out_pos + n > out_cap) {
            out_cap = (out_pos + n) * 2;
            uint8_t* grown = (uint8_t*)realloc(buf, out_cap);
            if (!grown) { free(buf); free(nfc); return -3; }
            buf = grown;
        }
        memcpy(buf + out_pos, enc, n);
        out_pos += n;
    }
    free(nfc);
    *out_utf8 = buf;
    *out_len  = out_pos;
    return 0;
}
