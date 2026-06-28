#include "laplace/core/etl_anchor.h"
#include "laplace/core/content_witness_batch.h"

#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static bool is_ss(char c) { return c == 'n' || c == 'v' || c == 'a' || c == 's' || c == 'r'; }
static bool is_digit(char c) { return c >= '0' && c <= '9'; }
static bool is_ws(char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; }

/* Trim leading/trailing ASCII whitespace in place over [*ps, *ps + *pn). */
static void trim(const char** ps, size_t* pn) {
    const char* s = *ps;
    size_t n = *pn;
    while (n > 0 && is_ws(s[0])) { ++s; --n; }
    while (n > 0 && is_ws(s[n - 1])) --n;
    *ps = s;
    *pn = n;
}

/* Case-insensitive match of the whole span against "NULL" (the C# parsers reject it). */
static bool is_null_token(const char* s, size_t n) {
    if (n != 4) return false;
    return (s[0] == 'N' || s[0] == 'n') && (s[1] == 'U' || s[1] == 'u')
        && (s[2] == 'L' || s[2] == 'l') && (s[3] == 'L' || s[3] == 'l');
}

/* All-digits, value > 0. */
static bool parse_offset(const char* s, size_t n, int64_t* out) {
    if (n == 0) return false;
    int64_t v = 0;
    for (size_t i = 0; i < n; ++i) {
        if (!is_digit(s[i])) return false;
        v = v * 10 + (int64_t)(s[i] - '0');
    }
    if (v <= 0) return false;
    *out = v;
    return true;
}

static size_t last_index_of(const char* s, size_t n, char c) {
    size_t found = (size_t)-1;
    for (size_t i = 0; i < n; ++i)
        if (s[i] == c) found = i;
    return found;
}

int lp_parse_mcr_synset(const char* s, size_t n, int64_t* out_offset, char* out_ss) {
    if (!s || !out_offset || !out_ss) return 0;
    trim(&s, &n);
    if (n == 0 || is_null_token(s, n)) return 0;

    /* StripPredicateMatrixNamespace: drop everything up to and including the first ':' (unless it is the
       last char or absent). */
    for (size_t i = 0; i < n; ++i) {
        if (s[i] == ':') {
            if (i + 1 < n) { s += i + 1; n -= i + 1; }
            break;
        }
    }

    /* strip a leading "ili-" (case-insensitive on the letters). */
    if (n >= 4 && (s[0] == 'i' || s[0] == 'I') && (s[1] == 'l' || s[1] == 'L')
        && (s[2] == 'i' || s[2] == 'I') && s[3] == '-') {
        s += 4;
        n -= 4;
    }

    size_t last_dash = last_index_of(s, n, '-');
    if (last_dash == (size_t)-1 || last_dash == 0 || last_dash + 1 >= n) return 0;

    char ss = s[last_dash + 1];
    if (!is_ss(ss)) return 0;

    /* offset = digit run after the LAST '-' within rest=s[..last_dash], or rest itself. */
    size_t rest_len = last_dash;
    size_t off_dash = last_index_of(s, rest_len, '-');
    const char* off_s = (off_dash != (size_t)-1) ? s + off_dash + 1 : s;
    size_t off_n = (off_dash != (size_t)-1) ? rest_len - off_dash - 1 : rest_len;

    int64_t off;
    if (!parse_offset(off_s, off_n, &off)) return 0;
    *out_offset = off;
    *out_ss = ss;
    return 1;
}

int lp_parse_mapnet_synset(const char* s, size_t n, int64_t* out_offset, char* out_ss) {
    if (!s || !out_offset || !out_ss) return 0;
    trim(&s, &n);
    if (n == 0 || is_null_token(s, n)) return 0;

    /* hash must have a pos char before it and at least one char after. */
    size_t hash = (size_t)-1;
    for (size_t i = 0; i < n; ++i)
        if (s[i] == '#') { hash = i; break; }
    if (hash == (size_t)-1 || hash == 0 || hash + 1 >= n) return 0;

    char ss = s[0];
    if (!is_ss(ss)) return 0;

    const char* rest = s + hash + 1;
    size_t rest_n = n - hash - 1;
    size_t k = 0;
    while (k < rest_n && is_digit(rest[k])) ++k;

    int64_t off;
    if (!parse_offset(rest, k, &off)) return 0;
    *out_offset = off;
    *out_ss = ss;
    return 1;
}

