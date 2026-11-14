/* The managed board's chess ABI (laplace/core/chess_rules.h) over the Engine's one implementation of chess's laws
 * (Laplace-Engine src/chess_rules.c, compiled into this library from the canonical Engine checkout). Nothing here is a
 * rule: a 0x88 state crosses into the Engine's board, the Engine moves, judges and reads SAN, and the result crosses
 * back. The clocks (halfmove, fullmove) are the managed board's and are kept here as it keeps them. */
#include "laplace/core/chess_rules.h"
#include "chess_rules.h"
#include <string.h>

static int s64(int s){ return (s >> 4) * 8 + (s & 7); }
static int s88(int s){ return (s >> 3) * 16 + (s & 7); }
static size_t law_length(const laplace_chess_state_t *s){ size_t n = 0; while (n < sizeof s->law && s->law[n]) n++; return n; }

/* The Engine's board of a state; 0 when the state names no law the firmware declares or is no board. */
static int board_of(const laplace_chess_state_t *s, Bd *b){
    memset(b, 0, sizeof *b); size_t n = law_length(s);
    const LpChessLaw *law = n ? lp_chess_law_named(s->law, n) : NULL; if (n && !law) return 0;
    if (s->white != 0 && s->white != 1) return 0;
    b->law = law; b->c960 = (uint8_t)(law && law->c960); b->side = (uint8_t)!s->white;
    for (int i = 0; i < 128; i++) if (!(i & 0x88)) b->sq[s64(i)] = s->squares[i];
    for (int c = 0; c < 2; c++) for (int kind = PAWN; kind <= QUEEN; kind++) { unsigned v = s->pockets[(c ? 5 : 0) + kind - 1]; b->hand[c][kind] = (uint8_t)(v > 255 ? 255 : v); }
    b->promoted = s->promoted; b->ep = (int8_t)(s->ep >= 0 && s->ep < 128 && !(s->ep & 0x88) ? s64(s->ep) : -1);
    for (int c = 0; c < 2; c++) b->checks[c] = (uint8_t)(s->checks[c] > 255 ? 255 : s->checks[c]);
    for (int i = 0; i < 4; i++) if ((s->castle & (1 << i)) && s->rook_files[i] >= 0 && s->rook_files[i] < 8) b->rights[i >= 2] |= (uint8_t)(1u << s->rook_files[i]);
    return 1;
}
/* A state's board, side, hands, promoted squares, en passant square, checks and castling from the Engine's board; its
 * law, clocks and unused rook files as they were. */
static void state_from(const Bd *b, laplace_chess_state_t *s){
    for (int i = 0; i < 128; i++) s->squares[i] = (i & 0x88) ? 0 : b->sq[s64(i)];
    for (int c = 0; c < 2; c++) for (int kind = PAWN; kind <= QUEEN; kind++) s->pockets[(c ? 5 : 0) + kind - 1] = b->hand[c][kind];
    s->promoted = b->promoted; s->ep = b->ep >= 0 ? s88(b->ep) : -1; s->white = !b->side;
    s->checks[0] = b->checks[0]; s->checks[1] = b->checks[1]; s->castle = 0;
    for (int c = 0; c < 2; c++) { int k = king_sq(b, c); if (k < 0) continue;
        for (int f = 0; f < 8; f++) if (b->rights[c] & (1u << f)) { int bit = (c ? 2 : 0) + (f > (k & 7) ? 0 : 1); s->castle |= 1 << bit; s->rook_files[bit] = f; } }
}
/* A move as the managed board writes it: a castling is the king onto its destination (g or c), a drop comes from 128
 * with the kind dropped as its promotion. */
static laplace_chess_move_t move_of(const Bd *b, const Mv *m){
    laplace_chess_move_t x = { 0, 0, 0, 0 };
    if (m->drop) { x.from = 128; x.to = s88(m->to); x.promotion = m->drop; x.flags = LAPLACE_CHESS_DROP; return x; }
    x.from = s88(m->from);
    if (m->castle) { int back = m->from & ~7, kingside = (m->to & 7) > (m->from & 7); x.to = s88(back + (kingside ? 6 : 2)); x.flags = LAPLACE_CHESS_CASTLE; return x; }
    x.to = s88(m->to);
    if (m->ep) x.flags |= LAPLACE_CHESS_EP;
    if (m->promo) { x.flags |= LAPLACE_CHESS_PROMOTION; x.promotion = m->promo; }
    if (abs(b->sq[m->from]) == PAWN && abs((m->to >> 3) - (m->from >> 3)) == 2) x.flags |= LAPLACE_CHESS_DOUBLE;
    return x;
}
static int same_move(const laplace_chess_move_t *a, const laplace_chess_move_t *b){ return a->from == b->from && a->to == b->to && a->promotion == b->promotion && a->flags == b->flags; }
/* The move made on the state: the Engine's board after it, and the managed clocks. */
static void played(laplace_chess_state_t *s, const Bd *before, const Mv *m){
    Bd after = *before; lp_chess_make(&after, m);
    int zeroing = !m->drop && !m->castle && (before->sq[m->to] || m->ep || abs(before->sq[m->from]) == PAWN);
    state_from(&after, s); s->halfmove = zeroing ? 0 : s->halfmove + 1; if (before->side) s->fullmove++;
}

