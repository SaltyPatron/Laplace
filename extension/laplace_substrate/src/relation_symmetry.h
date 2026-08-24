/*
 * relation_symmetry.h — the symmetric relation type ids, in ONE place.
 *
 * WHY ANY READ NEEDS THIS. laplace_attestation_orient() canonicalises a
 * symmetric assertion to subject = min(subject, object) by hash bytes, so the
 * unordered pair {a,b} folds into exactly ONE consensus cell rather than two
 * half-rated ones. That is correct on the write side -- all evidence for the
 * pair accumulates in one place.
 *
 * The cost is paid on the read side: `c.subject_id = <node>` alone can only
 * traverse such a pair from its lesser-hashed end. Measured on the live
 * substrate 2026-08-24, 133,019 of 133,019 symmetric cells are stored
 * subject < object and 0 the other way, so the reverse direction of every
 * symmetric edge was unreachable to any subject-only probe.
 *
 * A read that must see both ends unions a reverse arm restricted to THESE type
 * ids. The restriction is the point: traversing an asymmetric edge backwards
 * would assert a different claim (IS_A up is not IS_A down).
 *
 * Header-inline, like walk_score.h, and for the same reason: two callers needed
 * it, and a copy in each is the divergence the implementation law names. Each
 * translation unit keeps its own cached array -- the law is a link-time
 * constant, so the cache is built once per backend per TU and never invalidated.
 */
#ifndef LAPLACE_RELATION_SYMMETRY_H
#define LAPLACE_RELATION_SYMMETRY_H

#include "utils/array.h"
#include "utils/memutils.h"
#include "catalog/pg_type.h"

#include "laplace/core/relation_law.h"
#include "spi_common.h"

static ArrayType *laplace_symmetric_types_cache = NULL;

static inline ArrayType *
laplace_symmetric_relation_types(void)
{
    if (laplace_symmetric_types_cache == NULL)
    {
        MemoryContext old = MemoryContextSwitchTo(TopMemoryContext);
        size_t        cap = laplace_relation_table_count > 0
                            ? laplace_relation_table_count : 1;
        Datum        *ids = (Datum *) palloc(sizeof(Datum) * cap);
        int           n = 0;

        /*
         * Resolve through laplace_relation_type_id(), NOT the def's type_id
         * field. The generated table stores ids in a separate lazily-populated
         * cache (k_relation_type_id_cache, filled by relation_ids_ensure());
         * the struct member is left zero. Reading it yields an array of
         * zero-hashes that matches nothing, and the reverse arm silently never
         * fires -- measured exactly that way before this was corrected.
         */
        for (size_t i = 0; i < laplace_relation_table_count; i++)
        {
            hash128_t tid;

            if (laplace_relation_table[i].symmetry != LAPLACE_REL_SYMMETRY_SYMMETRIC)
                continue;
            if (laplace_relation_type_id(laplace_relation_table[i].canonical, &tid) < 0)
                continue;
            ids[n++] = hash128_to_datum(&tid);
        }

        laplace_symmetric_types_cache =
            construct_array(ids, n, BYTEAOID, -1, false, TYPALIGN_INT);
        MemoryContextSwitchTo(old);
    }
    return laplace_symmetric_types_cache;
}

#endif /* LAPLACE_RELATION_SYMMETRY_H */
