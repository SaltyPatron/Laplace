// Placeholder test — verifies the library links and version reports.
// Real test suite (coord4d, hash128, hilbert4d, mantissa_pack round-trip,
// cross-language consistency) populates in Chunk 1.

#include "laplace/laplace.h"
#include <cstring>

int main() {
    const char* v = laplace_version();
    if (v == nullptr) return 1;
    if (std::strcmp(v, "0.1.0") != 0) return 2;
    return 0;
}
