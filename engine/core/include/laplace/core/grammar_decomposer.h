#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/grammar_registry.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct TSParser TSParser;







#define LAPLACE_AST_ROOT UINT32_MAX

typedef struct {
    uint32_t type_id;     
    uint32_t start_byte;  
    uint32_t end_byte;
    uint32_t parent;      
    uint8_t  is_error;    
    uint8_t  _pad[3];
} laplace_ast_node_t;

typedef struct laplace_ast laplace_ast_t;





int laplace_grammar_parse(const uint8_t* utf8, size_t len,
                          const TSLanguage* recipe, laplace_ast_t** out_ast);

int laplace_grammar_parse_with(TSParser* parser, const uint8_t* utf8, size_t len,
                               const TSLanguage* recipe, laplace_ast_t** out_ast);

size_t laplace_ast_node_count(const laplace_ast_t* ast);
int    laplace_ast_get_node(const laplace_ast_t* ast, size_t idx, laplace_ast_node_t* out);
const char* laplace_ast_type_name(const laplace_ast_t* ast, uint32_t type_id);
void   laplace_ast_free(laplace_ast_t* ast);


typedef struct laplace_grammar_row_iter laplace_grammar_row_iter_t;

int laplace_grammar_row_iter_new(const TSLanguage* recipe,
                                 laplace_grammar_row_iter_t** out);

/** When set, one physical newline is one record (ignores grammar row quoting). */
void laplace_grammar_row_iter_set_line_framed(laplace_grammar_row_iter_t* it, int on);

typedef struct {
    laplace_ast_t* ast;
    uint8_t*       row_utf8;
    size_t         row_len;
} laplace_parsed_row_t;

typedef struct {
    uint8_t* row_utf8;
    size_t   row_len;
} laplace_raw_row_t;

int laplace_grammar_row_iter_feed(laplace_grammar_row_iter_t* it,
                                  const uint8_t* chunk, size_t len,
                                  laplace_parsed_row_t** out_rows, size_t* out_count);

int laplace_grammar_row_iter_feed_lines(laplace_grammar_row_iter_t* it,
                                        const uint8_t* chunk, size_t len,
                                        laplace_raw_row_t** out_rows, size_t* out_count);

int laplace_grammar_row_iter_parse_row(laplace_grammar_row_iter_t* it,
                                       const uint8_t* row_utf8, size_t row_len,
                                       laplace_ast_t** out_ast);

void laplace_grammar_row_iter_free_lines(laplace_raw_row_t* rows, size_t count);

void laplace_grammar_row_iter_free(laplace_grammar_row_iter_t* it);
void laplace_grammar_row_iter_free_rows(laplace_parsed_row_t* rows, size_t count);

#ifdef __cplusplus
}
#endif