/* --- ILI offset map (mirrors C# IliMap.Key/Load/Resolve) --- */

typedef struct { int64_t key; char* ili; } lp_ili_entry_t;
struct lp_ili_map { lp_ili_entry_t* e; size_t n; };

/* pos code: a and s collapse to 3 (an adjective offset is a XOR s; OMW writes satellites as -a). */
static long pos_code(char ss) {
    switch (ss) {
        case 'n': return 1;
        case 'v': return 2;
        case 'a': return 3;
        case 's': return 3;
        case 'r': return 5;
        default:  return 0;
    }
}

static int64_t ili_key(int64_t offset, char ss) { return (offset << 3) | pos_code(ss); }

static int cmp_entry(const void* a, const void* b) {
    int64_t ka = ((const lp_ili_entry_t*)a)->key, kb = ((const lp_ili_entry_t*)b)->key;
    return (ka > kb) - (ka < kb);
}

lp_ili_map_t* lp_ili_map_load(const char* path) {
    if (!path) return NULL;
    FILE* f = fopen(path, "rb");
    if (!f) return NULL;
    fseek(f, 0, SEEK_END);
    long sz = ftell(f);
    fseek(f, 0, SEEK_SET);
    if (sz <= 0) { fclose(f); return NULL; }
    char* buf = (char*)malloc((size_t)sz + 1);
    if (!buf) { fclose(f); return NULL; }
    size_t rd = fread(buf, 1, (size_t)sz, f);
    fclose(f);
    buf[rd] = '\0';

    size_t cap = 1;
    for (size_t i = 0; i < rd; ++i) if (buf[i] == '\n') ++cap;
    lp_ili_entry_t* e = (lp_ili_entry_t*)malloc(cap * sizeof(*e));
    if (!e) { free(buf); return NULL; }

    size_t n = 0;
    char* p = buf;
    char* end = buf + rd;
    while (p < end) {
        char* nl = (char*)memchr(p, '\n', (size_t)(end - p));
        char* line = p;
        size_t llen = nl ? (size_t)(nl - p) : (size_t)(end - p);
        p = nl ? nl + 1 : end;
        if (llen > 0 && line[llen - 1] == '\r') --llen;

        char* t1 = (char*)memchr(line, '\t', llen);
        if (!t1) continue;
        size_t ili_len = (size_t)(t1 - line);
        if (ili_len == 0) continue;

        char* op = t1 + 1;
        size_t op_len = llen - ili_len - 1;
        char* t2 = (char*)memchr(op, '\t', op_len);   /* older 3-col: stop offset-pos at next tab */
        if (t2) op_len = (size_t)(t2 - op);
        while (op_len > 0 && (op[0] == ' ' || op[0] == '\t')) { ++op; --op_len; }
        while (op_len > 0 && is_ws(op[op_len - 1])) --op_len;

        size_t dash = (size_t)-1;
        for (size_t i = 0; i < op_len; ++i) if (op[i] == '-') dash = i;
        if (dash == (size_t)-1 || dash == 0 || dash + 1 >= op_len) continue;
        char ss = op[dash + 1];

        int64_t off = 0;
        bool ok = true;
        for (size_t i = 0; i < dash; ++i) {
            if (!is_digit(op[i])) { ok = false; break; }
            off = off * 10 + (int64_t)(op[i] - '0');
        }
        if (!ok) continue;

        char* ili = (char*)malloc(ili_len + 1);
        if (!ili) continue;
        memcpy(ili, line, ili_len);
        ili[ili_len] = '\0';
        e[n].key = ili_key(off, ss);
        e[n].ili = ili;
        ++n;
    }
    free(buf);
    qsort(e, n, sizeof(*e), cmp_entry);

    lp_ili_map_t* m = (lp_ili_map_t*)malloc(sizeof(*m));
    if (!m) { for (size_t i = 0; i < n; ++i) free(e[i].ili); free(e); return NULL; }
    m->e = e;
    m->n = n;
    return m;
}

