#pragma once
#include <stddef.h>
#include <stdint.h>

#ifdef _WIN32
#define LAPLACE_CHESS_API __declspec(dllexport)
#else
#define LAPLACE_CHESS_API
#endif
#ifdef __cplusplus
extern "C" {
#endif

/* 0x88 squares and signed P,N,B,R,Q,K = 1..6. Pockets contain P..Q for
 * white then black. Promoted provenance matters when a captured piece returns
 * to a pocket. Castling bits/destinations agree with the managed board ABI. */
typedef struct {
    int8_t squares[128];
    uint16_t pockets[10];
    uint64_t promoted;
    int32_t white, castle, ep, halfmove, fullmove;
    int32_t rook_files[4];
    int32_t drops;
    uint32_t checks[2], check_target;
    uint64_t winning_squares;
} laplace_chess_state_t;

typedef struct { int32_t from, to, promotion, flags; } laplace_chess_move_t;
enum { LAPLACE_CHESS_DOUBLE = 1, LAPLACE_CHESS_EP = 2,
       LAPLACE_CHESS_CASTLE = 4, LAPLACE_CHESS_PROMOTION = 8,
       LAPLACE_CHESS_DROP = 16 };

/* Returns required output length. No partial output is reported as complete.
 * With legal=0, emits pseudo moves; castling still checks its transit squares. */
LAPLACE_CHESS_API size_t laplace_chess_moves(const laplace_chess_state_t* state,
    int legal, laplace_chess_move_t* out, size_t capacity);
/* Apply a generated move, or reject without changing the state. */
LAPLACE_CHESS_API int laplace_chess_apply(laplace_chess_state_t* state,
    const laplace_chess_move_t* move);
LAPLACE_CHESS_API int laplace_chess_resolve(const laplace_chess_state_t* state,
    const char* san, laplace_chess_move_t* out);
/* One native call resolves the whole game. states has count+1 entries; moves
 * has count entries. SAN strings are a UTF-8 NUL-delimited buffer. On failure,
 * failed_ply names the first rejected move and no recording is implied. */
LAPLACE_CHESS_API int laplace_chess_replay(const laplace_chess_state_t* initial,
    const char* sans, size_t bytes, size_t count, laplace_chess_state_t* states,
    laplace_chess_move_t* moves, size_t* failed_ply);
#ifdef __cplusplus
}
#endif
