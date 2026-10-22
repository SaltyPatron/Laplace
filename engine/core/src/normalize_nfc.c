/* Explicit NFC uses ICU's Unicode data; ingestion itself preserves source bytes. */
#include "laplace/core/normalize_nfc.h"
#include <limits.h>
#include <stdlib.h>
#include <unicode/unorm2.h>
#include <unicode/ustring.h>

static UChar *normalized(const UChar *input, int32_t length, int32_t *written) {
    UErrorCode status = U_ZERO_ERROR;
    const UNormalizer2 *nfc = unorm2_getNFCInstance(&status);
    if (U_FAILURE(status)) return NULL;
    int32_t need = unorm2_normalize(nfc, input, length, NULL, 0, &status);
    if ((U_FAILURE(status) && status != U_BUFFER_OVERFLOW_ERROR) || need < 0 || need == INT32_MAX) return NULL;
    UChar *output = malloc(((size_t)need + 1) * sizeof *output);
    if (!output) return NULL;
    status = U_ZERO_ERROR;
    *written = unorm2_normalize(nfc, input, length, output, need + 1, &status);
    if (U_FAILURE(status)) { free(output); return NULL; }
    return output;
}

size_t laplace_normalize_nfc(const uint32_t *input, size_t length, uint32_t *output, size_t capacity) {
    if (!length) return 0;
    if (!input || length > (INT32_MAX - 1) / 2) return 0;
    int32_t room = (int32_t)length * 2 + 1, count = 0;
    UChar *utf16 = malloc((size_t)room * sizeof *utf16);
    if (!utf16) return 0;
    UErrorCode status = U_ZERO_ERROR;
    u_strFromUTF32(utf16, room, &count, (const UChar32 *)input, (int32_t)length, &status);
    if (U_FAILURE(status)) { free(utf16); return 0; }
    int32_t normalized_count = 0;
    UChar *nfc = normalized(utf16, count, &normalized_count); free(utf16);
    if (!nfc) return 0;
    status = U_ZERO_ERROR;
    int32_t need = 0;
    u_strToUTF32(NULL, 0, &need, nfc, normalized_count, &status);
    if (U_FAILURE(status) && status != U_BUFFER_OVERFLOW_ERROR) { free(nfc); return 0; }
    if (output && capacity >= (size_t)need) {
        status = U_ZERO_ERROR;
        u_strToUTF32((UChar32 *)output, (int32_t)(capacity > INT32_MAX ? INT32_MAX : capacity),
                    &need, nfc, normalized_count, &status);
        if (U_FAILURE(status)) need = 0;
    }
    free(nfc); return (size_t)need;
}

int laplace_normalize_nfc_utf8(const uint8_t *input, size_t length, uint8_t **output, size_t *written) {
    if (!output || !written || (!input && length) || length >= INT32_MAX) return -1;
    *output = NULL; *written = 0;
    if (!length) return 0;
    UChar *utf16 = malloc((length + 1) * sizeof *utf16);
    if (!utf16) return -3;
    int32_t count = 0; UErrorCode status = U_ZERO_ERROR;
    u_strFromUTF8(utf16, (int32_t)length + 1, &count, (const char *)input, (int32_t)length, &status);
    if (U_FAILURE(status)) { free(utf16); return -2; }
    int32_t normalized_count = 0;
    UChar *nfc = normalized(utf16, count, &normalized_count); free(utf16);
    if (!nfc) return -3;
    status = U_ZERO_ERROR;
    int32_t need = 0;
    u_strToUTF8(NULL, 0, &need, nfc, normalized_count, &status);
    if ((U_FAILURE(status) && status != U_BUFFER_OVERFLOW_ERROR) || need < 0 || need == INT32_MAX) {
        free(nfc); return -3;
    }
    uint8_t *bytes = malloc((size_t)need + 1);
    if (!bytes) { free(nfc); return -3; }
    status = U_ZERO_ERROR;
    u_strToUTF8((char *)bytes, need + 1, &need, nfc, normalized_count, &status); free(nfc);
    if (U_FAILURE(status)) { free(bytes); return -3; }
    *output = bytes; *written = (size_t)need; return 0;
}
