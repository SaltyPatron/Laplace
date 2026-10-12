#include "laplace/core/chess_rules.h"
#include <stdlib.h>
#include <string.h>

static int on(int sq) { return sq >= 0 && sq < 128 && !(sq & 0x88); }
static uint64_t bit(int sq) { return UINT64_C(1) << ((sq >> 4) * 8 + (sq & 7)); }
static int king(const laplace_chess_state_t* s, int white) {
    int wanted = white ? 6 : -6;
    for (int sq = 0; sq < 128; ++sq) if (on(sq) && s->squares[sq] == wanted) return sq;
    return -1;
}
static const int knight_steps[] = {33,31,18,14,-33,-31,-18,-14};
static const int ray_steps[] = {16,-16,1,-1,17,15,-17,-15};
static int attacked(const laplace_chess_state_t* s, int sq, int white) {
    int sign = white ? 1 : -1;
    for (int i = 0; i < 2; ++i) {
        int p = sq - sign * (i ? 17 : 15);
        if (on(p) && s->squares[p] == sign) return 1;
    }
    for (int i = 0; i < 8; ++i) {
        int p = sq + knight_steps[i];
        if (on(p) && s->squares[p] == sign * 2) return 1;
        int distance = 1;
        for (p = sq + ray_steps[i]; on(p); p += ray_steps[i], ++distance) {
            int piece = s->squares[p];
            if (!piece) continue;
            if (piece == sign * 5 || piece == sign * (i < 4 ? 4 : 3)
                || (distance == 1 && piece == sign * 6)) return 1;
            break;
        }
    }
    return 0;
}
static void clear_rights(laplace_chess_state_t* s, int piece, int square) {
    if (abs(piece) == 6) s->castle &= piece > 0 ? ~3 : ~12;
    if (abs(piece) != 4) return;
    int base = piece > 0 ? 0 : 2, rank = piece > 0 ? 0 : 112;
    for (int flank = 0; flank < 2; ++flank)
        if (square == rank + s->rook_files[base + flank]) s->castle &= ~(1 << (base + flank));
}
static int apply_unchecked(laplace_chess_state_t* s, const laplace_chess_move_t* m) {
    if (!on(m->to)) return -1;
    int sign = s->white ? 1 : -1, piece, captured = 0, captured_sq = m->to;
    if (m->flags & LAPLACE_CHESS_DROP) {
        int type = abs(m->promotion), pocket = (s->white ? 0 : 5) + type - 1;
        if (!s->drops || m->from != 128 || type < 1 || type > 5 || s->squares[m->to]
            || !s->pockets[pocket] || (type == 1 && ((m->to >> 4) == 0 || (m->to >> 4) == 7))) return -1;
        piece = sign * type;
        --s->pockets[pocket];
        s->squares[m->to] = (int8_t)piece;
        s->promoted &= ~bit(m->to);
    } else {
        if (!on(m->from)) return -1;
        piece = s->squares[m->from];
        if (!piece || (piece > 0) != s->white) return -1;
        if (m->flags & LAPLACE_CHESS_CASTLE) {
            int base = s->white ? 0 : 2, rank = s->white ? 0 : 112;
            int flank = (m->to & 7) == 6 ? 0 : 1;
            int rook_from = rank + s->rook_files[base + flank], rook_to = rank + (flank ? 3 : 5);
            s->squares[m->from] = 0; s->squares[rook_from] = 0;
            s->squares[m->to] = (int8_t)piece; s->squares[rook_to] = (int8_t)(sign * 4);
            s->promoted &= ~(bit(m->from) | bit(rook_from) | bit(m->to) | bit(rook_to));
        } else {
            if (m->flags & LAPLACE_CHESS_EP) captured_sq = m->to - sign * 16;
            if (!on(captured_sq)) return -1;
            captured = s->squares[captured_sq];
            if (captured && ((captured > 0) == s->white || abs(captured) == 6)) return -1;
            if (s->drops && captured) {
                int type = (s->promoted & bit(captured_sq)) ? 1 : abs(captured);
                int pocket = (s->white ? 0 : 5) + type - 1;
                if (s->pockets[pocket] == UINT16_MAX) return -1;
                ++s->pockets[pocket];
            }
            int promoted = (s->promoted & bit(m->from)) != 0;
            s->promoted &= ~(bit(m->from) | bit(captured_sq) | bit(m->to));
            s->squares[m->from] = 0; s->squares[captured_sq] = 0;
            s->squares[m->to] = (int8_t)piece;
            if (m->flags & LAPLACE_CHESS_PROMOTION) {
                if (abs(piece) != 1 || m->promotion < 2 || m->promotion > 5) return -1;
                s->squares[m->to] = (int8_t)(sign * m->promotion); promoted = 1;
            }
            if (promoted) s->promoted |= bit(m->to);
        }
        clear_rights(s, piece, m->from);
        clear_rights(s, captured, captured_sq);
    }
    s->ep = m->flags & LAPLACE_CHESS_DOUBLE ? m->from + sign * 16 : -1;
    s->halfmove = captured || (!(m->flags & LAPLACE_CHESS_DROP) && abs(piece) == 1) ? 0 : s->halfmove + 1;
    if (!s->white) ++s->fullmove;
    s->white = !s->white;
    int opposing_king = king(s, s->white);
    if (s->check_target && opposing_king >= 0 && attacked(s, opposing_king, !s->white))
        ++s->checks[s->white ? 1 : 0];
    return 0;
}
typedef struct {
    const laplace_chess_state_t* state;
    laplace_chess_move_t* output;
    size_t capacity, count;
    int legal;
} emitter;
static void emit(emitter* e, int from, int to, int promotion, int flags) {
    laplace_chess_move_t m = {from,to,promotion,flags};
    if (e->legal) {
        laplace_chess_state_t next = *e->state;
        if (apply_unchecked(&next, &m)) return;
        int k = king(&next, e->state->white);
        if (k < 0 || attacked(&next, k, !e->state->white)) return;
    }
    if (e->count < e->capacity && e->output) e->output[e->count] = m;
    ++e->count;
}
static void pawn_emit(emitter* e, int from, int to, int flags) {
    if ((to >> 4) == 0 || (to >> 4) == 7)
        for (int promotion = 2; promotion <= 5; ++promotion) emit(e, from, to, promotion, flags | LAPLACE_CHESS_PROMOTION);
    else emit(e, from, to, 0, flags);
}
static void castles(emitter* e, int from) {
    const laplace_chess_state_t* s = e->state;
    int base = s->white ? 0 : 2, rank = s->white ? 0 : 112, sign = s->white ? 1 : -1;
    if ((from & 0x70) != rank || attacked(s, from, !s->white)) return;
    for (int flank = 0; flank < 2; ++flank) {
        if (!(s->castle & (1 << (base + flank)))) continue;
        int rf = s->rook_files[base + flank];
        if (rf < 0 || rf > 7) continue;
        int rook = rank + rf, dest = rank + (flank ? 2 : 6), rook_dest = rank + (flank ? 3 : 5);
        if (s->squares[rook] != sign * 4 || (s->promoted & bit(rook))) continue;
        int ok = 1;
        for (int sq = rank; sq < rank + 8; ++sq) {
            int king_path = sq >= (from < dest ? from : dest) && sq <= (from > dest ? from : dest);
            int rook_path = sq >= (rook < rook_dest ? rook : rook_dest) && sq <= (rook > rook_dest ? rook : rook_dest);
            if ((king_path || rook_path) && sq != from && sq != rook && s->squares[sq]) ok = 0;
        }
        for (int sq = from; ok && sq != dest;) {
            sq += dest > from ? 1 : -1;
            laplace_chess_state_t probe = *s;
            probe.squares[from] = 0;
            if (sq == rook) probe.squares[rook] = 0;
            probe.squares[sq] = (int8_t)(sign * 6);
            if (attacked(&probe, sq, !s->white)) ok = 0;
        }
        if (ok) emit(e, from, dest, 0, LAPLACE_CHESS_CASTLE);
    }
}
size_t laplace_chess_moves(const laplace_chess_state_t* s, int legal, laplace_chess_move_t* out, size_t capacity) {
    if (!s || (s->white != 0 && s->white != 1)) return 0;
    if (s->check_target && (s->checks[0] >= s->check_target || s->checks[1] >= s->check_target)) return 0;
    if (s->winning_squares) for (int white = 0; white <= 1; ++white) {
        int sq = king(s, white);
        if (sq >= 0 && (s->winning_squares & bit(sq))) return 0;
    }
    emitter e = {s,out,capacity,0,legal};
    int sign = s->white ? 1 : -1;
    for (int from = 0; from < 128; ++from) {
        if (!on(from)) continue;
        int piece = s->squares[from], type = abs(piece);
        if (!piece || (piece > 0) != s->white) continue;
        if (type == 1) {
            int to = from + sign * 16;
            if (on(to) && !s->squares[to]) {
                pawn_emit(&e, from, to, 0);
                int twice = to + sign * 16;
                if ((from >> 4) == (s->white ? 1 : 6) && !s->squares[twice]) emit(&e,from,twice,0,LAPLACE_CHESS_DOUBLE);
            }
            for (int i = 0; i < 2; ++i) {
                to = from + sign * (i ? 17 : 15);
                if (!on(to)) continue;
                int target = s->squares[to];
                if (target && (target > 0) != s->white && abs(target) != 6) pawn_emit(&e,from,to,0);
                else if (to == s->ep && !target && s->squares[to-sign*16] == -sign) emit(&e,from,to,0,LAPLACE_CHESS_EP);
            }
        } else if (type >= 2 && type <= 6) {
            for (int i = 0; i < 8; ++i) {
                if ((type == 3 && i < 4) || (type == 4 && i >= 4)) continue;
                int step = type == 2 ? knight_steps[i] : ray_steps[i];
                for (int to = from + step; on(to); to += step) {
                    int target = s->squares[to];
                    if (target && (target > 0) == s->white) break;
                    if (abs(target) != 6) emit(&e,from,to,0,0);
                    if (target || type == 2 || type == 6) break;
                }
            }
            if (type == 6) castles(&e,from);
        }
    }
    if (s->drops) for (int type = 1; type <= 5; ++type) {
        if (!s->pockets[(s->white ? 0 : 5) + type - 1]) continue;
        for (int to = 0; to < 128; ++to)
            if (on(to) && !s->squares[to] && (type != 1 || ((to >> 4) != 0 && (to >> 4) != 7)))
                emit(&e,128,to,type,LAPLACE_CHESS_DROP);
    }
    return e.count;
}
/* Every occupied square has at most 27 destinations, plus 320 drops and two
 * castles. This bound is board geometry, independent of game length. */
