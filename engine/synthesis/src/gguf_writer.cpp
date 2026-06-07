#include "laplace/synthesis/gguf_writer.h"

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace {

enum GgufType : uint32_t {
    GGUF_TYPE_UINT32  = 4,
    GGUF_TYPE_INT32   = 5,
    GGUF_TYPE_FLOAT32 = 6,
    GGUF_TYPE_BOOL    = 7,
    GGUF_TYPE_STRING  = 8,
    GGUF_TYPE_ARRAY   = 9,
};

enum GgufTensorType : uint32_t {
    GGUF_TENSOR_F32  = 0,
    GGUF_TENSOR_F16  = 1,
    GGUF_TENSOR_BF16 = 30,
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

inline void write_string(std::vector<uint8_t>& buf, const std::string& s) {
    write_u64(buf, (uint64_t)s.size());
    buf.insert(buf.end(), s.begin(), s.end());
}

inline size_t align_up(size_t v, size_t align) {
    return (v + align - 1) & ~(size_t)(align - 1);
}

enum MetaKind {
    META_STR,
    META_U32,
    META_F32,
    META_BOOL,
    META_STR_ARRAY_PACKED,
    META_F32_ARRAY,
    META_I32_ARRAY,
};

struct MetaKV {
    std::string key;
    MetaKind kind = META_STR;

    std::string str_val;
    uint32_t    u32_val = 0;
    float       f32_val = 0.0f;

    size_t               array_count = 0;
    std::vector<uint8_t> packed_data;
    std::vector<float>   f32_array;
    std::vector<int32_t> i32_array;
};

struct TensorEntry {
    std::string           name;
    uint32_t              dtype;
    std::vector<uint64_t> dims;
    std::vector<uint8_t>  data;
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
    switch (api_dtype) {
        case 0: return (int)GGUF_TENSOR_F32;
        case 1: return (int)GGUF_TENSOR_F16;
        case 2: return (int)GGUF_TENSOR_BF16;
        default: return (int)GGUF_TENSOR_BF16;
    }
}

}

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
    kv.kind = META_STR;
    kv.str_val = value;
    w->metadata.push_back(std::move(kv));
    return 0;
}

extern "C" int gguf_writer_add_metadata_u32(gguf_writer_t* w, const char* key, uint32_t value) {
    if (!w || !key) return -1;
    MetaKV kv;
    kv.key = key;
    kv.kind = META_U32;
    kv.u32_val = value;
    w->metadata.push_back(std::move(kv));
    return 0;
}

extern "C" int gguf_writer_add_metadata_f32(gguf_writer_t* w, const char* key, float value) {
    if (!w || !key) return -1;
    MetaKV kv;
    kv.key = key;
    kv.kind = META_F32;
    kv.f32_val = value;
    w->metadata.push_back(std::move(kv));
    return 0;
}

extern "C" int gguf_writer_add_metadata_bool(gguf_writer_t* w, const char* key, int value) {
    if (!w || !key) return -1;
    MetaKV kv;
    kv.key = key;
    kv.kind = META_BOOL;
    kv.u32_val = value ? 1u : 0u;
    w->metadata.push_back(std::move(kv));
    return 0;
}

extern "C" int gguf_writer_add_metadata_str_array_packed(gguf_writer_t* w,
                                                          const char*    key,
                                                          const uint8_t* packed_data,
                                                          size_t         total_bytes,
                                                          size_t         count) {
    if (!w || !key || !packed_data) return -1;
    MetaKV kv;
    kv.key = key;
    kv.kind = META_STR_ARRAY_PACKED;
    kv.array_count = count;
    kv.packed_data.assign(packed_data, packed_data + total_bytes);
    w->metadata.push_back(std::move(kv));
    return 0;
}

extern "C" int gguf_writer_add_metadata_f32_array(gguf_writer_t* w, const char* key,
                                                   const float*   values, size_t count) {
    if (!w || !key || !values) return -1;
    MetaKV kv;
    kv.key = key;
    kv.kind = META_F32_ARRAY;
    kv.array_count = count;
    kv.f32_array.assign(values, values + count);
    w->metadata.push_back(std::move(kv));
    return 0;
}

