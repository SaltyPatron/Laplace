#ifndef LAPLACE_SUBSTRATE_CONTENT_RESOLVE_INTERNAL_H
#define LAPLACE_SUBSTRATE_CONTENT_RESOLVE_INTERNAL_H

#include "postgres.h"
#include "funcapi.h"
#include "laplace/core/hash128.h"
#include "laplace/core/tier_tree.h"

typedef struct
{
    ReturnSetInfo *rsinfo;
} word_seg_ctx;

typedef struct
{
    uint32_t *off;   /* offsets into the tree's normalized text */
    uint32_t *len;
    int       n;
    int       cap;
} phrase_ctx;

void word_seg_emit(void *ctx_, uint32_t ordinal,
                   const uint8_t *word_utf8, uint32_t word_len,
                   const hash128_t *id);

int phrase_collect_from_tree(const tier_tree_t *tree,
                             const uint8_t *norm, size_t norm_len,
                             phrase_ctx *ctx);

#endif
