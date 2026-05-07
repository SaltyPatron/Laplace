/*
 * safetensors_header.c — minimal hand-rolled parser for the .safetensors
 * header format. The header is restricted JSON: a single top-level object
 * mapping tensor names to objects with {dtype: string, shape: [int...],
 * data_offsets: [int, int]}, plus an optional "__metadata__" key with
 * arbitrary string-string pairs.
 *
 * The parser tokenizes the header byte-by-byte into a small set of token
 * types and walks the structure, populating the entry array directly. No
 * external JSON dependency; no heap-grown intermediate structures beyond
 * the entry array itself.
 */

#include "laplace_pg/safetensors.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <inttypes.h>

struct laplace_safetensors_handle {
    laplace_tensor_entry_t *entries;
    size_t                  entry_count;
    size_t                  entry_capacity;
    uint64_t                data_section_offset;
    char                   *header_buffer;     /* owns the raw header bytes */
    uint64_t                header_byte_length;
};

/* ---------- dtype mapping ---------- */

static laplace_dtype_t parse_dtype(const char *s, size_t len)
{
    #define MATCH(x) (len == sizeof(x) - 1 && memcmp(s, (x), sizeof(x) - 1) == 0)
    if (MATCH("F64"))     { return LAPLACE_DTYPE_F64; }
    if (MATCH("F32"))     { return LAPLACE_DTYPE_F32; }
    if (MATCH("F16"))     { return LAPLACE_DTYPE_F16; }
    if (MATCH("BF16"))    { return LAPLACE_DTYPE_BF16; }
    if (MATCH("F8_E4M3")) { return LAPLACE_DTYPE_F8_E4M3; }
    if (MATCH("F8_E5M2")) { return LAPLACE_DTYPE_F8_E5M2; }
    if (MATCH("I64"))     { return LAPLACE_DTYPE_I64; }
    if (MATCH("I32"))     { return LAPLACE_DTYPE_I32; }
    if (MATCH("I16"))     { return LAPLACE_DTYPE_I16; }
    if (MATCH("I8"))      { return LAPLACE_DTYPE_I8; }
    if (MATCH("U64"))     { return LAPLACE_DTYPE_U64; }
    if (MATCH("U32"))     { return LAPLACE_DTYPE_U32; }
    if (MATCH("U16"))     { return LAPLACE_DTYPE_U16; }
    if (MATCH("U8"))      { return LAPLACE_DTYPE_U8; }
    if (MATCH("BOOL"))    { return LAPLACE_DTYPE_BOOL; }
    #undef MATCH
    return LAPLACE_DTYPE_UNKNOWN;
}

size_t laplace_dtype_byte_width(laplace_dtype_t dtype)
{
    switch (dtype) {
        case LAPLACE_DTYPE_F64: case LAPLACE_DTYPE_I64: case LAPLACE_DTYPE_U64: return 8;
        case LAPLACE_DTYPE_F32: case LAPLACE_DTYPE_I32: case LAPLACE_DTYPE_U32: return 4;
        case LAPLACE_DTYPE_F16: case LAPLACE_DTYPE_BF16:
        case LAPLACE_DTYPE_I16: case LAPLACE_DTYPE_U16: return 2;
        case LAPLACE_DTYPE_F8_E4M3: case LAPLACE_DTYPE_F8_E5M2:
        case LAPLACE_DTYPE_I8: case LAPLACE_DTYPE_U8: case LAPLACE_DTYPE_BOOL: return 1;
        default: return 0;
    }
}

/* ---------- minimal scanner over the JSON header ---------- */

typedef struct {
    const char *p;
    const char *end;
    int         error;
} scanner_t;

static void skip_ws(scanner_t *sc)
{
    while (sc->p < sc->end && (*sc->p == ' ' || *sc->p == '\t' || *sc->p == '\n' || *sc->p == '\r')) {
        ++sc->p;
    }
}

static int peek(scanner_t *sc, char c)
{
    skip_ws(sc);
    return sc->p < sc->end && *sc->p == c;
}

static int consume(scanner_t *sc, char c)
{
    skip_ws(sc);
    if (sc->p < sc->end && *sc->p == c) { ++sc->p; return 1; }
    sc->error = 1;
    return 0;
}

/* Parse a quoted string into out_buf (NULL-terminated). Returns length,
 * 0 on error. Does NOT process escapes beyond \" — sufficient for tensor
 * names and dtype strings as written by the safetensors writer. */