extern "C" int gguf_writer_add_metadata_i32_array(gguf_writer_t* w, const char*    key,
                                                   const int32_t* values, size_t count) {
    if (!w || !key || !values) return -1;
    MetaKV kv;
    kv.key = key;
    kv.kind = META_I32_ARRAY;
    kv.array_count = count;
    kv.i32_array.assign(values, values + count);
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

    std::vector<uint8_t> header;
    header.reserve(4096);

    header.push_back('G'); header.push_back('G');
    header.push_back('U'); header.push_back('F');

    write_u32(header, 3);

    write_u64(header, (uint64_t)w->tensors.size());
    write_u64(header, (uint64_t)w->metadata.size());

    for (const MetaKV& kv : w->metadata) {
        write_string(header, kv.key);
        switch (kv.kind) {
        case META_STR:
            write_u32(header, (uint32_t)GGUF_TYPE_STRING);
            write_string(header, kv.str_val);
            break;
        case META_U32:
            write_u32(header, (uint32_t)GGUF_TYPE_UINT32);
            write_u32(header, kv.u32_val);
            break;
        case META_F32: {
            write_u32(header, (uint32_t)GGUF_TYPE_FLOAT32);
            uint32_t bits = 0;
            std::memcpy(&bits, &kv.f32_val, 4);
            write_u32(header, bits);
            break;
        }
        case META_BOOL:
            write_u32(header, (uint32_t)GGUF_TYPE_BOOL);
            header.push_back((uint8_t)(kv.u32_val ? 1 : 0));
            break;
        case META_STR_ARRAY_PACKED:
            write_u32(header, (uint32_t)GGUF_TYPE_ARRAY);
            write_u32(header, (uint32_t)GGUF_TYPE_STRING);
            write_u64(header, (uint64_t)kv.array_count);
            header.insert(header.end(), kv.packed_data.begin(), kv.packed_data.end());
            break;
        case META_F32_ARRAY:
            write_u32(header, (uint32_t)GGUF_TYPE_ARRAY);
            write_u32(header, (uint32_t)GGUF_TYPE_FLOAT32);
            write_u64(header, (uint64_t)kv.array_count);
            for (float v : kv.f32_array) {
                uint32_t bits = 0;
                std::memcpy(&bits, &v, 4);
                write_u32(header, bits);
            }
            break;
        case META_I32_ARRAY:
            write_u32(header, (uint32_t)GGUF_TYPE_ARRAY);
            write_u32(header, (uint32_t)GGUF_TYPE_INT32);
            write_u64(header, (uint64_t)kv.array_count);
            for (int32_t v : kv.i32_array)
                write_u32(header, (uint32_t)v);
            break;
        }
    }

    std::vector<uint64_t> tensor_offsets(w->tensors.size());
    {
        size_t tensor_info_bytes = 0;
        for (const TensorEntry& te : w->tensors) {
            tensor_info_bytes += 8 + te.name.size();
            tensor_info_bytes += 4;
            tensor_info_bytes += te.dims.size() * 8;
            tensor_info_bytes += 4;
            tensor_info_bytes += 8;
        }

        const size_t header_kv_end = header.size();
        const size_t data_section_start =
            align_up(header_kv_end + tensor_info_bytes, 32);
        (void)data_section_start;

        uint64_t cur_offset = 0;
        for (size_t i = 0; i < w->tensors.size(); ++i) {
            tensor_offsets[i] = cur_offset;
            cur_offset = (uint64_t)align_up(
                (size_t)cur_offset + w->tensors[i].data.size(), 32);
        }
    }

    for (size_t i = 0; i < w->tensors.size(); ++i) {
        const TensorEntry& te = w->tensors[i];
        write_string(header, te.name);
        write_u32(header, (uint32_t)te.dims.size());
        for (uint64_t dim : te.dims) write_u64(header, dim);
        write_u32(header, te.dtype);
        write_u64(header, tensor_offsets[i]);
    }

    const size_t pad = align_up(header.size(), 32) - header.size();
    header.insert(header.end(), pad, 0x00);

    FILE* f = std::fopen(w->output_path.c_str(), "wb");
    if (!f) return -1;

    if (std::fwrite(header.data(), 1, header.size(), f) != header.size()) {
        std::fclose(f);
        return -1;
    }

    for (size_t i = 0; i < w->tensors.size(); ++i) {
        const TensorEntry& te = w->tensors[i];
        if (std::fwrite(te.data.data(), 1, te.data.size(), f) != te.data.size()) {
            std::fclose(f);
            return -1;
        }
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
