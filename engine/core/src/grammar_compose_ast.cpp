#include "grammar_compose_internal.h"
#include "laplace/core/entity_type_law.h"

#include <stdlib.h>
#include <string.h>

#include "laplace/core/codepoint_table.h"
#include "laplace/core/content_witness_batch.h"
#include "laplace/core/utf8.h"
#include "laplace/core/hash128.h"
#include "laplace/core/hash_composer.h"
#include "laplace/core/mantissa.h"
#include "laplace/core/hilbert4d.h"

/* Grammar-composed entities are typed by content tier through
 * laplace_content_tier_type_id, the same map every content path uses. A parser
 * node name is the grammar's private vocabulary, never an entity type, and type_id
 * is not an input to identity: equal content from any grammar or ladder is one
 * entity. */

int laplace_compose_codepoint_resolver(uint32_t atom, void* ctx,
                                       hash128_t* out_id, double out_coord[4],
                                       hilbert128_t* out_hb) {
    (void)ctx;
    return codepoint_table_resolve_atom(atom, out_id, out_coord, out_hb);
}

int laplace_is_json_modality(const char* modality_id) {
    return modality_id && strcmp(modality_id, "json") == 0;
}

/* A JSON string is one semantic scalar even when tree-sitter splits its
 * source spelling into string_content and escape_sequence children. Those
 * fragments remain available for span navigation, but the complete string
 * owns decoded grapheme occurrences and parent composition. */
int laplace_json_node_is_string(laplace_ast_t* ast, const laplace_ast_node_t* node) {
    const char* type = laplace_ast_type_name(ast, node->type_id);
    return type && strcmp(type, "string") == 0;
}

int laplace_json_node_is_string_fragment(laplace_ast_t* ast,
                                         const laplace_ast_node_t* node) {
    if (node->parent == LAPLACE_AST_ROOT) return 0;
    laplace_ast_node_t parent;
    return laplace_ast_get_node(ast, node->parent, &parent) == 0
        && laplace_json_node_is_string(ast, &parent);
}

static int json_hex_digit(uint8_t c) {
    if (c >= '0' && c <= '9') return (int)(c - '0');
    if (c >= 'a' && c <= 'f') return (int)(c - 'a' + 10);
    if (c >= 'A' && c <= 'F') return (int)(c - 'A' + 10);
    return -1;
}

static int json_span_has_escapes(const uint8_t* span, size_t span_len) {
    for (size_t i = 0; i < span_len; ++i)
        if (span[i] == (uint8_t)'\\') return 1;
    return 0;
}

int laplace_json_string_decode(const uint8_t* input, size_t length,
                               uint8_t* output, size_t capacity, size_t* written) {
    if (!written) return -1;
    *written = 0;
    if ((!input && length != 0) || (!output && capacity != 0)) return -1;

    size_t i = 0, w = 0;
    while (i < length) {
        uint32_t cp = input[i++];
        if (cp == '\\') {
            if (i == length) return -1;
            switch (input[i++]) {
                case '"': cp = '"'; break;
                case '\\': cp = '\\'; break;
                case '/': cp = '/'; break;
                case 'b': cp = '\b'; break;
                case 'f': cp = '\f'; break;
                case 'n': cp = '\n'; break;
                case 'r': cp = '\r'; break;
                case 't': cp = '\t'; break;
                case 'u': {
                    if (length - i < 4) return -1;
                    cp = 0;
                    for (size_t h = 0; h < 4; ++h) {
                        int digit = json_hex_digit(input[i++]);
                        if (digit < 0) return -1;
                        cp = (cp << 4) | (uint32_t)digit;
                    }
                    if (cp >= 0xD800 && cp <= 0xDBFF) {
                        if (length - i < 6 || input[i] != '\\' || input[i + 1] != 'u')
                            return -1;
                        i += 2;
                        uint32_t low = 0;
                        for (size_t h = 0; h < 4; ++h) {
                            int digit = json_hex_digit(input[i++]);
                            if (digit < 0) return -1;
                            low = (low << 4) | (uint32_t)digit;
                        }
                        if (low < 0xDC00 || low > 0xDFFF) return -1;
                        cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00);
                    } else if (cp >= 0xDC00 && cp <= 0xDFFF) {
                        return -1;
                    }
                    break;
                }
                default: return -1;
            }
        } else {
            if (cp < 0x20 || cp == '"') return -1;
            size_t consumed = 0;
            if (laplace_utf8_decode(input + i - 1, length - (i - 1), &cp, &consumed) != 0)
                return -1;
            i += consumed - 1;
        }
        uint8_t encoded[4];
        size_t n = laplace_utf8_encode(cp, encoded);
        if (n <= capacity && w <= capacity - n)
            memcpy(output + w, encoded, n);
        w += n;
    }
    *written = w;
    return w <= capacity ? 0 : -2;
}