const char* lp_ili_map_resolve(const lp_ili_map_t* m, int64_t offset, char ss) {
    if (!m) return NULL;
    int64_t key = ili_key(offset, ss);
    size_t lo = 0, hi = m->n;
    while (lo < hi) {
        size_t mid = lo + (hi - lo) / 2;
        if (m->e[mid].key < key) lo = mid + 1;
        else hi = mid;
    }
    return (lo < m->n && m->e[lo].key == key) ? m->e[lo].ili : NULL;
}

size_t lp_ili_map_count(const lp_ili_map_t* m) { return m ? m->n : 0; }

void lp_ili_map_free(lp_ili_map_t* m) {
    if (!m) return;
    for (size_t i = 0; i < m->n; ++i) free(m->e[i].ili);
    free(m->e);
    free(m);
}

int lp_resolve_synset_anchor(const lp_ili_map_t* map, const char* raw, size_t n, hash128_t* out_id) {
    if (!map || !raw || !out_id) return 0;
    trim(&raw, &n);
    if (n == 0 || is_null_token(raw, n)) return 0;

    /* WN-RDF tail: strip everything up to and including the last '/'. */
    size_t slash = last_index_of(raw, n, '/');
    if (slash != (size_t)-1 && slash + 1 < n) { raw += slash + 1; n -= slash + 1; }

    int64_t offset;
    char ss;
    if (!lp_parse_mcr_synset(raw, n, &offset, &ss) && !lp_parse_mapnet_synset(raw, n, &offset, &ss))
        return 0;

    const char* ili = lp_ili_map_resolve(map, offset, ss);
    if (!ili) return 0;

    return laplace_content_root_id((const uint8_t*)ili, strlen(ili), out_id) == 0 ? 1 : 0;
}

int lp_resolve_sense_anchor(const char* raw, size_t n, hash128_t* out_id) {
    if (!raw || !out_id) return 0;
    const char* s = raw;
    size_t len = n;
    trim(&s, &len);
    while (len > 0 && (s[0] == '?' || s[0] == '!')) { ++s; --len; }  /* TrimStart('?','!') */
    if (len == 0) return 0;

    size_t pct = (size_t)-1;
    for (size_t i = 0; i < len; ++i) if (s[i] == '%') { pct = i; break; }
    if (pct == (size_t)-1 || pct == 0 || pct + 1 >= len) return 0;

    /* rest = s[pct+1 .. len): require >= 3 ':'-fields; take the first three. */
    const char* rest = s + pct + 1;
    size_t rlen = len - pct - 1;
    size_t c1 = (size_t)-1, c2 = (size_t)-1, c3 = (size_t)-1;
    for (size_t i = 0; i < rlen; ++i) {
        if (rest[i] != ':') continue;
        if (c1 == (size_t)-1) c1 = i;
        else if (c2 == (size_t)-1) c2 = i;
        else { c3 = i; break; }
    }
    if (c1 == (size_t)-1 || c2 == (size_t)-1) return 0;
    size_t f2end = (c3 == (size_t)-1) ? rlen : c3;

    /* Build "lemma%f0:f1:f2" with lemma '_' -> ' '. Sense keys are short; bail if it doesn't fit. */
    char buf[512];
    size_t bi = 0;
#define LP_PUT(ch) do { if (bi >= sizeof(buf)) return 0; buf[bi++] = (char)(ch); } while (0)
    for (size_t i = 0; i < pct; ++i) LP_PUT(s[i] == '_' ? ' ' : s[i]);
    LP_PUT('%');
    for (size_t i = 0; i < c1; ++i) LP_PUT(rest[i]);
    LP_PUT(':');
    for (size_t i = c1 + 1; i < c2; ++i) LP_PUT(rest[i]);
    LP_PUT(':');
    for (size_t i = c2 + 1; i < f2end; ++i) LP_PUT(rest[i]);
#undef LP_PUT

    return laplace_content_root_id((const uint8_t*)buf, bi, out_id) == 0 ? 1 : 0;
}

int lp_resolve_category_anchor(const char* raw, size_t n, hash128_t* out_id) {
    if (!raw || !out_id) return 0;
    const char* s = raw;
    size_t len = n;
    trim(&s, &len);
    if (len == 0) return 0;
    return laplace_content_root_id((const uint8_t*)s, len, out_id) == 0 ? 1 : 0;
}
