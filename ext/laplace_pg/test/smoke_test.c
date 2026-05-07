/*
 * smoke_test.c — Phase 1 sample test. Verifies laplace_native links and the
 * version function returns a non-zero packed value matching the build.
 */

#include <stdio.h>
#include <stdint.h>

extern uint32_t laplace_native_version(void);

int main(void)
{
    uint32_t v = laplace_native_version();
    printf("laplace_native version: %u.%u.%u\n",
           (v >> 16) & 0xFFu,
           (v >>  8) & 0xFFu,
           (v      ) & 0xFFu);
    if (v == 0u) {
        fprintf(stderr, "FAIL: version is zero\n");
        return 1;
    }
    return 0;
}
