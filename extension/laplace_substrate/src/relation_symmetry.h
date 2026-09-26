/*
 * relation_symmetry.h — the symmetric relation type ids from the manifest
 * relation registry.
 *
 * laplace_attestation_orient() stores a symmetric assertion with
 * subject = min(subject, object) by hash bytes, so the unordered pair folds
 * into one consensus cell. A subject-keyed read therefore reaches that cell
 * only from its lesser end; a read that must traverse both ends adds a reverse
 * arm restricted to these type ids. Reversing an asymmetric edge would read a
 * different claim, so the restriction is required.
 *
 * Each translation unit caches its own array in TopMemoryContext; the
 * registry is a link-time constant, so the cache is never invalidated.
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
         * Resolve through laplace_relation_type_id(), not the def's type_id
         * field: the generated table leaves that member zero and keeps ids in
         * a lazily populated cache. A zero id matches no cell.
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

/* Narrows a caller's relation-type set to its symmetric members so the reverse
 * arm is bounded before partitions and indexes are scanned; filtering in a row
 * callback would read every asymmetric reverse neighborhood first. NULL means
 * all symmetric types. */
static inline ArrayType *
laplace_symmetric_relation_types_in(ArrayType *types)
{
    Datum *ids;
    bool *nulls;
    int count, kept = 0;
    ArrayType *result;
    if (types == NULL) return laplace_symmetric_relation_types();
    deconstruct_array(types, BYTEAOID, -1, false, TYPALIGN_INT,
                      &ids, &nulls, &count);
    for (int i = 0; i < count; ++i)
    {
        const laplace_relation_def_t *def = NULL;
        hash128_t id;
        bytea *value;
        if (nulls[i]) continue;
        value = DatumGetByteaPP(ids[i]);
        if (VARSIZE_ANY_EXHDR(value) != sizeof(id))
            ereport(ERROR, (errmsg("relation types require 16-byte identities")));
        memcpy(&id, VARDATA_ANY(value), sizeof(id));
        if (laplace_relation_lookup(&id, &def) == 0 && def != NULL &&
            def->symmetry == LAPLACE_REL_SYMMETRY_SYMMETRIC)
            ids[kept++] = ids[i];
    }
    result = construct_array(ids, kept, BYTEAOID, -1, false, TYPALIGN_INT);
    pfree(ids);
    pfree(nulls);
    return result;
}

#endif /* LAPLACE_RELATION_SYMMETRY_H */
