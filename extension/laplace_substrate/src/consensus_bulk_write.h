#ifndef LAPLACE_CONSENSUS_BULK_WRITE_H
#define LAPLACE_CONSENSUS_BULK_WRITE_H

#include "postgres.h"

/* Consume the canonical nine folded arrays. Returns false when the relation
 * requires SQL rewriting or row policies; the caller retains its INSERT path. */
bool laplace_consensus_copy_novel(Datum type, Datum *values, int count,
                                  uint64 *processed);

/* Phase-1 already locked every matched row. Re-route those same folded rows to
 * their exact HASH leaves and update them there, avoiding a second parent
 * partition route and INSERT/ON CONFLICT arbitration. Returns false whenever
 * direct leaf UPDATE would change PostgreSQL's parent-table policy/permission/
 * trigger behavior; the caller then retains the existing keyed SQL path. */
bool laplace_consensus_update_matched(Datum type, Datum *values, int count,
                                      uint64 expected, uint64 *processed);

#endif