static int json_unescape_utf8(const uint8_t* span, size_t span_len,
                              uint8_t** out, size_t* out_len) {
    *out = NULL;
    *out_len = 0;
    uint8_t* buf = (uint8_t*)malloc(span_len ? span_len : 1);
    if (!buf) return -3;
    size_t written = 0;
    int rc = laplace_json_string_decode(span, span_len, buf, span_len, &written);
    if (rc != 0) { free(buf); return rc; }
    *out = buf;
    *out_len = written;
    return 0;
}

static int emit_grapheme_floor_entities(
    laplace_compose_result_t* r,
    tier_tree_t* tree, const laplace_grapheme_floor_t* floor,
    hash128_t** emitted_entity, size_t* emitted_entity_n, size_t* emitted_entity_cap) {
    hash128_t grapheme_type;
    (void)laplace_entity_type_id("Grapheme", &grapheme_type);
    size_t g_first = laplace_grapheme_floor_graph_first_idx(floor);
    size_t g_count = laplace_grapheme_floor_graph_count(floor);
    for (size_t g = 0; g < g_count; ++g) {
        tier_node_view_t gv;
        if (tier_tree_get_node(tree, (uint32_t)(g_first + g), &gv) != 0) continue;
        /* Grapheme-floor law: a single-codepoint cluster IS its codepoint
         * (same bytes, same id -- hash_composer copies the sole child's id).
         * It is scaffold only; emitting it would mint a wrong-tier (tier 1)
         * row for a tier-0 identity. Only multi-codepoint clusters (combining
         * stacks, ZWJ sequences, Hangul, emoji) are real tier-1 content. */
        if (gv.child_count == 1) continue;
        const int novel_entity = laplace_compose_id_push(
            emitted_entity, emitted_entity_n, emitted_entity_cap, gv.id);
        if (novel_entity < 0) return -3;
        if (novel_entity && laplace_compose_push_entity(r, gv.id, 1, grapheme_type, 0) != 0) return -3;
        /* Native leaf trees own the actual codepoint decomposition. */
    }
    return 0;
}

/* +1 means syntax/empty content with no constituent; negative results are real
 * decoding/allocation failures and must abort composition, not drop a value. */
static int native_leaf_root(const uint8_t* bytes, size_t length,
                            laplace_compose_result_t* result,
                            hash128_t* id, uint8_t* tier, double* coord) {
    tier_tree_t* tree = NULL;
    int rc = content_witness_tree_build(bytes, length, &tree);
    if (rc != 0) return rc;
    tier_node_view_t root;
    rc = content_witness_tree_root_node(tree, &root);
    if (rc == 0 && result) {
        rc = laplace_compose_reserve_rows(result, 0, 0, result->source_tree_count + 1);
        if (rc == 0) result->source_trees[result->source_tree_count++] = tree;
    }
    if (rc == 0) {
        if (id) *id = root.id;
        if (tier) *tier = root.tier;
        if (coord) memcpy(coord, root.coord, 4 * sizeof(double));
    }
    if (rc != 0 || !result) tier_tree_free(tree);
    return rc;
}

