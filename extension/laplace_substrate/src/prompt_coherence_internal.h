#ifndef LAPLACE_SUBSTRATE_PROMPT_COHERENCE_INTERNAL_H
#define LAPLACE_SUBSTRATE_PROMPT_COHERENCE_INTERNAL_H

#include "postgres.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "funcapi.h"
#include "miscadmin.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/hsearch.h"
#include "utils/memutils.h"
#include "utils/numeric.h"

#include "laplace/core/content_witness_batch.h"
#include "laplace/core/hash128.h"
#include "laplace/core/relation_law.h"
#include "spi_common.h"
#include "relation_symmetry.h"
#include "spi_nested.h"
#include "spi_stream.h"
#include "laplace/core/sql_catalog.h"

typedef struct PcCand
{
    int32   ord;
    uint8   tok[16];
    uint8   syn[16];
    double  denote_mu;
    int64   witnesses;         /* witnesses behind this sense, from bubble_up_batch */
    int32   lang_agree;        /* +1 agrees with the token's language, 0 unknown,
                                * -1 disagrees. Tri-state on purpose: an
                                * unattested language is NOT a mismatch. */
    double  coherence;
    double  total_mass;        /* ALL rated mass on this candidate, peers or not */
    int64   peer_count;
    double  rel_mass;
    uint8   rel_type[16];
    double  rel_type_rank;     /* rank of the recorded rel_type, for "best" */
    bool    has_rel_type;
} PcCand;

/* syn -> the candidate rows carrying it (a surface/sense pair can repeat across
 * tokens). */
typedef struct PcSynEntry
{
    uint8   key[16];
    int    *idx;
    int     n_idx;
    int     cap_idx;
} PcSynEntry;

typedef struct PcTokEntry
{
    uint8   key[16];
    int32  *ords;
    int     n_ords;
    int     cap_ords;
} PcTokEntry;

typedef struct PcTypeEntry
{
    uint8   key[16];
    int32  *namer_ords;        /* ords whose token names this relation type */
    int     n_namers;
    int     cap_namers;
    bool    named;
} PcTypeEntry;

typedef struct PcPeerKey
{
    int32 cand_idx;
    int32 peer_ord;
} PcPeerKey;

typedef struct PcPeerEntry
{
    PcPeerKey key;
} PcPeerEntry;

typedef struct PcOrdEntry
{
    int32 key;
} PcOrdEntry;

typedef struct
{
    HTAB *syn_h, *type_h, *peer_h;
    PcCand *cands;
} PcEdgeRead;

typedef struct
{
    HTAB *syn_h, *tok_h;
    PcCand *cands;
    int count, capacity;
    MemoryContext work;
} PcCandidateRead;

void pc_int32_add(int32 **values, int *n, int *cap, int32 value,
                  MemoryContext cxt, const char *what);
void pc_type_add_namers(PcTypeEntry *te, const PcTokEntry *tk, HTAB *namer_h,
                        MemoryContext cxt);
bool pc_type_has_other_namer(const PcTypeEntry *te, int32 ord);
void pc_syn_add(HTAB *h, const uint8 *syn, int idx, MemoryContext cxt);
SPIPlanPtr ensure_pc_plan(SPIPlanPtr *slot, const char *sql, int nargs,
                          Oid *argtypes, const char *label);
void pc_scan_edges(HTAB *syn_h, HTAB *type_h, HTAB *peer_h, PcCand *cands,
                   ArrayType *syn_arr, bool forward);
void pc_receive_candidate(TupleTableSlot *row, void *opaque);
void pc_receive_relation_mass(TupleTableSlot *row, void *opaque);

#endif