#define MOVE_CAPACITY (64 * 27 + 320 + 2)
int laplace_chess_apply(laplace_chess_state_t* s, const laplace_chess_move_t* m) {
    if (!s || !m) return -1;
    laplace_chess_move_t legal[MOVE_CAPACITY];
    size_t n = laplace_chess_moves(s,1,legal,MOVE_CAPACITY);
    if (n > MOVE_CAPACITY) return -1;
    for (size_t i=0;i<n;++i) if (!memcmp(m,&legal[i],sizeof(*m))) {
        laplace_chess_state_t next=*s;
        if (apply_unchecked(&next,m)) return -1;
        *s=next; return 0;
    }
    return -1;
}
static int piece_type(char c) {
    static const char pieces[] = " PNBRQK";
    const char* p = strchr(pieces,c);
    return p && c ? (int)(p-pieces) : 0;
}
int laplace_chess_resolve(const laplace_chess_state_t* s, const char* san, laplace_chess_move_t* out) {
    if (!s || !san || !out) return -1;
    size_t len=strlen(san);
    while (len && strchr("+#!?",san[len-1])) --len;
    if (!len || len > 16) return -1;
    char text[17]; memcpy(text,san,len); text[len]=0;
    int castle=0,drop=0,type=1,promotion=0,to=-1,file=-1,rank=-1,capture=0;
    if (!strcmp(text,"O-O") || !strcmp(text,"0-0")) castle=1;
    else if (!strcmp(text,"O-O-O") || !strcmp(text,"0-0-0")) castle=2;
    else {
        char* at=strchr(text,'@');
        if(at) {
            if(at!=text && at!=text+1) return -1;
            drop=1; type=at==text ? 1 : piece_type(text[0]);
            if(type<1 || type>5 || strlen(at+1)!=2) return -1;
            memmove(text,at+1,3); len=2;
        } else {
            char* eq=strchr(text,'=');
            if(eq) {
                if(strlen(eq)!=2 || (promotion=piece_type(eq[1]))<2 || promotion>5) return -1;
                *eq=0; len=(size_t)(eq-text);
            }
        }
        if(len<2 || text[len-2]<'a' || text[len-2]>'h' || text[len-1]<'1' || text[len-1]>'8') return -1;
        to=(text[len-1]-'1')*16+text[len-2]-'a';
        if(!drop) {
            size_t i=0;
            if(piece_type(text[0])>=2) type=piece_type(text[i++]);
            for(;i<len-2;++i) {
                char c=text[i];
                if(c=='x' && !capture) capture=1;
                else if(c>='a' && c<='h' && file<0) file=c-'a';
                else if(c>='1' && c<='8' && rank<0) rank=c-'1';
                else return -1;
            }
        }
    }
    laplace_chess_move_t legal[MOVE_CAPACITY];
    size_t n=laplace_chess_moves(s,1,legal,MOVE_CAPACITY), hits=0;
    if(n>MOVE_CAPACITY) return -1;
    for(size_t i=0;i<n;++i) {
        laplace_chess_move_t m=legal[i]; int match;
        if(castle) match=(m.flags & LAPLACE_CHESS_CASTLE) && (m.to & 7)==(castle==1 ? 6 : 2);
        else if(drop) match=(m.flags & LAPLACE_CHESS_DROP) && m.to==to && m.promotion==type;
        else match=!(m.flags & (LAPLACE_CHESS_CASTLE|LAPLACE_CHESS_DROP)) && m.to==to
            && abs(s->squares[m.from])==type && m.promotion==promotion
            && (file<0 || (m.from & 7)==file) && (rank<0 || (m.from >> 4)==rank)
            && capture==((s->squares[m.to]!=0 || (m.flags & LAPLACE_CHESS_EP)) ? 1 : 0);
        if(match) { *out=m; ++hits; }
    }
    return hits==1 ? 0 : -1;
}
int laplace_chess_replay(const laplace_chess_state_t* initial, const char* sans, size_t bytes,
    size_t count, laplace_chess_state_t* states, laplace_chess_move_t* moves, size_t* failed_ply) {
    if(!initial || !sans || !states || (!moves && count) || !failed_ply) return -1;
    states[0]=*initial; size_t offset=0;
    for(size_t i=0;i<count;++i) {
        *failed_ply=i;
        if(offset>=bytes) return -1;
        const char* end=memchr(sans+offset,0,bytes-offset);
        if(!end || laplace_chess_resolve(&states[i],sans+offset,&moves[i])) return -1;
        states[i+1]=states[i];
        if(apply_unchecked(&states[i+1],&moves[i])) return -1;
        offset=(size_t)(end-sans)+1;
    }
    *failed_ply=count;
    return offset==bytes ? 0 : -1;
}
