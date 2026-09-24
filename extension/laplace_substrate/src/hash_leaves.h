#pragma once

#include "postgres.h"

/* The physical HASH leaves of one partitioned laplace table, indexed by hash
 * remainder. The leaf count is whatever the table's DDL declared; nothing here
 * assumes it. Resolved once per backend and table. */
typedef struct LaplaceHashLeaves
{
    Oid  parent_oid;
    int  count;      /* modulus == number of leaves */
    Oid *leaf_oids;  /* leaf_oids[remainder] */
} LaplaceHashLeaves;

const LaplaceHashLeaves *laplace_hash_leaves(const char *relname, const char *label);

/* Remainder (leaf index) of each key under the table's own partition hash. */
void laplace_hash_leaf_route(const LaplaceHashLeaves *leaves, const Datum *keys,
                             int n, int *remainders, const char *label);
