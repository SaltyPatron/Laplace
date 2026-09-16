#include "laplace/core/byte_atoms.h"

#include <string.h>
#include "laplace/core/super_fibonacci.h"

static size_t atom_slot(const hash128_t* id) {
    return (size_t)(id->lo ^ id->hi) & (LAPLACE_BYTE_ATOM_INDEX_SIZE - 1u);
}

int laplace_byte_atoms_build(laplace_byte_atoms_t* out) {
    /* The receipt serializes exact fields, never C struct padding. */
    uint8_t bytes[LAPLACE_BYTE_ATOM_COUNT * 64u];
    if (out == NULL) return -1;
    memset(out, 0, sizeof(*out));
    for (size_t i = 0; i < LAPLACE_BYTE_ATOM_COUNT; ++i) {
        const uint8_t value = (uint8_t)(LAPLACE_BYTE_ATOM_FIRST + i);
        laplace_byte_atom_t* atom = &out->atoms[i];
        hash128_blake3(&value, 1u, &atom->id);
        super_fibonacci_point(LAPLACE_BYTE_ATOM_COUNT, i, atom->coord);
        hilbert4d_encode(atom->coord, &atom->hilbert);
        size_t slot = atom_slot(&atom->id);
        while (out->index[slot] != 0u) slot = (slot + 1u) & (LAPLACE_BYTE_ATOM_INDEX_SIZE - 1u);
        out->index[slot] = (uint16_t)(i + 1u);
        memcpy(bytes + i * 64u, &atom->id, 16u);
        for (size_t axis = 0; axis < 4u; ++axis) {
            uint64_t word;
            memcpy(&word, &atom->coord[axis], sizeof(word));
            for (size_t byte = 0; byte < 8u; ++byte)
                bytes[i * 64u + 16u + axis * 8u + byte] = (uint8_t)(word >> (byte * 8u));
        }
        memcpy(bytes + i * 64u + 48u, atom->hilbert.bytes, 16u);
    }
    hash128_blake3(bytes, sizeof(bytes), &out->receipt);
    return 0;
}

int laplace_byte_atoms_validate(const laplace_byte_atoms_t* basis) {
    laplace_byte_atoms_t expected;
    if (basis == NULL || laplace_byte_atoms_build(&expected) != 0) return -1;
    return memcmp(basis, &expected, sizeof(expected)) == 0 ? 0 : -1;
}

int laplace_byte_atoms_lookup(const laplace_byte_atoms_t* basis,
    const hash128_t* id, const laplace_byte_atom_t** out) {
    if (out == NULL) return -1;
    *out = NULL;
    if (basis == NULL || id == NULL) return -1;
    size_t slot = atom_slot(id);
    for (size_t probe = 0; probe < LAPLACE_BYTE_ATOM_INDEX_SIZE; ++probe) {
        const uint16_t entry = basis->index[slot];
        if (entry == 0u) return 0;
        if (entry > LAPLACE_BYTE_ATOM_COUNT) return -1;
        const laplace_byte_atom_t* atom = &basis->atoms[entry - 1u];
        if (hash128_equals(id, &atom->id)) { *out = atom; return 1; }
        slot = (slot + 1u) & (LAPLACE_BYTE_ATOM_INDEX_SIZE - 1u);
    }
    return -1;
}

int laplace_byte_atoms_copy(hash128_t* ids, double* coordinates,
    hilbert128_t* hilberts, size_t capacity) {
    laplace_byte_atoms_t basis;
    if (ids == NULL || coordinates == NULL || hilberts == NULL ||
        capacity < LAPLACE_BYTE_ATOM_COUNT || laplace_byte_atoms_build(&basis) != 0) return -1;
    for (size_t i = 0; i < LAPLACE_BYTE_ATOM_COUNT; ++i) {
        ids[i] = basis.atoms[i].id;
        memcpy(coordinates + i * 4u, basis.atoms[i].coord, 4u * sizeof(double));
        hilberts[i] = basis.atoms[i].hilbert;
    }
    return 0;
}
