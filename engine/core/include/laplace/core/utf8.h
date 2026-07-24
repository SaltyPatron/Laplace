#pragma once

#include <stddef.h>
#include <stdint.h>

/* The one UTF-8 codec for the core. Every tier-0 text path (NFC normalize,
 * grapheme floor, grammar compose, unicode seed, codepoint whitespace scan)
 * shares these two functions instead of copy-pasting the byte grammar --
 * identity-bearing decoding must have exactly one definition. Both are
 * static inline so there is no new translation unit and no linkage symbol;
 * usable from C and C++ alike. */

/* Decode one code point. Rejects overlong forms, UTF-16 surrogates
 * (U+D800..U+DFFF), and anything above U+10FFFF. Returns 0 on success (writing
 * *out_cp and *out_consumed) or -1 on any malformed / truncated input. */
static inline int laplace_utf8_decode(const uint8_t* p, size_t remaining,
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

/* Encode one code point into out[0..3]. Returns the byte count (1..4). The
 * caller is responsible for passing a valid scalar value. */
static inline size_t laplace_utf8_encode(uint32_t cp, uint8_t out[4]) {
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
