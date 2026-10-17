/* The identity law (Laplace-Wiki Storage/Identity.md) checked on the monorepo's own composition code, compiled with
 * Laplace-Native's lp_id_compose as the build links it. Reads "text<TAB>expected-hex" lines on stdin (tests/identity-law.sh
 * takes them from app/Laplace.Core.Tests/Core/IdentityLawVectorTests.cs, one list) and checks, for each text:
 *   hash128_merkle over its codepoints' hash128_blake3  (the monorepo's composition, managed callers' path)
 *   hash128_merkle_runs over the same children           (the run-length path trajectories take)
 *   hash128_label_content_id, for word-character labels  (the label path)
 *   lp_id_codepoints_utf8                                 (Laplace-Native, the one implementation)
 * and that run-length encoded children stream to the same ID as their expansion. Exit 1 on any difference. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "laplace/core/hash128.h"
#include "laplace/laplace.h"

static void hex(const hash128_t *h, char out[33]) {
    const unsigned char *b = (const unsigned char *)h;
    for (int i = 0; i < 16; i++) snprintf(out + 2 * i, 3, "%02x", b[i]);
}

typedef struct { const hash128_t *kids; const size_t *runs; size_t n, i; } reader_t;
static int next_run(void *ctx, hash128_t *child, size_t *run) {
    reader_t *r = ctx;
    if (r->i >= r->n) return 1;
    *child = r->kids[r->i]; *run = r->runs ? r->runs[r->i] : 1; r->i++;
    return 0;
}

static int check(const char *what, const char *text, const hash128_t *got, const char *expected) {
    char h[33]; hex(got, h);
    if (strcmp(h, expected) == 0) return 0;
    fprintf(stderr, "FAIL %-26s %-10s got %s expected %s\n", what, text, h, expected);
    return 1;
}

#define MAXK 256
static int vector(const char *text, const char *expected) {
    hash128_t kids[MAXK], id; size_t n = 0, len = strlen(text), i = 0; uint32_t cp; int bad = 0, word = 1;
    while (i < len) {
        size_t at = i;
        if (!lp_utf8_next((const uint8_t *)text, len, &i, &cp) || n == MAXK) { fprintf(stderr, "FAIL bad text %s\n", text); return 1; }
        hash128_blake3((const uint8_t *)text + at, i - at, &kids[n++]);
        if (!(cp < 128 && (cp == '_' || (cp >= '0' && cp <= '9') || ((cp | 32) >= 'a' && (cp | 32) <= 'z')))) word = 0;
    }
    if (n == 1) id = kids[0]; else hash128_merkle(2, kids, n, &id);
    bad |= check("hash128_merkle", text, &id, expected);
    if (n >= 2) {
        reader_t r = { kids, NULL, n, 0 };
        if (hash128_merkle_runs(n, next_run, &r, &id) != 0) { fprintf(stderr, "FAIL hash128_merkle_runs %s: error\n", text); bad = 1; }
        else bad |= check("hash128_merkle_runs", text, &id, expected);
    }
    if (word && hash128_label_content_id(text, len, &id) == 0) bad |= check("hash128_label_content_id", text, &id, expected);
    lp_id nid;
    if (!lp_id_codepoints_utf8(text, len, &nid)) { fprintf(stderr, "FAIL lp_id_codepoints_utf8 %s: error\n", text); bad = 1; }
    else bad |= check("lp_id_codepoints_utf8", text, (const hash128_t *)&nid, expected);
    if (!bad) printf("ok   %-10s %s\n", text, expected);
    return bad;
}

/* Runs: [x*3, y, x*70] streams to the ID of its 74 expanded children, composed by Laplace-Native. */
static int runs(void) {
    hash128_t k[3], expanded[74], a, b; size_t r[3] = { 3, 1, 70 }, m = 0;
    hash128_blake3((const uint8_t *)"x", 1, &k[0]); hash128_blake3((const uint8_t *)"y", 1, &k[1]); k[2] = k[0];
    for (int i = 0; i < 3; i++) for (size_t j = 0; j < r[i]; j++) expanded[m++] = k[i];
    reader_t rd = { k, r, 3, 0 };
    if (hash128_merkle_runs(m, next_run, &rd, &a) != 0) { fprintf(stderr, "FAIL runs: error\n"); return 1; }
    lp_id_compose((const lp_id *)expanded, m, (lp_id *)&b);
    char ha[33], hb[33]; hex(&a, ha); hex(&b, hb);
    if (strcmp(ha, hb) != 0) { fprintf(stderr, "FAIL runs: streamed %s, lp_id_compose %s\n", ha, hb); return 1; }
    printf("ok   runs       %s (74 children in 3 runs)\n", ha);
    /* A reader that says more or fewer children than declared is refused, on the stack and the heap path. */
    size_t short_r[3] = { 3, 1, 69 }, long_r[3] = { 3, 1, 71 };
    reader_t s1 = { k, short_r, 3, 0 }, s2 = { k, long_r, 3, 0 }, s3 = { k, r, 2, 0 };
    if (hash128_merkle_runs(m, next_run, &s1, &a) == 0 || hash128_merkle_runs(m, next_run, &s2, &a) == 0 ||
        hash128_merkle_runs(5, next_run, &s3, &a) == 0 || hash128_merkle_runs(1, next_run, &rd, &a) == 0) {
        fprintf(stderr, "FAIL runs: a reader that does not end at child_count was accepted\n"); return 1; }
    printf("ok   runs       short, long and one-child readers refused\n");
    return 0;
}

int main(void) {
    char line[1024]; int bad = 0, count = 0;
    while (fgets(line, sizeof line, stdin)) {
        char *tab = strchr(line, '\t'); if (!tab) continue;
        *tab = 0; char *e = tab + 1; e[strcspn(e, "\r\n")] = 0;
        bad |= vector(line, e); count++;
    }
    if (count == 0) { fprintf(stderr, "FAIL no vectors read\n"); return 1; }
    bad |= runs();
    printf("%d vectors, %s\n", count, bad ? "FAILED" : "all match the law");
    return bad;
}
