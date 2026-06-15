/*
 * trajectory_corpus.h — the per-backend generation index (the trajectory stream +
 * suffix array built from physicalities.trajectory) and its accessors, shared by
 * trajectory_generate.c (the continuation/co-occurrence walk). Split out of
 * trajectory_walk.c. Include after postgres.h.
 */
#ifndef LAPLACE_TRAJECTORY_CORPUS_H
#define LAPLACE_TRAJECTORY_CORPUS_H

#include "utils/hsearch.h"      /* HTAB */

#define GEN_COMPARE_CAP   64      /* suffix order depth; >= max usable context */
#define GEN_MAX_ORDER     16
#define GEN_MAX_STEPS     2048
#define GEN_SENTINEL      (-1)

typedef struct VocabEntry
{
    char  key[16];
    int32 id;
} VocabEntry;

typedef struct GenCorpus
{
    MemoryContext cxt;
    int32   *stream;        /* token ids with GEN_SENTINEL between sequences */
    int32    stream_len;
    int32   *suffix;        /* sorted suffix start positions (token positions only) */
    int32    n_suffix;
    char   (*ids)[16];      /* vocab id -> 16-byte entity id */
    int32    n_vocab;
    int32    vocab_cap;
    HTAB    *vocab;         /* 16-byte entity id -> vocab id */
    int32   *sep_after;     /* parallel to stream: the witnessed separator entity (vocab id)
                            * that followed the word at this stream position, or -1. Lets
                            * generation replay the EXACT witnessed spacing omniglottally
                            * (Latin space / 。、/ U+3000 / CJK no-space) with zero detok
                            * rules — the separator is content the substrate recorded. */
    int64    sequences;     /* witnessed roots walked                  */
    int64    separators;    /* whitespace tokens excluded from order   */
    int64    probe_rows;    /* invalidation: trajectory physicalities  */
    int64    probe_max_us;  /* invalidation: max(observed_at) epoch µs */
    int      build_max_rows;/* invalidation: the corpus_max_rows cap at build */
} GenCorpus;

/* laplace_substrate.corpus_max_rows GUC (0 = unbounded) + its registration. */
extern int laplace_corpus_max_rows;
extern void laplace_corpus_guc_init(void);

/* Build-or-reuse the per-backend corpus (invalidated when the trajectory set changes). */
extern GenCorpus *corpus_ensure(void);
/* Ensure the suffix array is built over the corpus stream. */
extern void corpus_ensure_suffix(GenCorpus *c);
/* Intern a 16-byte entity id into the corpus vocab, returning its token id. */
extern int32 corpus_vocab_intern(GenCorpus *c, const char key[16]);
/* Compare the suffix at stream position s against a k-token context. */
extern int prefix_cmp(const GenCorpus *c, int32 s, const int32 *ctx, int k);

#endif                          /* LAPLACE_TRAJECTORY_CORPUS_H */
