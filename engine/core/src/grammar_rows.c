#include "laplace/core/grammar_decomposer.h"

#include <stdlib.h>
#include <string.h>
#include <stdbool.h>
#include <stdint.h>

#include "tree_sitter/api.h"

extern const TSLanguage* tree_sitter_csv(void);
extern const TSLanguage* tree_sitter_tsv(void);

typedef struct { uint32_t s; uint32_t e; } laplace_row_rng_t;

struct laplace_grammar_row_iter {
    const TSLanguage* recipe;
    TSParser*         parser;
    uint8_t*          carry;
    size_t            carry_len;
    size_t            carry_cap;
    int               oom;
    TSSymbol          row_symbol;
    int               row_structured;
    int               force_line_framed;
    uint8_t           delimited_separator;
};

int laplace_grammar_row_iter_new(const TSLanguage* recipe,
                                 laplace_grammar_row_iter_t** out) {
    if (!recipe || !out) return -1;
    laplace_grammar_row_iter_t* it =
        (laplace_grammar_row_iter_t*)calloc(1, sizeof(*it));
    if (!it) return -3;
    it->recipe = recipe;
    it->parser = ts_parser_new();
    if (!it->parser) { free(it); return -3; }
    if (!ts_parser_set_language(it->parser, recipe)) {
        ts_parser_delete(it->parser);
        free(it);
        return -2;
    }
    it->row_symbol = ts_language_symbol_for_name(recipe, "row", 3, true);
    it->row_structured = it->row_symbol != 0;
    if (recipe == tree_sitter_csv()) it->delimited_separator = ',';
    else if (recipe == tree_sitter_tsv()) it->delimited_separator = '\t';
    *out = it;
    return 0;
}

void laplace_grammar_row_iter_set_line_framed(laplace_grammar_row_iter_t* it, int on) {
    if (it) it->force_line_framed = on ? 1 : 0;
}

static int use_grammar_row_framing(const laplace_grammar_row_iter_t* it) {
    return it->row_structured && !it->force_line_framed;
}

static int use_delimited_row_framing(const laplace_grammar_row_iter_t* it) {
    return it->delimited_separator != 0 && !it->force_line_framed;
}

static int append_carry(laplace_grammar_row_iter_t* it,
                        const uint8_t* chunk, size_t len) {
    if (len > SIZE_MAX - it->carry_len) {
        it->oom = 1;
        return -3;
    }
    const size_t needed = it->carry_len + len;
    if (needed > it->carry_cap) {
        size_t ncap = it->carry_cap ? it->carry_cap : needed;
        while (ncap < needed) {
            if (ncap > SIZE_MAX / 2) {
                ncap = needed;
                break;
            }
            ncap *= 2;
        }
        uint8_t* n = (uint8_t*)realloc(it->carry, ncap);
        if (!n) { it->oom = 1; return -3; }
        it->carry = n;
        it->carry_cap = ncap;
    }
    memcpy(it->carry + it->carry_len, chunk, len);
    it->carry_len += len;
    return 0;
}

static int append_raw_row_copy(laplace_grammar_row_iter_t* it,
                               laplace_raw_row_t** rows,
                               size_t* row_cap,
                               size_t* row_n,
                               const uint8_t* src,
                               size_t len) {
    if (len == 0) return 0;
    if (*row_n >= *row_cap) {
        size_t ncap = *row_cap ? *row_cap * 2 : 64;
        laplace_raw_row_t* n = (laplace_raw_row_t*)realloc(
            *rows, ncap * sizeof(*n));
        if (!n) { it->oom = 1; return -3; }
        *rows = n;
        *row_cap = ncap;
    }
    uint8_t* copy = (uint8_t*)malloc(len);
    if (!copy) { it->oom = 1; return -3; }
    memcpy(copy, src, len);
    (*rows)[*row_n].row_utf8 = copy;
    (*rows)[*row_n].row_len = len;
    (*row_n)++;
    return 0;
}

