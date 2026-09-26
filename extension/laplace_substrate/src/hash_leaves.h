#pragma once

#include "postgres.h"

/* The physical HASH leaves of one partitioned laplace table, indexed by hash
 * remainder. The leaf count is whatever the table's DDL declared; nothing here
 * assumes it. Each table keeps one entry per backend at a fixed address; a leaf
 * attached or detached invalidates the parent's relcache in every backend, and the
 * entry is resolved again, in place, the next time it is asked for. */
typedef struct LaplaceHashLeaves
{
    Oid  parent_oid;
    int  count;      /* modulus == number of leaves */
    Oid *leaf_oids;  /* leaf_oids[remainder] */
    bool stale;
} LaplaceHashLeaves;

const LaplaceHashLeaves *laplace_hash_leaves(const char *relname, const char *label);

/* Remainder (leaf index) of each key under the table's own partition hash. */
void laplace_hash_leaf_route(const LaplaceHashLeaves *leaves, const Datum *keys,
                             int n, int *remainders, const char *label);
