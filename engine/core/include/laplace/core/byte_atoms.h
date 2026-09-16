#pragma once

#include "laplace/core/hash128.h"
#include "laplace/core/hilbert4d.h"

#ifdef __cplusplus
extern "C" {
#endif

enum { LAPLACE_BYTE_ATOM_FIRST = 128, LAPLACE_BYTE_ATOM_COUNT = 128,
       LAPLACE_BYTE_ATOM_INDEX_SIZE = 256 };

typedef struct {
    hash128_t id;
    double coord[4];
    hilbert128_t hilbert;
} laplace_byte_atom_t;

/* The existing raw-byte basis: Blake3([byte]), SuperFibonacci(128), Hilbert.
 * ASCII belongs to the Unicode floor and is deliberately outside this owner.
 * No allocation, global mutable state or persisted-entity claim. */
typedef struct {
    laplace_byte_atom_t atoms[LAPLACE_BYTE_ATOM_COUNT];
    uint16_t index[LAPLACE_BYTE_ATOM_INDEX_SIZE];
    hash128_t receipt;
} laplace_byte_atoms_t;

int laplace_byte_atoms_build(laplace_byte_atoms_t* out);
int laplace_byte_atoms_validate(const laplace_byte_atoms_t* basis);
/* 1 = found, 0 = absent, -1 = invalid arguments/index. */
int laplace_byte_atoms_lookup(const laplace_byte_atoms_t* basis,
    const hash128_t* id, const laplace_byte_atom_t** out);
/* One managed/native crossing for the complete existing basis. */
int laplace_byte_atoms_copy(hash128_t* ids, double* coordinates,
    hilbert128_t* hilberts, size_t capacity);

#ifdef __cplusplus
}
#endif
