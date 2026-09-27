#ifndef LAPLACE_CONSENSUS_BULK_WRITE_H
#define LAPLACE_CONSENSUS_BULK_WRITE_H

#include "postgres.h"
#include "executor/spi.h"
#include "utils/builtins.h"

/* Persist the unmatched rows of the nine folded consensus arrays (id, subject,
 * object, games, observed_at, matched, rating, rd, volatility) through binary
 * COPY into laplace.consensus. Returns false when the relation has rules or row
 * security, leaving the write to the caller's SQL INSERT. */
bool laplace_consensus_copy_novel(Datum type, Datum *values, int count,
                                  uint64 *processed);

/* Apply the matched rows of the folded arrays, already locked by the caller:
 * route each to its HASH leaf by subject and update standing and witness count
 * there with one set UPDATE per leaf, with no parent partition routing or
 * ON CONFLICT arbitration. Returns false when a direct leaf UPDATE would bypass
 * parent-table rules, triggers, RLS or ACLs; the caller then writes through its
 * keyed SQL path. */
bool laplace_consensus_update_matched(Datum type, Datum *values, int count,
                                      uint64 expected, uint64 *processed);

#endif
