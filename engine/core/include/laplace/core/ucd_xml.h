#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* SAX2-shaped element events over UCD flat XML (and documents like it).
 *
 * UCD nounihan flat is a property table (millions of shallow elements), not a
 * nested container, so this path streams tags and fires callbacks without
 * materializing a tree. Nested containers (code, JSON, …) unpack through the
 * tree-sitter grammars.
 *
 * Element names + attribute values are passed through verbatim (no entity
 * expansion; the UCD attribute set we read contains none).
 *
 * attrs is a NUL-terminated name/value pointer array (n0,v0,n1,v1,...,NULL),
 * or NULL when the element has no attributes — identical shape to SAX2
 * startElement. Pointers are only valid for the duration of the callback. */
typedef void (*laplace_ucd_xml_start_cb)(void* user, const char* name, const char** attrs);
typedef void (*laplace_ucd_xml_end_cb)(void* user, const char* name);

/* Parse buf[0..len) as XML and fire start/end events in document order.
 * Returns 0 on success, -1 on bad args, -2 on malformed input. */
int laplace_ucd_xml_parse(const uint8_t* buf, size_t len,
                          laplace_ucd_xml_start_cb on_start,
                          laplace_ucd_xml_end_cb on_end,
                          void* user);

#ifdef __cplusplus
}
#endif
