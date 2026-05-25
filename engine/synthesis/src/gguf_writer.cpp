#include "laplace/synthesis/gguf_writer.h"

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

/* GGUF v3 binary proof/compatibility writer.
 *
 * Format reference: https://github.com/ggerganov/ggml/blob/master/docs/gguf.md
 *
 * Layout:
 *   magic[4]        = "GGUF"
 *   version[4]      = 3 (uint32_le)
 *   n_tensors[8]    = uint64_le
 *   n_kv[8]         = uint64_le
 *   kv_pairs[]      = variable-length key-value metadata
 *   tensor_infos[]  = name + dims + dtype + offset per tensor
 *   <pad to 32B>
 *   tensor_data[]   = concatenated tensor bytes (each aligned to 32B)
 *
 * Per RULES.md R15: we implement the format directly rather than linking
 * llama.cpp/ggml. Per RULES.md R4: sparse-by-construction — the caller
 * is responsible for zeroing unsupported tensor positions. */

namespace {

/* GGUF value type tags (subset used here). */
enum GgufType : uint32_t {
    GGUF_TYPE_UINT32  = 4,
    GGUF_TYPE_STRING  = 8,
};

/* GGUF tensor element type tags. */
enum GgufTensorType : uint32_t {
    GGUF_TENSOR_F32  = 0,
    GGUF_TENSOR_F16  = 1,
    GGUF_TENSOR_BF16 = 32,
};

inline void write_u32(std::vector<uint8_t>& buf, uint32_t v) {
    buf.push_back((uint8_t)(v & 0xFF));
    buf.push_back((uint8_t)((v >> 8) & 0xFF));
    buf.push_back((uint8_t)((v >> 16) & 0xFF));
    buf.push_back((uint8_t)((v >> 24) & 0xFF));
}

inline void write_u64(std::vector<uint8_t>& buf, uint64_t v) {
    for (int i = 0; i < 8; ++i)
        buf.push_back((uint8_t)((v >> (8 * i)) & 0xFF));
}

/* GGUF string: uint64_le length + raw bytes (no null terminator). */
inline void write_string(std::vector<uint8_t>& buf, const std::string& s) {
    write_u64(buf, (uint64_t)s.size());
    buf.insert(buf.end(), s.begin(), s.end());
}

inline size_t align_up(size_t v, size_t align) {
    return (v + align - 1) & ~(size_t)(align - 1);
}

struct MetaKV {
    std::string key;
    bool        is_string;
    std::string str_val;
    uint32_t    u32_val = 0;
};

struct TensorEntry {
    std::string          name;
    uint32_t             dtype;    /* GGUF tensor type tag */
    std::vector<uint64_t> dims;    /* shape dimensions */
    std::vector<uint8_t> data;     /* raw bytes */
};

size_t gguf_dtype_element_size(uint32_t dtype) {
    switch (dtype) {
        case GGUF_TENSOR_F32:  return 4;
        case GGUF_TENSOR_F16:  return 2;
        case GGUF_TENSOR_BF16: return 2;
        default: return 0;
    }
}

int dtype_from_api(int api_dtype) {
    /* API dtype: 0=f32, 1=f16, 2=bf16 */
    switch (api_dtype) {
        case 0: return (int)GGUF_TENSOR_F32;
        case 1: return (int)GGUF_TENSOR_F16;
        case 2: return (int)GGUF_TENSOR_BF16;
        default: return (int)GGUF_TENSOR_BF16;
    }
}

} /* namespace */

struct gguf_writer {
    std::string              output_path;
    std::vector<MetaKV>      metadata;
    std::vector<TensorEntry> tensors;
};

extern "C" gguf_writer_t* gguf_writer_create(const char* output_path) {
    if (!output_path) return nullptr;
    auto* w = new gguf_writer();
    w->output_path = output_path;
    return w;
}

extern "C" int gguf_writer_add_metadata_str(gguf_writer_t* w, const char* key, const char* value) {
    if (!w || !key || !value) return -1;
    MetaKV kv;
    kv.key = key;
    kv.is_string = true;
    kv.str_val = value;
    w->metadata.push_back(std::move(kv));
    return 0;
}

extern "C" int gguf_writer_add_metadata_u32(gguf_writer_t* w, const char* key, uint32_t value) {
    if (!w || !key) return -1;
    MetaKV kv;
    kv.key = key;
    kv.is_string = false;
    kv.u32_val = value;
    w->metadata.push_back(std::move(kv));
    return 0;
}