static int split_carry_lines(laplace_grammar_row_iter_t* it, int finalize,
                             laplace_raw_row_t** out_rows, size_t* out_count) {
    *out_rows = NULL;
    *out_count = 0;

    size_t row_cap = 0, row_n = 0;
    laplace_raw_row_t* rows = NULL;
    size_t start = 0;
    for (size_t i = 0; i < it->carry_len; ++i) {
        if (it->carry[i] != '\n') continue;
        size_t row_len = i - start;
        if (row_len > 0) {
            if (row_n >= row_cap) {
                size_t ncap = row_cap ? row_cap * 2 : 64;
                laplace_raw_row_t* n = (laplace_raw_row_t*)realloc(
                    rows, ncap * sizeof(*n));
                if (!n) { it->oom = 1; goto fail; }
                rows = n;
                row_cap = ncap;
            }
            uint8_t* copy = (uint8_t*)malloc(row_len);
            if (!copy) { it->oom = 1; goto fail; }
            memcpy(copy, it->carry + start, row_len);
            rows[row_n].row_utf8 = copy;
            rows[row_n].row_len = row_len;
            row_n++;
        }
        start = i + 1;
    }

    if (finalize && start < it->carry_len) {
        size_t row_len = it->carry_len - start;
        if (row_len > 0) {
            if (row_n >= row_cap) {
                size_t ncap = row_cap ? row_cap * 2 : 64;
                laplace_raw_row_t* n = (laplace_raw_row_t*)realloc(rows, ncap * sizeof(*n));
                if (!n) { it->oom = 1; goto fail; }
                rows = n;
                row_cap = ncap;
            }
            uint8_t* copy = (uint8_t*)malloc(row_len);
            if (!copy) { it->oom = 1; goto fail; }
            memcpy(copy, it->carry + start, row_len);
            rows[row_n].row_utf8 = copy;
            rows[row_n].row_len = row_len;
            row_n++;
        }
        start = it->carry_len;
    }

    if (start > 0 && start < it->carry_len) {
        size_t rem = it->carry_len - start;
        memmove(it->carry, it->carry + start, rem);
        it->carry_len = rem;
    } else if (start >= it->carry_len) {
        it->carry_len = 0;
    }

    *out_rows = rows;
    *out_count = row_n;
    return 0;

fail:
    for (size_t j = 0; j < row_n; ++j)
        free(rows[j].row_utf8);
    free(rows);
    return -3;
}

/* CSV/TSV need a codec-level framing pass before Tree-sitter parses each recovered
 * record. Parsing an incomplete prefix is not a safe boundary oracle: before the
 * closing quote arrives, the vendor grammar can legally tokenize the opening quote
 * as unquoted text and expose an embedded newline as a temporary row boundary.
 * Emitting that speculative CST row makes ReadAsync/feed chunk size semantic.
 *
 * This scanner only owns source-record framing. It does not interpret field values;
 * the grammar still parses each emitted byte-exact record. One complete record is
 * deliberately retained on non-final feeds, preserving the iterator's existing
 * lookahead contract while making that lookahead depend on real delimiters rather
 * than a speculative partial parse. */
static int split_carry_delimited_records(laplace_grammar_row_iter_t* it, int finalize,
                                         laplace_raw_row_t** out_rows, size_t* out_count) {
    *out_rows = NULL;
    *out_count = 0;
    if (it->carry_len == 0) return 0;

    size_t row_cap = 0, row_n = 0;
    laplace_raw_row_t* rows = NULL;
    size_t start = 0;
    size_t pending_start = 0, pending_len = 0;
    int have_pending = 0;
    bool in_quotes = false;
    bool field_start = true;

    for (size_t i = 0; i < it->carry_len; ++i) {
        uint8_t ch = it->carry[i];

        if (in_quotes) {
            if (ch == '"') {
                if (i + 1 < it->carry_len && it->carry[i + 1] == '"') {
                    ++i;
                    continue;
                }
                in_quotes = false;
            }
            continue;
        }

        if (ch == '"' && field_start) {
            in_quotes = true;
            field_start = false;
            continue;
        }

        if (ch == it->delimited_separator) {
            field_start = true;
            continue;
        }

        if (ch != '\r' && ch != '\n') {
            field_start = false;
            continue;
        }

        size_t row_len = i - start;
        size_t after = i + 1;
        if (ch == '\r' && after < it->carry_len && it->carry[after] == '\n')
            ++after;

        if (row_len > 0) {
            if (have_pending) {
                if (append_raw_row_copy(it, &rows, &row_cap, &row_n,
                                        it->carry + pending_start, pending_len) != 0)
                    goto fail;
            }
            pending_start = start;
            pending_len = row_len;
            have_pending = 1;
        }

        start = after;
        field_start = true;
        i = after - 1;
    }

    if (finalize) {
        if (have_pending) {
            if (append_raw_row_copy(it, &rows, &row_cap, &row_n,
                                    it->carry + pending_start, pending_len) != 0)
                goto fail;
        }
        if (start < it->carry_len) {
            if (append_raw_row_copy(it, &rows, &row_cap, &row_n,
                                    it->carry + start, it->carry_len - start) != 0)
                goto fail;
        }
        it->carry_len = 0;
    } else {
        size_t keep_start = have_pending ? pending_start : start;
        if (keep_start > 0) {
            size_t rem = it->carry_len - keep_start;
            if (rem > 0) memmove(it->carry, it->carry + keep_start, rem);
            it->carry_len = rem;
        }
    }

    *out_rows = rows;
    *out_count = row_n;
    return 0;

fail:
    for (size_t j = 0; j < row_n; ++j)
        free(rows[j].row_utf8);
    free(rows);
    return -3;
}

