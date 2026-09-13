#ifndef LAPLACE_CONSENSUS_BULK_WRITE_H
#define LAPLACE_CONSENSUS_BULK_WRITE_H

#include "postgres.h"

/* Consume the canonical nine folded arrays. Returns false when the relation
 * requires SQL rewriting or row policies; the caller retains its INSERT path. */
bool laplace_consensus_copy_novel(Datum type, Datum *values, int count,
                                  uint64 *processed);

#endif