static int json_leaf_fill_grapheme_children(
    const uint8_t* utf8, size_t len, laplace_ast_t* ast, size_t idx,
    laplace_compose_result_t* r,
    hash128_t** emitted_entity, size_t* emitted_entity_n, size_t* emitted_entity_cap,
    hash128_t** out_ids, double** out_coords, uint64_t** out_flags, size_t* out_m,
    hash128_t* out_root_id, uint8_t* out_root_tier = NULL, double* out_root_coord = NULL) {
    *out_ids = NULL;
    *out_coords = NULL;
    *out_flags = NULL;
    *out_m = 0;
    if (out_root_id) hash128_zero(out_root_id);
    if (out_root_tier) *out_root_tier = 0;

    laplace_ast_node_t node;
    if (laplace_ast_get_node(ast, idx, &node) != 0) return -1;
    if (node.end_byte <= node.start_byte || node.end_byte > len) return -1;

    const uint8_t* span = utf8 + node.start_byte;
    size_t span_len = (size_t)(node.end_byte - node.start_byte);
    const char* nt = laplace_ast_type_name(ast, node.type_id);
    if (nt && strcmp(nt, "string_content") == 0) {
        
    } else if (nt && strcmp(nt, "string") == 0 && span_len >= 2
               && span[0] == '"' && span[span_len - 1] == '"') {
        span += 1;
        span_len -= 2;
    } else if (!nt || (strcmp(nt, "number") != 0 && strcmp(nt, "true") != 0
                       && strcmp(nt, "false") != 0 && strcmp(nt, "null") != 0)) {
        return 1;
    }
    if (span_len == 0) return 1;

    const uint8_t* content_span = span;
    size_t content_len = span_len;
    uint8_t* decoded_owned = NULL;
    if (json_span_has_escapes(span, span_len)) {
        int decode_rc = json_unescape_utf8(span, span_len, &decoded_owned, &content_len);
        if (decode_rc != 0) return decode_rc;
        if (content_len == 0) { free(decoded_owned); return 1; }
        content_span = decoded_owned;
    }

    if (out_root_id || out_root_tier) {
        int root_rc = native_leaf_root(content_span, content_len, r,
                                      out_root_id, out_root_tier, out_root_coord);
        if (root_rc != 0) { free(decoded_owned); return root_rc; }
    }

    tier_tree_t* local_tree = NULL;
    laplace_grapheme_floor_t local_floor;
    memset(&local_floor, 0, sizeof(local_floor));
    if (laplace_grapheme_floor_build(content_span, content_len, &local_tree, &local_floor) != 0) {
        free(decoded_owned);
        return -1;
    }
    if (codepoint_table_is_loaded()) {
        if (hash_composer_run(local_tree, laplace_compose_codepoint_resolver, NULL) != 0) {
            laplace_grapheme_floor_free(&local_floor);
            tier_tree_free(local_tree);
            free(decoded_owned);
            return -1;
        }
    }

    if (r && emitted_entity) {
        int erc = emit_grapheme_floor_entities(
            r, local_tree, &local_floor,
            emitted_entity, emitted_entity_n, emitted_entity_cap);
        if (erc != 0) {
            laplace_grapheme_floor_free(&local_floor);
            tier_tree_free(local_tree);
            free(decoded_owned);
            return erc;
        }
    }

    size_t g_first = laplace_grapheme_floor_graph_first_idx(&local_floor);
    size_t g_count = laplace_grapheme_floor_graph_count(&local_floor);
    hash128_t* ids = (hash128_t*)malloc(g_count * sizeof(hash128_t));
    double* coords = (double*)malloc(g_count * 4 * sizeof(double));
    uint64_t* flags = (uint64_t*)malloc(g_count * sizeof(uint64_t));
    if (!ids || !coords || !flags) {
        free(ids);
        free(coords);
        free(flags);
        laplace_grapheme_floor_free(&local_floor);
        tier_tree_free(local_tree);
        free(decoded_owned);
        return -3;
    }
    size_t w = 0;
    for (size_t g = 0; g < g_count; ++g) {
        tier_node_view_t gv;
        if (tier_tree_get_node(local_tree, (uint32_t)(g_first + g), &gv) != 0) continue;
        ids[w] = gv.id;
        memcpy(coords + w * 4, gv.coord, 4 * sizeof(double));
        flags[w] = laplace_vertex_flags(1, 0, 0);
        w++;
    }
    laplace_grapheme_floor_free(&local_floor);
    tier_tree_free(local_tree);
    if (w == 0) {
        free(ids);
        free(coords);
        free(flags);
        free(decoded_owned);
        return -1;
    }
    *out_ids = ids;
    *out_coords = coords;
    *out_flags = flags;
    *out_m = w;
    free(decoded_owned);
    return 0;
}