static int split_carry_records(laplace_grammar_row_iter_t* it, int finalize,
                               laplace_raw_row_t** out_rows, size_t* out_count) {
    *out_rows = NULL;
    *out_count = 0;
    if (it->carry_len == 0) return 0;
    /* Tree-sitter's byte-index ABI is uint32_t. Crossing that real format
     * boundary is an error; it must never silently change grammar-defined
     * records into newline-defined records. */
    if (it->carry_len > UINT32_MAX) return -2;

    ts_parser_reset(it->parser);
    TSTree* tree = ts_parser_parse_string(it->parser, NULL,
                                          (const char*)it->carry, (uint32_t)it->carry_len);
    if (!tree) return -3;

    TSNode   root   = ts_tree_root_node(tree);
    uint32_t nchild = ts_node_child_count(root);

    laplace_row_rng_t* rr = NULL;
    size_t rcap = 0, rn = 0;
    for (uint32_t i = 0; i < nchild; ++i) {
        TSNode c = ts_node_child(root, i);
        if (ts_node_symbol(c) != it->row_symbol) continue;
        if (rn >= rcap) {
            size_t ncap = rcap ? rcap * 2 : 64;
            laplace_row_rng_t* n = (laplace_row_rng_t*)realloc(rr, ncap * sizeof(*n));
            if (!n) { free(rr); ts_tree_delete(tree); it->oom = 1; return -3; }
            rr = n; rcap = ncap;
        }
        rr[rn].s = ts_node_start_byte(c);
        rr[rn].e = ts_node_end_byte(c);
        rn++;
    }

    size_t   emit       = 0;
    uint32_t tail_start = (uint32_t)it->carry_len;
    if (rn > 0) {
        emit       = finalize ? rn : (rn - 1);
        tail_start = (emit < rn) ? rr[emit].s : (uint32_t)it->carry_len;
    } else {
        tail_start = 0;
    }

    if (emit > 0) {
        laplace_raw_row_t* rows = (laplace_raw_row_t*)malloc(emit * sizeof(*rows));
        if (!rows) { free(rr); ts_tree_delete(tree); it->oom = 1; return -3; }
        size_t outn = 0;
        for (size_t i = 0; i < emit; ++i) {
            if (rr[i].e <= rr[i].s) continue;
            uint32_t rl = rr[i].e - rr[i].s;
            uint8_t* copy = (uint8_t*)malloc(rl);
            if (!copy) {
                for (size_t j = 0; j < outn; ++j) free(rows[j].row_utf8);
                free(rows); free(rr); ts_tree_delete(tree); it->oom = 1; return -3;
            }
            memcpy(copy, it->carry + rr[i].s, rl);
            rows[outn].row_utf8 = copy;
            rows[outn].row_len  = rl;
            outn++;
        }
        *out_rows  = rows;
        *out_count = outn;
    }

    if (tail_start > 0) {
        size_t rem = it->carry_len - tail_start;
        if (rem > 0) memmove(it->carry, it->carry + tail_start, rem);
        it->carry_len = rem;
    }

    free(rr);
    ts_tree_delete(tree);
    return 0;
}

int laplace_grammar_row_iter_feed_lines(laplace_grammar_row_iter_t* it,
                                        const uint8_t* chunk, size_t len,
                                        laplace_raw_row_t** out_rows, size_t* out_count) {
    if (!it || !out_rows || !out_count) return -1;
    *out_rows = NULL;
    *out_count = 0;
    if (it->oom) return -3;
    int finalize = (chunk == NULL || len == 0);
    if (chunk && len > 0) {
        if (append_carry(it, chunk, len) != 0) return -3;
    }
    if (use_delimited_row_framing(it))
        return split_carry_delimited_records(it, finalize, out_rows, out_count);
    if (use_grammar_row_framing(it))
        return split_carry_records(it, finalize, out_rows, out_count);
    return split_carry_lines(it, finalize, out_rows, out_count);
}

