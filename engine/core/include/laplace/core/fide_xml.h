#ifndef LAPLACE_CORE_FIDE_XML_H
#define LAPLACE_CORE_FIDE_XML_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct laplace_text_span {
    uint32_t offset;
    uint32_t length;
} laplace_text_span_t;

typedef struct laplace_fide_player_projection {
    laplace_text_span_t fide_id;
    laplace_text_span_t name;
    laplace_text_span_t country;
    laplace_text_span_t sex;
    laplace_text_span_t title;
    laplace_text_span_t standard_rating;
    laplace_text_span_t rapid_rating;
    laplace_text_span_t blitz_rating;
    laplace_text_span_t birthday;
    laplace_text_span_t flag;
} laplace_fide_player_projection_t;

/* Project the flat fields from concatenated FIDE <player> records. Spans point
 * into utf8; no strings or second semantic representation are allocated.
 * Returns 0, -1 for invalid arguments, -2 for insufficient output capacity,
 * or -3 for malformed record/field markup. */
int laplace_fide_xml_project(const uint8_t* utf8, size_t len,
                             laplace_fide_player_projection_t* out,
                             size_t capacity, size_t* out_count);

#ifdef __cplusplus
}
#endif

#endif
