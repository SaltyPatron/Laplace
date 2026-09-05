#include "laplace/core/identity_key.h"

#include <stdlib.h>
#include <string.h>
#include <limits.h>

#include <unicode/uchar.h>
#include <unicode/ucasemap.h>

#include "laplace/core/normalize_nfc.h"
#include "laplace/core/utf8.h"

int laplace_identity_key_normalize_utf8(
    const uint8_t* utf8,
    size_t len,
    uint8_t** out_utf8,
    size_t* out_len) {
    if (!out_utf8 || !out_len || (!utf8 && len > 0)) return -1;
    *out_utf8 = NULL;
    *out_len = 0;
    if (len == 0) return 0;

    if (len > INT32_MAX) return -3;
    UErrorCode status = U_ZERO_ERROR;
    UCaseMap* case_map = ucasemap_open("", U_FOLD_CASE_DEFAULT, &status);
    if (U_FAILURE(status) || !case_map) return -3;
    int32_t folded_len = ucasemap_utf8FoldCase(
        case_map, NULL, 0, (const char*)utf8, (int32_t)len, &status);
    if (status != U_BUFFER_OVERFLOW_ERROR || folded_len <= 0) {
        ucasemap_close(case_map);
        return -2;
    }
    status = U_ZERO_ERROR;
    uint8_t* folded = (uint8_t*)malloc((size_t)folded_len + 1u);
    if (!folded) {
        ucasemap_close(case_map);
        return -3;
    }
    int32_t written = ucasemap_utf8FoldCase(
        case_map, (char*)folded, folded_len + 1,
        (const char*)utf8, (int32_t)len, &status);
    ucasemap_close(case_map);
    if (U_FAILURE(status) || written != folded_len) {
        free(folded);
        return -2;
    }

    uint8_t* collapsed = (uint8_t*)malloc((size_t)folded_len + 1u);
    if (!collapsed) {
        free(folded);
        return -3;
    }

    size_t in_pos = 0;
    size_t out_pos = 0;
    int pending_space = 0;
    while (in_pos < (size_t)folded_len) {
        uint32_t cp;
        size_t consumed;
        if (laplace_utf8_decode(
                folded + in_pos, (size_t)folded_len - in_pos, &cp, &consumed) != 0) {
            free(folded);
            free(collapsed);
            return -2;
        }
        in_pos += consumed;
        if (u_isUWhiteSpace((UChar32)cp)) {
            pending_space = out_pos > 0;
            continue;
        }
        if (pending_space) collapsed[out_pos++] = (uint8_t)' ';
        pending_space = 0;
        uint8_t encoded[4];
        size_t encoded_len = laplace_utf8_encode(cp, encoded);
        memcpy(collapsed + out_pos, encoded, encoded_len);
        out_pos += encoded_len;
    }
    free(folded);

    if (out_pos == 0) {
        free(collapsed);
        return 0;
    }
    int rc = laplace_normalize_nfc_utf8(collapsed, out_pos, out_utf8, out_len);
    free(collapsed);
    return rc;
}