static size_t parse_string(scanner_t *sc, char *out_buf, size_t out_capacity)
{
    if (!consume(sc, '"')) { return 0; }
    size_t len = 0;
    while (sc->p < sc->end && *sc->p != '"') {
        if (*sc->p == '\\' && sc->p + 1 < sc->end) {
            ++sc->p;
            char esc = *sc->p++;
            char c;
            switch (esc) {
                case '"':  c = '"';  break;
                case '\\': c = '\\'; break;
                case '/':  c = '/';  break;
                case 'b':  c = '\b'; break;
                case 'f':  c = '\f'; break;
                case 'n':  c = '\n'; break;
                case 'r':  c = '\r'; break;
                case 't':  c = '\t'; break;
                default:   c = esc;  break;
            }
            if (len + 1 < out_capacity) { out_buf[len++] = c; }
            continue;
        }
        if (len + 1 < out_capacity) { out_buf[len++] = *sc->p; }
        ++sc->p;
    }
    if (sc->p >= sc->end || *sc->p != '"') { sc->error = 1; return 0; }
    ++sc->p;
    out_buf[len < out_capacity ? len : out_capacity - 1] = '\0';
    return len;
}

static int64_t parse_int(scanner_t *sc)
{
    skip_ws(sc);
    int64_t sign = 1;
    if (sc->p < sc->end && (*sc->p == '-' || *sc->p == '+')) {
        if (*sc->p == '-') { sign = -1; }
        ++sc->p;
    }
    int64_t value = 0;
    int     any   = 0;
    while (sc->p < sc->end && *sc->p >= '0' && *sc->p <= '9') {
        value = value * 10 + (*sc->p - '0');
        ++sc->p;
        any = 1;
    }
    if (!any) { sc->error = 1; return 0; }
    return sign * value;
}

/* Skip a JSON value of any type (used for __metadata__ and unknown keys). */
static void skip_value(scanner_t *sc)
{
    skip_ws(sc);
    if (sc->p >= sc->end) { sc->error = 1; return; }
    char c = *sc->p;
    if (c == '"') {
        ++sc->p;
        while (sc->p < sc->end && *sc->p != '"') {
            if (*sc->p == '\\' && sc->p + 1 < sc->end) { sc->p += 2; }
            else { ++sc->p; }
        }
        if (sc->p < sc->end) { ++sc->p; }
        return;
    }
    if (c == '{' || c == '[') {
        char open = c;
        char close = (c == '{') ? '}' : ']';
        int  depth = 1;
        ++sc->p;
        while (sc->p < sc->end && depth > 0) {
            if (*sc->p == '"') {
                ++sc->p;
                while (sc->p < sc->end && *sc->p != '"') {
                    if (*sc->p == '\\' && sc->p + 1 < sc->end) { sc->p += 2; }
                    else { ++sc->p; }
                }
                if (sc->p < sc->end) { ++sc->p; }
            } else if (*sc->p == open) { ++depth; ++sc->p; }
            else if (*sc->p == close) { --depth; ++sc->p; }
            else { ++sc->p; }
        }
        return;
    }
    while (sc->p < sc->end && *sc->p != ',' && *sc->p != '}' && *sc->p != ']') { ++sc->p; }
}

/* ---------- entry array growth ---------- */

static int ensure_capacity(struct laplace_safetensors_handle *h)
{
    if (h->entry_count < h->entry_capacity) { return 1; }
    size_t new_cap = h->entry_capacity == 0 ? 64 : h->entry_capacity * 2;
    laplace_tensor_entry_t *grown =
        (laplace_tensor_entry_t *) realloc(h->entries, new_cap * sizeof(laplace_tensor_entry_t));
    if (grown == NULL) { return 0; }
    h->entries        = grown;
    h->entry_capacity = new_cap;
    return 1;
}

/* ---------- per-tensor object parser ---------- */

