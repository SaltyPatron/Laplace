





#ifndef LAPLACE_TRAJECTORY_CORPUS_H
#define LAPLACE_TRAJECTORY_CORPUS_H

#include "utils/hsearch.h"      

#define GEN_COMPARE_CAP   64      
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
    int32   *stream;        
    int32    stream_len;
    int32   *suffix;        
    int32    n_suffix;
    char   (*ids)[16];      
    int32    n_vocab;
    int32    vocab_cap;
    HTAB    *vocab;         
    int32   *sep_after;     




    int64    sequences;     
    int64    separators;    
    int64    probe_rows;    
    int64    probe_max_us;  
    int      build_max_rows;
} GenCorpus;


extern int laplace_corpus_max_rows;
extern void laplace_corpus_guc_init(void);


extern GenCorpus *corpus_ensure(void);

extern void corpus_ensure_suffix(GenCorpus *c);

extern int32 corpus_vocab_intern(GenCorpus *c, const char key[16]);

extern int prefix_cmp(const GenCorpus *c, int32 s, const int32 *ctx, int k);

#endif                          