LAPLACE_CHESS_API int laplace_chess_laws(const char *path){ return lp_chess_laws_load(path); }
LAPLACE_CHESS_API int laplace_chess_law_known(const char *name){ return name && *name && lp_chess_law_named(name, strlen(name)) != NULL; }
LAPLACE_CHESS_API int laplace_chess_law_start(const char *name, char *out, size_t capacity){
    const LpChessLaw *law = name && *name ? lp_chess_law_named(name, strlen(name)) : NULL; if (!law || !out) return -1;
    size_t n = strlen(law->start); if (n + 1 > capacity) return -1;
    memcpy(out, law->start, n + 1); return (int)n;
}
static int written(const char *text, char *out, size_t capacity){
    size_t n = strlen(text); if (!out || n + 1 > capacity) return -1;
    memcpy(out, text, n + 1); return (int)n;
}
LAPLACE_CHESS_API int laplace_chess_law_canonical(const char *name, char *out, size_t capacity){
    const LpChessLaw *law = name && *name ? lp_chess_law_named(name, strlen(name)) : NULL;
    return law ? written(law->name, out, capacity) : -1;
}
LAPLACE_CHESS_API int laplace_chess_law_name(int i, char *out, size_t capacity){
    const char *name = lp_chess_law_name(i); return name ? written(name, out, capacity) : -1;
}
LAPLACE_CHESS_API size_t laplace_chess_moves(const laplace_chess_state_t *s, int legal, laplace_chess_move_t *out, size_t capacity){
    Bd b; if (!s || !board_of(s, &b)) return 0;
    Mv all[LP_CHESS_MOVES]; int n = legal ? lp_chess_gen(&b, all) : lp_chess_pseudo(&b, all);
    for (int i = 0; i < n && out && (size_t)i < capacity; i++) out[i] = move_of(&b, &all[i]);
    return (size_t)n;
}
LAPLACE_CHESS_API int laplace_chess_apply(laplace_chess_state_t *s, const laplace_chess_move_t *m){
    Bd b; if (!s || !m || !board_of(s, &b)) return -1;
    Mv all[LP_CHESS_MOVES]; int n = lp_chess_gen(&b, all);
    for (int i = 0; i < n; i++) { laplace_chess_move_t x = move_of(&b, &all[i]); if (same_move(&x, m)) { played(s, &b, &all[i]); return 0; } }
    return -1;
}
/* The legal move a SAN token names, in the Engine's board's terms. */
static int resolved(const Bd *b, const char *san, Mv *out){
    Mv all[LP_CHESS_MOVES]; int n = lp_chess_gen(b, all), k = lp_chess_san_match(b, san, strlen(san), all, n);
    if (k < 0) return 0; *out = all[k]; return 1;
}
LAPLACE_CHESS_API int laplace_chess_resolve(const laplace_chess_state_t *s, const char *san, laplace_chess_move_t *out){
    Bd b; Mv m; if (!s || !san || !out || !board_of(s, &b) || !resolved(&b, san, &m)) return -1;
    *out = move_of(&b, &m); return 0;
}
LAPLACE_CHESS_API int laplace_chess_replay(const laplace_chess_state_t *initial, const char *sans, size_t bytes, size_t count,
    laplace_chess_state_t *states, laplace_chess_move_t *moves, size_t *failed_ply){
    if (!initial || !sans || !states || (!moves && count) || !failed_ply) return -1;
    states[0] = *initial; size_t offset = 0;
    for (size_t i = 0; i < count; i++) {
        *failed_ply = i; if (offset >= bytes) return -1;
        const char *end = memchr(sans + offset, 0, bytes - offset); Bd b; Mv m;
        if (!end || !board_of(&states[i], &b) || !resolved(&b, sans + offset, &m)) return -1;
        moves[i] = move_of(&b, &m); states[i + 1] = states[i]; played(&states[i + 1], &b, &m);
        offset = (size_t)(end - sans) + 1;
    }
    *failed_ply = count;
    return offset == bytes ? 0 : -1;
}
LAPLACE_CHESS_API int laplace_chess_result(const laplace_chess_state_t *s){
    Bd b; if (!s || !board_of(s, &b)) return -1;
    return lp_chess_result(&b);
}