static int parse_tensor_object(scanner_t *sc, laplace_tensor_entry_t *entry)
{
    if (!consume(sc, '{')) { return 0; }
    while (!sc->error) {
        skip_ws(sc);
        if (peek(sc, '}')) { ++sc->p; return 1; }
        char  key[64] = {0};
        size_t klen = parse_string(sc, key, sizeof key);
        if (klen == 0) { return 0; }
        if (!consume(sc, ':')) { return 0; }

        if (strcmp(key, "dtype") == 0) {
            char dt[16] = {0};
            size_t dlen = parse_string(sc, dt, sizeof dt);
            entry->dtype = (dlen > 0) ? parse_dtype(dt, dlen) : LAPLACE_DTYPE_UNKNOWN;
        }
        else if (strcmp(key, "shape") == 0) {
            if (!consume(sc, '[')) { return 0; }
            entry->rank = 0;
            int first = 1;
            while (!sc->error) {
                skip_ws(sc);
                if (peek(sc, ']')) { ++sc->p; break; }
                if (!first) {
                    if (!consume(sc, ',')) { return 0; }
                }
                first = 0;
                if (entry->rank >= LAPLACE_SAFETENSORS_MAX_RANK) {
                    skip_value(sc);
                    continue;
                }
                entry->shape[entry->rank++] = parse_int(sc);
            }
        }
        else if (strcmp(key, "data_offsets") == 0) {
            if (!consume(sc, '[')) { return 0; }
            int64_t a = parse_int(sc);
            if (!consume(sc, ',')) { return 0; }
            int64_t b = parse_int(sc);
            if (!consume(sc, ']')) { return 0; }
            entry->data_offset      = (uint64_t) a;
            entry->data_byte_length = (uint64_t)(b - a);
        }
        else {
            skip_value(sc);
        }

        skip_ws(sc);
        if (peek(sc, ',')) { ++sc->p; continue; }
        if (peek(sc, '}')) { ++sc->p; return 1; }
        sc->error = 1;
        return 0;
    }
    return 0;
}

/* ---------- top-level header parser ---------- */

static int parse_header(struct laplace_safetensors_handle *h)
{
    scanner_t sc = { h->header_buffer, h->header_buffer + h->header_byte_length, 0 };

    if (!consume(&sc, '{')) { return 0; }

    while (!sc.error) {
        skip_ws(&sc);
        if (peek(&sc, '}')) { ++sc.p; return 1; }

        char  name[LAPLACE_SAFETENSORS_MAX_NAME] = {0};
        size_t nlen = parse_string(&sc, name, sizeof name);
        if (nlen == 0) { return 0; }
        if (!consume(&sc, ':')) { return 0; }

        if (strcmp(name, "__metadata__") == 0) {
            skip_value(&sc);
        } else {
            if (!ensure_capacity(h)) { return 0; }
            laplace_tensor_entry_t *entry = &h->entries[h->entry_count];
            memset(entry, 0, sizeof *entry);
            strncpy(entry->name, name, sizeof entry->name - 1);
            if (!parse_tensor_object(&sc, entry)) { return 0; }
            ++h->entry_count;
        }

        skip_ws(&sc);
        if (peek(&sc, ',')) { ++sc.p; continue; }
        if (peek(&sc, '}')) { ++sc.p; return 1; }
        return 0;
    }
    return !sc.error;
}

/* ---------- public API ---------- */

laplace_safetensors_handle_t *laplace_safetensors_open(const char *path)
{
    FILE *f = fopen(path, "rb");
    if (f == NULL) { return NULL; }

    uint64_t header_len = 0;
    if (fread(&header_len, 1, sizeof header_len, f) != sizeof header_len) {
        fclose(f);
        return NULL;
    }

    struct laplace_safetensors_handle *h =
        (struct laplace_safetensors_handle *) calloc(1, sizeof *h);
    if (h == NULL) { fclose(f); return NULL; }

    h->header_byte_length   = header_len;
    h->data_section_offset  = 8 + header_len;
    h->header_buffer        = (char *) malloc((size_t) header_len + 1);
    if (h->header_buffer == NULL) { free(h); fclose(f); return NULL; }
    if (fread(h->header_buffer, 1, (size_t) header_len, f) != (size_t) header_len) {
        free(h->header_buffer); free(h); fclose(f); return NULL;
    }
    h->header_buffer[header_len] = '\0';
    fclose(f);

    if (!parse_header(h)) {
        laplace_safetensors_close(h);
        return NULL;
    }
    return h;
}

void laplace_safetensors_close(laplace_safetensors_handle_t *h)
{
    if (h == NULL) { return; }
    free(h->entries);
    free(h->header_buffer);
    free(h);
}

size_t laplace_safetensors_entry_count(const laplace_safetensors_handle_t *h)
{
    return h == NULL ? 0 : h->entry_count;
}

const laplace_tensor_entry_t *laplace_safetensors_entry(
    const laplace_safetensors_handle_t *h, size_t index)
{
    if (h == NULL || index >= h->entry_count) { return NULL; }
    return &h->entries[index];
}

const laplace_tensor_entry_t *laplace_safetensors_find(
    const laplace_safetensors_handle_t *h, const char *name)
{
    if (h == NULL || name == NULL) { return NULL; }
    for (size_t i = 0; i < h->entry_count; ++i) {
        if (strcmp(h->entries[i].name, name) == 0) { return &h->entries[i]; }
    }
    return NULL;
}

uint64_t laplace_safetensors_data_section_offset(const laplace_safetensors_handle_t *h)
{
    return h == NULL ? 0 : h->data_section_offset;
}
