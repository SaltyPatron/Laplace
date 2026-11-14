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

/* The managed board's ABI over the Engine's one implementation of chess's laws (Laplace-Engine src/chess_rules.c,
 * compiled into this library; engine/core/src/chess_rules.c only crosses between the two boards). 0x88 squares and
 * signed P,N,B,R,Q,K = 1..6. Pockets contain P..Q for white then black. Promoted provenance matters when a captured
 * piece returns to a pocket. Castling bits/destinations agree with the managed board ABI. law: the variant's name as
 * the Engine's firmware/chess.firmware declares it (laplace_chess_laws reads it); empty is Standard. */
typedef struct {
    int8_t squares[128];
    uint16_t pockets[10];
    uint64_t promoted;
    int32_t white, castle, ep, halfmove, fullmove;
    int32_t rook_files[4];
    uint32_t checks[2];
    char law[64];
} laplace_chess_state_t;

typedef struct { int32_t from, to, promotion, flags; } laplace_chess_move_t;
enum { LAPLACE_CHESS_DOUBLE = 1, LAPLACE_CHESS_EP = 2,
       LAPLACE_CHESS_CASTLE = 4, LAPLACE_CHESS_PROMOTION = 8,
       LAPLACE_CHESS_DROP = 16 };
/* How a position stands by its law alone: play goes on, White or Black has won, or it is drawn (the Engine's
 * lp_chess_result); -1 when the state names no law the firmware declares. */
enum { LAPLACE_CHESS_ON = 0, LAPLACE_CHESS_WHITE_WINS = 1, LAPLACE_CHESS_BLACK_WINS = 2, LAPLACE_CHESS_DRAWN = 3 };

/* Reads the laws the Engine's chess firmware at path declares, once per process; the count read. */
LAPLACE_CHESS_API int laplace_chess_laws(const char* path);
/* Whether the firmware declares a law of this name (a PGN Variant tag, or Lichess's variant name). */
LAPLACE_CHESS_API int laplace_chess_law_known(const char* name);
/* The FEN a law starts from, as its firmware declares it, written to out (NUL-terminated); its length, 0 when the law
 * starts from Standard's array, or -1 when no law has that name or out is too small. */
LAPLACE_CHESS_API int laplace_chess_law_start(const char* name, char* out, size_t capacity);
/* The law's own name for a name it goes by (Lichess's kingOfTheHill, UCI's kingofthehill: "King of the Hill"), written
 * to out; its length, or -1 when no law has that name or out is too small. */
LAPLACE_CHESS_API int laplace_chess_law_canonical(const char* name, char* out, size_t capacity);
/* The i-th name a law goes by, written to out; its length, or -1 past the last name or when out is too small. */
LAPLACE_CHESS_API int laplace_chess_law_name(int i, char* out, size_t capacity);
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
LAPLACE_CHESS_API int laplace_chess_result(const laplace_chess_state_t* state);
#ifdef __cplusplus
}
#endif