int laplace_compose_ast_nodes(const uint8_t* utf8, size_t len, laplace_ast_t* ast,
                              const char* modality_id,
                              const laplace_grapheme_floor_t* floor, tier_tree_t* tree,
                              laplace_compose_result_t* r,
                              hash128_t** emitted_entity, size_t* emitted_entity_n,
                              size_t* emitted_entity_cap,
                              compose_state_t* st) {
    size_t n = laplace_ast_node_count(ast);
    int rc = 0;
    st->n = n;
    if (n == 0) return 0;

    st->comp_id    = (hash128_t*)calloc(n, sizeof(hash128_t));
    st->comp_coord = (double*)calloc(n * 4, sizeof(double));
    st->comp_tier  = (uint8_t*)calloc(n, 1);
    st->entity_tier = (uint8_t*)calloc(n, 1);
    st->comp_valid = (uint8_t*)calloc(n, 1);
    if (!st->comp_id || !st->comp_coord || !st->comp_tier || !st->entity_tier || !st->comp_valid)
        return -3;

    size_t graph_first = laplace_grapheme_floor_graph_first_idx(floor);

    uint32_t** children_of = (uint32_t**)calloc(n, sizeof(uint32_t*));
    uint32_t*  child_counts = (uint32_t*)calloc(n, sizeof(uint32_t));
    if (!children_of || !child_counts) {
        free(children_of);
        free(child_counts);
        return -3;
    }

    for (size_t i = 0; i < n; ++i) {
        laplace_ast_node_t nd;
        if (laplace_ast_get_node(ast, i, &nd) != 0) continue;
        if (nd.parent != LAPLACE_AST_ROOT && nd.parent < n) {
            uint32_t p = nd.parent;
            uint32_t cc = child_counts[p]++;
            uint32_t* new_ch = (uint32_t*)realloc(children_of[p], (size_t)(cc + 1) * sizeof(uint32_t));
            if (!new_ch) {
                for (size_t j = 0; j < n; ++j) free(children_of[j]);
                free(children_of);
                free(child_counts);
                return -3;
            }
            children_of[p] = new_ch;
            children_of[p][cc] = (uint32_t)i;
        }
    }

    for (size_t idx = n; idx-- > 0;) {
        laplace_ast_node_t node;
        if (laplace_ast_get_node(ast, idx, &node) != 0) continue;
        uint32_t kid_n = child_counts[idx];
        const int json_string = laplace_is_json_modality(modality_id)
            && laplace_json_node_is_string(ast, &node);
        const int json_fragment = laplace_is_json_modality(modality_id)
            && laplace_json_node_is_string_fragment(ast, &node);

        hash128_t*  child_ids    = NULL;
        double*     child_coords = NULL;
        uint64_t*   child_flags  = NULL;
        size_t      m            = 0;
        uint8_t     tier         = 0;
        hash128_t   leaf_root_id;
        uint8_t     leaf_root_tier = 0;
        double      leaf_root_coord[4] = {0};
        hash128_zero(&leaf_root_id);

        if (kid_n > 0 && !json_string) {
            child_ids    = (hash128_t*)malloc(kid_n * sizeof(hash128_t));
            child_coords = (double*)malloc(kid_n * 4 * sizeof(double));
            child_flags  = (uint64_t*)malloc(kid_n * sizeof(uint64_t));
            if (!child_ids || !child_coords || !child_flags) {
                free(child_ids); free(child_coords); free(child_flags);
                continue;
            }
            uint8_t max_tier = 0;
            size_t w = 0;
            for (uint32_t j = 0; j < kid_n; ++j) {
                uint32_t c = children_of[idx][j];
                if (!st->comp_valid[c]) continue;
                child_ids[w] = st->comp_id[c];
                memcpy(child_coords + w * 4, st->comp_coord + c * 4, 4 * sizeof(double));
                uint8_t ct = st->comp_tier[c];
                child_flags[w] = laplace_vertex_flags(ct, 0, 0);
                if (ct > max_tier) max_tier = ct;
                w++;
            }
            if (w == 0) {
                free(child_ids); free(child_coords); free(child_flags);
                continue;
            }
            m = w;
            {
                const uint8_t TIER_DOCUMENT = 4;
                uint8_t next = (uint8_t)(max_tier + 1);
                tier = next < TIER_DOCUMENT ? next : TIER_DOCUMENT;
            }
        } else if (laplace_is_json_modality(modality_id)) {
            int leaf_rc = json_leaf_fill_grapheme_children(
                    utf8, len, ast, idx, json_fragment ? NULL : r,
                    emitted_entity, emitted_entity_n, emitted_entity_cap,
                    &child_ids, &child_coords, &child_flags, &m,
                    &leaf_root_id, &leaf_root_tier, leaf_root_coord);
            if (leaf_rc < 0) { rc = leaf_rc; goto done; }
            if (leaf_rc > 0) continue;
            tier = json_string && kid_n > 0 ? 3 : 2;
        } else {
            size_t g_start = 0, g_end = 0;
            if (laplace_grapheme_floor_span_to_graphemes(
                    floor, node.start_byte, node.end_byte, &g_start, &g_end) != 0) {
                continue;
            }
            m = g_end - g_start;
            child_ids    = (hash128_t*)malloc(m * sizeof(hash128_t));
            child_coords = (double*)malloc(m * 4 * sizeof(double));
            child_flags  = (uint64_t*)malloc(m * sizeof(uint64_t));
            if (!child_ids || !child_coords || !child_flags) {
                free(child_ids); free(child_coords); free(child_flags);
                continue;
            }
            size_t filled = 0;
            for (size_t g = g_start; g < g_end; ++g) {
                tier_node_view_t gv;
                uint32_t gidx = (uint32_t)(graph_first + g);
                if (tier_tree_get_node(tree, gidx, &gv) != 0) continue;
                child_ids[filled] = gv.id;
                memcpy(child_coords + filled * 4, gv.coord, 4 * sizeof(double));
                child_flags[filled] = laplace_vertex_flags(1, 0, 0);
                filled++;
            }
            m = filled;
            tier = 2;
            if (m) {
                rc = native_leaf_root(utf8 + node.start_byte, node.end_byte - node.start_byte,
                                      r, &leaf_root_id, &leaf_root_tier, leaf_root_coord);
                if (rc != 0) { free(child_ids); free(child_coords); free(child_flags); goto done; }
            }
        }

        if (m == 0) {
            free(child_ids);
            free(child_coords);
            free(child_flags);
            continue;
        }

        double out_coord[4];
        hilbert128_t hb;
        rc = hash_composer_compose_node(tier, child_ids, child_coords, m,
                                       &st->comp_id[idx], out_coord, &hb);
        if (rc != 0) {
            free(child_ids);
            free(child_coords);
            free(child_flags);
            goto done;
        }
        if (leaf_root_id.hi != 0 || leaf_root_id.lo != 0) {
            st->comp_id[idx] = leaf_root_id;
            memcpy(out_coord, leaf_root_coord, sizeof out_coord);
        }
        memcpy(st->comp_coord + idx * 4, out_coord, 4 * sizeof(double));
        st->comp_tier[idx]  = tier;
        st->entity_tier[idx] = leaf_root_id.hi || leaf_root_id.lo ? leaf_root_tier : tier;
        st->comp_valid[idx] = 1;

        free(child_ids);
        free(child_coords);
        free(child_flags);
    }

done:
    for (size_t i = 0; i < n; ++i) {
        free(children_of[i]);
    }
    free(children_of);
    free(child_counts);
    return rc;
}
