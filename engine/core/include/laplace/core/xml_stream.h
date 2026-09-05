#ifndef LAPLACE_CORE_XML_STREAM_H
#define LAPLACE_CORE_XML_STREAM_H
#include <stddef.h>
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
typedef struct laplace_xml_stream laplace_xml_stream_t;
typedef struct laplace_xml_event {
    int32_t kind; /* 1 start, 2 end, 3 text, 4 attribute */
    int32_t depth; /* containing element depth, root = 0 */
    const char* name; /* local name; empty for text */
    const char* value;
    size_t value_len;
    const char* namespace_uri;
    const char* prefix;
} laplace_xml_event_t;
/* Registered XML source decoder. No whole-document tree, external entity loading,
 * or source-specific field rules. The caller owns input reads and cancellation.
 * Feed returns a batch valid until the next feed/free. Text events may be split
 * anywhere; consumers concatenate them within their selected record boundary.
 * Return 0 success, -1 invalid state/arguments, -2 XML failure, -3 allocation failure. */
int laplace_xml_stream_new(laplace_xml_stream_t** out);
int laplace_xml_stream_feed(laplace_xml_stream_t*, const uint8_t*, size_t, int final,
                           const laplace_xml_event_t** events, size_t* count);
const char* laplace_xml_stream_error(const laplace_xml_stream_t*);
void laplace_xml_stream_free(laplace_xml_stream_t*);
#ifdef __cplusplus
}
#endif
#endif
