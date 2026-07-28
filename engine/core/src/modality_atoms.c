#include "laplace/core/modality_atoms.h"

#include "laplace/core/hilbert4d.h"
#include "laplace/core/super_fibonacci.h"

#define LAPLACE_IMAGE_ALPHABET 16777216ull /* 2^24 packed RGB */
#define LAPLACE_AUDIO_ALPHABET 65536ull    /* 2^16 PCM samples */
#define LAPLACE_AUDIO_BIAS     32768ll     /* amplitude order: -32768 -> rank 0 */

uint64_t laplace_modality_alphabet_size(laplace_modality_t modality) {
    switch (modality) {
        case LAPLACE_MODALITY_IMAGE: return LAPLACE_IMAGE_ALPHABET;
        case LAPLACE_MODALITY_AUDIO: return LAPLACE_AUDIO_ALPHABET;
        default: return 0ull;
    }
}

int laplace_modality_atom_rank(laplace_modality_t modality, int64_t atom,
                               uint64_t* out_rank) {
    if (out_rank == NULL) return -1;
    switch (modality) {
        case LAPLACE_MODALITY_IMAGE:
            /* The packed value IS the rank — that is the rock lock, not an
             * implementation shortcut. (R<<16)|(G<<8)|B is already a total
             * order on the alphabet, so no sort and no table exists to drift. */
            if (atom < 0 || (uint64_t)atom >= LAPLACE_IMAGE_ALPHABET) return -1;
            *out_rank = (uint64_t)atom;
            return 0;
        case LAPLACE_MODALITY_AUDIO:
            /* Amplitude order over the signed range, biased into [0, 65536). */
            if (atom < -LAPLACE_AUDIO_BIAS || atom > (LAPLACE_AUDIO_BIAS - 1)) return -1;
            *out_rank = (uint64_t)(atom + LAPLACE_AUDIO_BIAS);
            return 0;
        default:
            return -1;
    }
}

int laplace_modality_atom_from_rank(laplace_modality_t modality, uint64_t rank,
                                    int64_t* out_atom) {
    if (out_atom == NULL) return -1;
    if (rank >= laplace_modality_alphabet_size(modality)) return -1;
    switch (modality) {
        case LAPLACE_MODALITY_IMAGE: *out_atom = (int64_t)rank; return 0;
        case LAPLACE_MODALITY_AUDIO: *out_atom = (int64_t)rank - LAPLACE_AUDIO_BIAS; return 0;
        default: return -1;
    }
}

int laplace_modality_atom_geometry(laplace_modality_t modality, int64_t atom,
                                   uint64_t* out_rank, double out_coord[4],
                                   hilbert128_t* out_hilbert) {
    uint64_t rank = 0ull;
    uint64_t n = laplace_modality_alphabet_size(modality);
    double coord[4];

    if (out_coord == NULL || out_hilbert == NULL || n == 0ull) return -1;
    if (laplace_modality_atom_rank(modality, atom, &rank) != 0) return -1;

    /* Same construction unicode_seed.cpp applies to codepoints: the canonical
     * rank indexes a super-Fibonacci point on S3, and the hilbert index is
     * encoded from that point. Evaluated per atom rather than materialised —
     * identical output, and the image alphabet cannot be materialised. */
    super_fibonacci_point((size_t)n, (size_t)rank, coord);
    hilbert4d_encode(coord, out_hilbert);

    out_coord[0] = coord[0];
    out_coord[1] = coord[1];
    out_coord[2] = coord[2];
    out_coord[3] = coord[3];
    if (out_rank) *out_rank = rank;
    return 0;
}
