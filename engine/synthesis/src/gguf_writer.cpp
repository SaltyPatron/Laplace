#include "laplace/synthesis/gguf_writer.h"

#include <cstddef>

/* Real impl lands Chunk 7 Story 7.15 — GGUF proof/compatibility writer per
 * the spec at github.com/ggerganov/ggml. Per RULES.md R4: sparse-by-
 * construction emission (positions with no significant substrate attestation
 * emit exact zero). Stubs satisfy linkage. */

struct gguf_writer {
    int _placeholder;
};

extern "C" gguf_writer_t* gguf_writer_create(const char* output_path) {
    (void)output_path;
    return nullptr;
}

extern "C" int gguf_writer_add_metadata_str(gguf_writer_t* w, const char* key, const char* value) {
    (void)w; (void)key; (void)value;
    return -1;
}

extern "C" int gguf_writer_add_metadata_u32(gguf_writer_t* w, const char* key, uint32_t value) {
    (void)w; (void)key; (void)value;
    return -1;
}

extern "C"
int gguf_writer_add_tensor(gguf_writer_t* w,
                           const char*    name,
                           int            dtype,
                           const size_t*  shape,
                           size_t         rank,
                           const void*    data) {
    (void)w; (void)name; (void)dtype; (void)shape; (void)rank; (void)data;
    return -1;
}

extern "C" int gguf_writer_finalize(gguf_writer_t* w) {
    (void)w;
    return -1;
}

extern "C" void gguf_writer_free(gguf_writer_t* w) {
    delete w;
}
