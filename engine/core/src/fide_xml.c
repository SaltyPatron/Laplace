#include "laplace/core/fide_xml.h"

#include <string.h>

static const uint8_t* find_bytes(const uint8_t* begin, const uint8_t* end,
                                 const char* needle, size_t needle_len) {
    if (!begin || !end || begin > end || needle_len == 0 || (size_t)(end - begin) < needle_len)
        return NULL;
    const uint8_t first = (uint8_t)needle[0];
    const uint8_t* p = begin;
    while (p + needle_len <= end) {
        p = (const uint8_t*)memchr(p, first, (size_t)(end - p) - needle_len + 1);
        if (!p) return NULL;
        if (memcmp(p, needle, needle_len) == 0) return p;
        ++p;
    }
    return NULL;
}

static int field_span(const uint8_t* base, const uint8_t* record_begin,
                      const uint8_t* record_end, const char* tag,
                      laplace_text_span_t* out) {
    char open[32];
    char close[36];
    const size_t tag_len = strlen(tag);
    if (tag_len + 2 >= sizeof(open) || tag_len + 3 >= sizeof(close)) return -1;
    open[0] = '<';
    memcpy(open + 1, tag, tag_len);
    open[tag_len + 1] = '\0';
    close[0] = '<'; close[1] = '/';
    memcpy(close + 2, tag, tag_len);
    close[tag_len + 2] = '>'; close[tag_len + 3] = '\0';

    const uint8_t* cursor = record_begin;
    const uint8_t* start = NULL;
    while ((start = find_bytes(cursor, record_end, open, tag_len + 1)) != NULL) {
        const uint8_t following = start[tag_len + 1];
        if (following == '>' || following == '/' || following == ' '
            || following == '\t' || following == '\r' || following == '\n')
            break;
        cursor = start + tag_len + 1;
    }
    if (!start) {
        out->offset = 0;
        out->length = 0;
        return 0;
    }

    const uint8_t* open_end = (const uint8_t*)memchr(
        start + tag_len + 1, '>', (size_t)(record_end - (start + tag_len + 1)));
    if (!open_end) return -3;
    const uint8_t* before = open_end;
    while (before > start && (before[-1] == ' ' || before[-1] == '\t'
                              || before[-1] == '\r' || before[-1] == '\n'))
        --before;
    if (before > start && before[-1] == '/') {
        out->offset = (uint32_t)(open_end + 1 - base);
        out->length = 0;
        return 0;
    }

    const uint8_t* value = open_end + 1;
    const uint8_t* value_end = find_bytes(value, record_end, close, tag_len + 3);
    if (!value_end) return -3;
    out->offset = (uint32_t)(value - base);
    out->length = (uint32_t)(value_end - value);
    return 0;
}

int laplace_fide_xml_project(const uint8_t* utf8, size_t len,
                             laplace_fide_player_projection_t* out,
                             size_t capacity, size_t* out_count) {
    static const char player_open[] = "<player>";
    static const char player_close[] = "</player>";
    if ((!utf8 && len != 0) || !out_count || (!out && capacity != 0)) return -1;
    if (len == 0) {
        *out_count = 0;
        return 0;
    }

    const uint8_t* const end = utf8 + len;
    const uint8_t* cursor = utf8;
    size_t count = 0;
    while (cursor < end) {
        const uint8_t* begin = find_bytes(cursor, end, player_open, sizeof(player_open) - 1);
        if (!begin) break;
        const uint8_t* close = find_bytes(begin + sizeof(player_open) - 1, end,
                                          player_close, sizeof(player_close) - 1);
        if (!close) return -3;
        const uint8_t* record_end = close + sizeof(player_close) - 1;
        if (count >= capacity) return -2;

        laplace_fide_player_projection_t* p = &out[count];
#define PROJECT(member, tag) \
        do { int rc = field_span(utf8, begin, record_end, tag, &p->member); \
             if (rc != 0) return rc; } while (0)
        PROJECT(fide_id, "fideid");
        PROJECT(name, "name");
        PROJECT(country, "country");
        PROJECT(sex, "sex");
        PROJECT(title, "title");
        PROJECT(standard_rating, "rating");
        PROJECT(rapid_rating, "rapid_rating");
        PROJECT(blitz_rating, "blitz_rating");
        PROJECT(birthday, "birthday");
        PROJECT(flag, "flag");
#undef PROJECT
        ++count;
        cursor = record_end;
    }
    *out_count = count;
    return 0;
}