extern "C"
int gguf_writer_add_tensor(gguf_writer_t* w,
                           const char*    name,
                           int            dtype,
                           const size_t*  shape,
                           size_t         rank,
                           const void*    data) {
    if (!w || !name || !shape || !data || rank == 0) return -1;

    TensorEntry te;
    te.name  = name;
    te.dtype = (uint32_t)dtype_from_api(dtype);

    size_t n_elements = 1;
    for (size_t d = 0; d < rank && d < 8; ++d) {
        te.dims.push_back((uint64_t)shape[d]);
        n_elements *= shape[d];
    }

    const size_t elem_size = gguf_dtype_element_size(te.dtype);
    if (elem_size == 0) return -1;

    const size_t byte_len = n_elements * elem_size;
    te.data.resize(byte_len);
    std::memcpy(te.data.data(), data, byte_len);

    w->tensors.push_back(std::move(te));
    return 0;
}

extern "C" int gguf_writer_finalize(gguf_writer_t* w) {
    if (!w) return -1;

    /* === Build the header + KV + tensor_info buffer in memory === */
    std::vector<uint8_t> header;
    header.reserve(4096);

    /* Magic */
    header.push_back('G'); header.push_back('G');
    header.push_back('U'); header.push_back('F');

    /* Version = 3 */
    write_u32(header, 3);

    /* n_tensors and n_kv (placeholder — we'll write the file in order) */
    write_u64(header, (uint64_t)w->tensors.size());
    write_u64(header, (uint64_t)w->metadata.size());

    /* KV pairs */
    for (const MetaKV& kv : w->metadata) {
        write_string(header, kv.key);
        if (kv.is_string) {
            write_u32(header, (uint32_t)GGUF_TYPE_STRING);
            write_string(header, kv.str_val);
        } else {
            write_u32(header, (uint32_t)GGUF_TYPE_UINT32);
            write_u32(header, kv.u32_val);
        }
    }

    /* Compute tensor data offsets (each tensor data section is 32B-aligned) */
    std::vector<uint64_t> tensor_offsets(w->tensors.size());
    {
        /* We need the full tensor_info section size first to know where data starts,
         * but tensor_info references offsets within the data section — so we do two
         * passes: first compute tensor_info byte size, then compute offsets. */

        /* Compute tensor_info section size */
        size_t tensor_info_bytes = 0;
        for (const TensorEntry& te : w->tensors) {
            /* name (uint64 + bytes) + n_dims (uint32) + dims (n_dims × uint64)
             * + type (uint32) + offset (uint64) */
            tensor_info_bytes += 8 + te.name.size();
            tensor_info_bytes += 4;
            tensor_info_bytes += te.dims.size() * 8;
            tensor_info_bytes += 4;
            tensor_info_bytes += 8;
        }

        /* Data starts at: header.size() + tensor_info_bytes, aligned to 32 */
        const size_t header_kv_end = header.size();
        const size_t data_section_start =
            align_up(header_kv_end + tensor_info_bytes, 32);
        (void)data_section_start; /* used below */

        /* Assign offsets within data section */
        uint64_t cur_offset = 0;
        for (size_t i = 0; i < w->tensors.size(); ++i) {
            tensor_offsets[i] = cur_offset;
            cur_offset = (uint64_t)align_up(
                (size_t)cur_offset + w->tensors[i].data.size(), 32);
        }
    }

    /* Tensor info section */
    for (size_t i = 0; i < w->tensors.size(); ++i) {
        const TensorEntry& te = w->tensors[i];
        write_string(header, te.name);
        write_u32(header, (uint32_t)te.dims.size());
        for (uint64_t dim : te.dims) write_u64(header, dim);
        write_u32(header, te.dtype);
        write_u64(header, tensor_offsets[i]);
    }

    /* Pad header to 32B alignment */
    const size_t pad = align_up(header.size(), 32) - header.size();
    header.insert(header.end(), pad, 0x00);

    /* === Write to file === */
    FILE* f = std::fopen(w->output_path.c_str(), "wb");
    if (!f) return -1;

    if (std::fwrite(header.data(), 1, header.size(), f) != header.size()) {
        std::fclose(f);
        return -1;
    }

    /* Tensor data section (each tensor padded to 32B) */
    for (size_t i = 0; i < w->tensors.size(); ++i) {
        const TensorEntry& te = w->tensors[i];
        if (std::fwrite(te.data.data(), 1, te.data.size(), f) != te.data.size()) {
            std::fclose(f);
            return -1;
        }
        /* Pad to next 32B boundary */
        const size_t written = te.data.size();
        const size_t pad_bytes = align_up(written, 32) - written;
        if (pad_bytes > 0) {
            uint8_t zeros[32] = {};
            std::fwrite(zeros, 1, pad_bytes, f);
        }
    }

    std::fclose(f);
    return 0;
}

extern "C" void gguf_writer_free(gguf_writer_t* w) {
    delete w;
}