int laplace_grammar_row_iter_parse_row(laplace_grammar_row_iter_t* it,
                                       const uint8_t* row_utf8, size_t row_len,
                                       laplace_ast_t** out_ast) {
    if (!it || !row_utf8 || !out_ast) return -1;
    *out_ast = NULL;
    if (it->oom || !it->parser) return -3;
    return laplace_grammar_parse_with(it->parser, row_utf8, row_len, it->recipe, out_ast);
}

int laplace_grammar_row_iter_feed_parsed(laplace_grammar_row_iter_t* it,
                                          const uint8_t* chunk, size_t len,
                                          laplace_parsed_row_t** out_rows, size_t* out_count) {
    if (!it || !out_rows || !out_count) return -1;
    *out_rows = NULL;
    *out_count = 0;
    if (it->oom) return -3;
    int finalize = (chunk == NULL || len == 0);
    if (chunk && len > 0)
        if (append_carry(it, chunk, len) != 0) return -3;

    laplace_raw_row_t* raw = NULL;
    size_t raw_n = 0;
    int rc = use_delimited_row_framing(it)
        ? split_carry_delimited_records(it, finalize, &raw, &raw_n)
        : use_grammar_row_framing(it)
            ? split_carry_records(it, finalize, &raw, &raw_n)
            : split_carry_lines(it, finalize, &raw, &raw_n);
    if (rc != 0) return rc;
    if (raw_n == 0) return 0;

    laplace_parsed_row_t* rows =
        (laplace_parsed_row_t*)calloc(raw_n, sizeof(*rows));
    if (!rows) {
        laplace_grammar_row_iter_free_lines(raw, raw_n);
        it->oom = 1;
        return -3;
    }

    size_t out_n = 0;
    for (size_t i = 0; i < raw_n; i++) {
        laplace_ast_t* ast = NULL;
        int r = laplace_grammar_row_iter_parse_row(
            it, raw[i].row_utf8, raw[i].row_len, &ast);
        if (r == 0 && ast) {
            rows[out_n].ast      = ast;
            rows[out_n].row_utf8 = raw[i].row_utf8;
            rows[out_n].row_len  = raw[i].row_len;
            out_n++;
        } else {
            free(raw[i].row_utf8);
        }
    }
    free(raw);

    *out_rows  = rows;
    *out_count = out_n;
    return 0;
}

int laplace_grammar_row_iter_feed(laplace_grammar_row_iter_t* it,
                                  const uint8_t* chunk, size_t len,
                                  laplace_parsed_row_t** out_rows, size_t* out_count) {
    if (!it || !out_rows || !out_count) return -1;
    *out_rows = NULL;
    *out_count = 0;
    if (it->oom) return -3;
    if (chunk && len > 0) {
        if (append_carry(it, chunk, len) != 0) return -3;
    }

    laplace_raw_row_t* raw = NULL;
    size_t raw_n = 0;
    if (split_carry_lines(it, 0, &raw, &raw_n) != 0) return -3;

    if (raw_n == 0) return 0;

    laplace_parsed_row_t* rows =
        (laplace_parsed_row_t*)calloc(raw_n, sizeof(*rows));
    if (!rows) {
        laplace_grammar_row_iter_free_lines(raw, raw_n);
        it->oom = 1;
        return -3;
    }

    size_t out_n = 0;
    for (size_t i = 0; i < raw_n; ++i) {
        laplace_ast_t* ast = NULL;
        int rc = laplace_grammar_row_iter_parse_row(
            it, raw[i].row_utf8, raw[i].row_len, &ast);
        if (rc == 0 && ast) {
            rows[out_n].ast = ast;
            rows[out_n].row_utf8 = raw[i].row_utf8;
            rows[out_n].row_len = raw[i].row_len;
            out_n++;
        } else {
            free(raw[i].row_utf8);
        }
    }
    free(raw);

    *out_rows = rows;
    *out_count = out_n;
    return 0;
}

void laplace_grammar_row_iter_free(laplace_grammar_row_iter_t* it) {
    if (!it) return;
    if (it->parser) ts_parser_delete(it->parser);
    free(it->carry);
    free(it);
}

void laplace_grammar_row_iter_free_rows(laplace_parsed_row_t* rows, size_t count) {
    if (!rows) return;
    for (size_t i = 0; i < count; ++i) {
        if (rows[i].ast) laplace_ast_free(rows[i].ast);
        free(rows[i].row_utf8);
    }
    free(rows);
}

void laplace_grammar_row_iter_free_lines(laplace_raw_row_t* rows, size_t count) {
    if (!rows) return;
    for (size_t i = 0; i < count; ++i)
        free(rows[i].row_utf8);
    free(rows);
}
