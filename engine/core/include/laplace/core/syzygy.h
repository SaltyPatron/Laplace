#pragma once

/*
 * Syzygy tablebase probe kernel — the thin Laplace ABI over the vendored
 * Fathom prober (external/fathom, MIT, pinned submodule).
 *
 * A probe is a memory-mapped table lookup, the exact thing an in-process
 * kernel is for ("compute at ingest" — subprocess probing over millions of
 * positions was rejected in the campaign design). The chess lane converts a
 * board to bitboards on the C# side and calls these entry points through
 * NativeInterop, like every other laplace_core kernel.
 *
 * POV and rule-50 law: results are side-to-move POV. Laplace position
 * identity excludes the halfmove clock (PositionContent carries stm/castling/
 * ep/pieces only), so the lawful per-position fact is the rule50-agnostic
 * verdict: every probe here runs with rule50 = 0, and the five-valued WDL
 * (loss / blessed-loss / draw / cursed-win / win) already carries the 50-move
 * boundary as content.
 */

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* WDL values, identical to Fathom's TB_LOSS..TB_WIN ordering. */
#define LAPLACE_SYZYGY_LOSS         0
#define LAPLACE_SYZYGY_BLESSED_LOSS 1
#define LAPLACE_SYZYGY_DRAW         2
#define LAPLACE_SYZYGY_CURSED_WIN   3
#define LAPLACE_SYZYGY_WIN          4

/*
 * Initialize (or re-initialize) the prober against a tablebase directory.
 * Returns the largest man count the discovered table set covers (0 when the
 * directory holds no tables), or -1 when init itself failed. Serialized
 * internally; safe to call again with a new path.
 */
int laplace_syzygy_init(const char* path);

/* Release every table mapping. Idempotent. */
void laplace_syzygy_free(void);

/* Largest man count of the loaded table set (0 when nothing is loaded). */
int laplace_syzygy_largest(void);

/*
 * Probe the WDL table for one position. Bitboards are a1=bit0..h8=bit63.
 * ep is the en-passant square (0 = none); white_to_move is 1/0. The caller
 * must not pass positions with castling rights (not covered by tablebases)
 * or with more men than laplace_syzygy_largest().
 * Returns 0..4 (LAPLACE_SYZYGY_LOSS..WIN, side-to-move POV) or -1 when the
 * probe failed. Thread-safe.
 */
int laplace_syzygy_probe_wdl(
    uint64_t white, uint64_t black, uint64_t kings, uint64_t queens,
    uint64_t rooks, uint64_t bishops, uint64_t knights, uint64_t pawns,
    unsigned ep, int white_to_move);

/*
 * Probe WDL + DTZ through the root probe (requires DTZ tables). Same inputs
 * as the WDL probe. On success returns 0 and writes out_wdl (0..4, STM POV)
 * and out_dtz (plies to the next zeroing move under optimal play, >= 0).
 * Returns -1 when the probe failed or the position is terminal (checkmate /
 * stalemate — a terminal position needs no oracle). Serialized internally
 * (Fathom's root probe is not thread-safe).
 */
int laplace_syzygy_probe_root(
    uint64_t white, uint64_t black, uint64_t kings, uint64_t queens,
    uint64_t rooks, uint64_t bishops, uint64_t knights, uint64_t pawns,
    unsigned ep, int white_to_move, int* out_wdl, int* out_dtz);

#ifdef __cplusplus
}
#endif
