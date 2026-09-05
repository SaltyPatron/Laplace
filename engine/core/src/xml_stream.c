#include "laplace/core/xml_stream.h"
#include "laplace/core/grammar_registry.h"
#include <libxml/parser.h>
#include <libxml/entities.h>
#include <limits.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>

typedef struct xml_event_block {
    struct xml_event_block* next;
    size_t used, capacity;
    char data[];
} xml_event_block;

struct laplace_xml_stream {
    xmlParserCtxtPtr parser;
    xmlSAXHandler sax;
    laplace_xml_event_t* events;
    size_t count, capacity;
    xml_event_block* blocks;
    int depth, failed, finished;
    char error[256];
};

static void clear_events(laplace_xml_stream_t* s) {
    while (s->blocks) {
        xml_event_block* next = s->blocks->next;
        free(s->blocks); s->blocks = next;
    }
    s->count = 0;
}

static char* copy_string(laplace_xml_stream_t* s, const char* src, size_t len) {
    if (len == SIZE_MAX) return NULL;
    size_t need = len + 1;
    if (!s->blocks || need > s->blocks->capacity - s->blocks->used) {
        size_t capacity = need > 65536 ? need : 65536;
        if (capacity > SIZE_MAX - sizeof(xml_event_block)) return NULL;
        xml_event_block* block = malloc(sizeof(*block) + capacity);
        if (!block) return NULL;
        *block = (xml_event_block){s->blocks, 0, capacity};
        s->blocks = block;
    }
    char* p = s->blocks->data + s->blocks->used;
    s->blocks->used += need;
    if (len) memcpy(p, src, len);
    p[len] = 0;
    return p;
}

static void emit(laplace_xml_stream_t* s, int kind, int depth,
                 const xmlChar* name, const xmlChar* value, size_t len,
                 const xmlChar* uri, const xmlChar* prefix) {
    if (s->failed) return;
    if (s->count == s->capacity) {
        size_t cap = s->capacity ? s->capacity * 2 : 128;
        if (cap < s->capacity || cap > SIZE_MAX / sizeof(*s->events)) {
            s->failed = -3; return;
        }
        void* next = realloc(s->events, cap * sizeof(*s->events));
        if (!next) { s->failed = -3; return; }
        s->events = next; s->capacity = cap;
    }
    char* n = copy_string(s, (const char*)name, name ? strlen((const char*)name) : 0);
    char* v = copy_string(s, (const char*)value, len);
    char* u = copy_string(s, (const char*)uri, uri ? strlen((const char*)uri) : 0);
    char* p = copy_string(s, (const char*)prefix, prefix ? strlen((const char*)prefix) : 0);
    if (!n || !v || !u || !p) { s->failed = -3; return; }
    s->events[s->count++] = (laplace_xml_event_t){kind, depth, n, v, len, u, p};
}

static void start_element(void* ctx, const xmlChar* local, const xmlChar* prefix,
    const xmlChar* uri, int namespaces, const xmlChar** ns, int attributes,
    int defaults, const xmlChar** attrs) {
    laplace_xml_stream_t* s = ctx;
    (void)namespaces; (void)ns; (void)defaults;
    emit(s, 1, s->depth, local, NULL, 0, uri, prefix);
    for (int i = 0; i < attributes; ++i)
        emit(s, 4, s->depth, attrs[i * 5], attrs[i * 5 + 3],
             (size_t)(attrs[i * 5 + 4] - attrs[i * 5 + 3]),
             attrs[i * 5 + 2], attrs[i * 5 + 1]);
    ++s->depth;
}
static void end_element(void* ctx, const xmlChar* local,
                        const xmlChar* prefix, const xmlChar* uri) {
    laplace_xml_stream_t* s = ctx;
    emit(s, 2, --s->depth, local, NULL, 0, uri, prefix);
}
static void characters(void* ctx, const xmlChar* text, int len) {
    laplace_xml_stream_t* s = ctx;
    if (len > 0) emit(s, 3, s->depth - 1, NULL, text, (size_t)len, NULL, NULL);
}
static xmlEntityPtr get_entity(void* ctx, const xmlChar* name) {
    (void)ctx;
    /* Only XML's five predefined references are entities in this source codec.
     * Numeric references are decoded by libxml2. Neither a network location nor
     * an internal DTD declaration can cause a second physical artifact read. */
    return xmlGetPredefinedEntity(name);
}
static void xml_error(void* ctx, xmlErrorPtr error) {
    laplace_xml_stream_t* s = ctx;
    if (error && error->level >= XML_ERR_ERROR) {
        s->failed = -2;
        snprintf(s->error, sizeof(s->error), "line %d: %s", error->line,
                 error->message ? error->message : "invalid XML");
    }
}

int laplace_xml_stream_new(laplace_xml_stream_t** out) {
    if (!out || !laplace_grammar_lookup_by_id("xml")) return -1;
    *out = NULL;
    laplace_xml_stream_t* s = calloc(1, sizeof(*s));
    if (!s) return -3;
    s->sax.initialized = XML_SAX2_MAGIC;
    s->sax.startElementNs = start_element;
    s->sax.endElementNs = end_element;
    s->sax.characters = characters;
    s->sax.cdataBlock = characters;
    s->sax.getEntity = get_entity;
    s->sax.serror = xml_error;
    s->parser = xmlCreatePushParserCtxt(&s->sax, s, NULL, 0, NULL);
    if (!s->parser) { free(s); return -3; }
    xmlCtxtUseOptions(s->parser, XML_PARSE_NONET | XML_PARSE_NOENT);
    *out = s;
    return 0;
}

int laplace_xml_stream_feed(laplace_xml_stream_t* s, const uint8_t* bytes,
    size_t len, int final, const laplace_xml_event_t** events, size_t* count) {
    if (!events || !count) return -1;
    *events = NULL; *count = 0;
    if (!s || (!bytes && len) || len > INT_MAX || s->finished || s->failed) return -1;
    clear_events(s);
    int rc = xmlParseChunk(s->parser, (const char*)bytes, (int)len, final != 0);
    if (s->failed || rc != 0) {
        if (!s->failed) s->failed = -2;
        clear_events(s);
        return s->failed;
    }
    if (final) s->finished = 1;
    *events = s->events; *count = s->count;
    return 0;
}
const char* laplace_xml_stream_error(const laplace_xml_stream_t* s) {
    return s ? s->error : "invalid XML stream";
}
void laplace_xml_stream_free(laplace_xml_stream_t* s) {
    if (!s) return;
    xmlFreeParserCtxt(s->parser);
    clear_events(s);
    free(s->events);
    free(s);
}
