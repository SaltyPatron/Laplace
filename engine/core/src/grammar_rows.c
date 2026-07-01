#include "laplace/core/grammar_decomposer.h"

#include <stdlib.h>
#include <string.h>
#include <stdbool.h>

#include "tree_sitter/api.h"

#define LAPLACE_GRAMMAR_ROW_MAX (65536u)

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
    *out = it;
    return 0;
}

void laplace_grammar_row_iter_set_line_framed(laplace_grammar_row_iter_t* it, int on) {
    if (it) it->force_line_framed = on ? 1 : 0;
}

static int use_grammar_row_framing(const laplace_grammar_row_iter_t* it) {
    return it->row_structured && !it->force_line_framed;
}

static int append_carry(laplace_grammar_row_iter_t* it,
                        const uint8_t* chunk, size_t len) {
    if (it->carry_len + len > it->carry_cap) {
        size_t ncap = it->carry_cap ? it->carry_cap * 2 : 65536;
        while (ncap < it->carry_len + len) ncap *= 2;
        uint8_t* n = (uint8_t*)realloc(it->carry, ncap);
        if (!n) { it->oom = 1; return -3; }
        it->carry = n;
        it->carry_cap = ncap;
    }
    memcpy(it->carry + it->carry_len, chunk, len);
    it->carry_len += len;
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

static int row_span_too_large(uint32_t s, uint32_t e) {
    return e > s && (e - s) > LAPLACE_GRAMMAR_ROW_MAX;
}

static int split_carry_records(laplace_grammar_row_iter_t* it, int finalize,
                               laplace_raw_row_t** out_rows, size_t* out_count) {
    *out_rows = NULL;
    *out_count = 0;
    if (it->carry_len == 0) return 0;

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

    for (size_t i = 0; i < rn; ++i) {
        if (row_span_too_large(rr[i].s, rr[i].e)) {
            free(rr);
            ts_tree_delete(tree);
            return split_carry_lines(it, finalize, out_rows, out_count);
        }
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
    if (use_grammar_row_framing(it) && it->carry_len > LAPLACE_GRAMMAR_ROW_MAX) {
        return split_carry_lines(it, finalize, out_rows, out_count);
    }
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
    int rc = use_grammar_row_framing(it)
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
