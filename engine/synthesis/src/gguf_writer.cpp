#include "laplace/synthesis/gguf_writer.h"

#include <cstdint>
#include <array>
#include <limits>
#include <unordered_set>
#ifdef _WIN32
#include <windows.h>
#endif
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
    size_t bytes = 0;
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
        default: return -1;
    }
}

}

struct gguf_writer {
    std::string              output_path;
    std::vector<MetaKV>      metadata;
    std::vector<TensorEntry> tensors;
    std::unordered_set<std::string> tensor_names,metadata_names;
    FILE* payload=nullptr;
    bool failed=false,finalized=false;
    ~gguf_writer(){if(payload)std::fclose(payload);}
};

extern "C" gguf_writer_t* gguf_writer_create(const char* output_path) {
    if (!output_path) return nullptr;
    auto* w = new gguf_writer();
    w->output_path = output_path;
    w->payload=std::tmpfile();
    if(!w->payload){delete w;return nullptr;}
    return w;
}

extern "C" int gguf_writer_add_metadata_str(gguf_writer_t* w, const char* key, const char* value) {
    if (!w || !key || !value) return -1;
    MetaKV kv;
    kv.key = key;
    kv.kind = META_STR;
    kv.str_val = value;
    if(w->failed||w->finalized||!w->metadata_names.insert(kv.key).second)return -1;
    w->metadata.push_back(std::move(kv));
    return 0;
}

extern "C" int gguf_writer_add_metadata_u32(gguf_writer_t* w, const char* key, uint32_t value) {
    if (!w || !key) return -1;
    MetaKV kv;
    kv.key = key;
    kv.kind = META_U32;
    kv.u32_val = value;
    if(w->failed||w->finalized||!w->metadata_names.insert(kv.key).second)return -1;
    w->metadata.push_back(std::move(kv));
    return 0;
}

extern "C" int gguf_writer_add_metadata_f32(gguf_writer_t* w, const char* key, float value) {
    if (!w || !key) return -1;
    MetaKV kv;
    kv.key = key;
    kv.kind = META_F32;
    kv.f32_val = value;
    if(w->failed||w->finalized||!w->metadata_names.insert(kv.key).second)return -1;
    w->metadata.push_back(std::move(kv));
    return 0;
}

extern "C" int gguf_writer_add_metadata_bool(gguf_writer_t* w, const char* key, int value) {
    if (!w || !key) return -1;
    MetaKV kv;
    kv.key = key;
    kv.kind = META_BOOL;
    kv.u32_val = value ? 1u : 0u;
    if(w->failed||w->finalized||!w->metadata_names.insert(kv.key).second)return -1;
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
    if(w->failed||w->finalized||!w->metadata_names.insert(kv.key).second)return -1;
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
    if(w->failed||w->finalized||!w->metadata_names.insert(kv.key).second)return -1;
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
    if(w->failed||w->finalized||!w->metadata_names.insert(kv.key).second)return -1;
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
    if(!w||w->failed||w->finalized||!name||!shape||!data||!rank||rank>8||dtype_from_api(dtype)<0||w->tensor_names.count(name))return -1;
    TensorEntry te;te.name=name;te.dtype=(uint32_t)dtype_from_api(dtype);
    size_t elements=1;
    for(size_t d=0;d<rank;d++){
        if(!shape[d]||elements>SIZE_MAX/shape[d])return -1;
        elements*=shape[d];te.dims.push_back(shape[d]);
    }
    size_t element_size=gguf_dtype_element_size(te.dtype);
    if(!element_size||elements>SIZE_MAX/element_size)return -1;
    te.bytes=elements*element_size;
    if(te.bytes>SIZE_MAX-31)return -1;
    if(std::fwrite(data,1,te.bytes,w->payload)!=te.bytes){w->failed=true;return -1;}
    uint8_t zero[32]={};size_t padding=align_up(te.bytes,32)-te.bytes;
    if(padding&&std::fwrite(zero,1,padding,w->payload)!=padding){w->failed=true;return -1;}
    w->tensor_names.insert(te.name);w->tensors.push_back(std::move(te));return 0;
}

extern "C" int gguf_writer_finalize(gguf_writer_t* w) {
    if(!w||w->failed||w->finalized)return -1;

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
        uint64_t cur_offset = 0;
        for (size_t i = 0; i < w->tensors.size(); ++i) {
            tensor_offsets[i] = cur_offset;
            cur_offset = (uint64_t)align_up(
                (size_t)cur_offset + w->tensors[i].bytes, 32);
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

    const std::string pending=w->output_path+".partial";
    FILE* f=std::fopen(pending.c_str(),"wb");if(!f)return -1;
    bool ok=std::fwrite(header.data(),1,header.size(),f)==header.size()&&std::fflush(w->payload)==0;
    std::rewind(w->payload);std::array<uint8_t,65536> block;
    while(ok){size_t count=std::fread(block.data(),1,block.size(),w->payload);
        if(count&&std::fwrite(block.data(),1,count,f)!=count){ok=false;break;}
        if(count<block.size()){if(std::ferror(w->payload))ok=false;break;}}
    ok=std::fclose(f)==0&&ok;
    if(!ok){std::remove(pending.c_str());w->failed=true;return -1;}
#ifdef _WIN32
    if(!MoveFileExA(pending.c_str(),w->output_path.c_str(),MOVEFILE_REPLACE_EXISTING)){std::remove(pending.c_str());return -1;}
#else
    if(std::rename(pending.c_str(),w->output_path.c_str())!=0){std::remove(pending.c_str());return -1;}
#endif
    w->finalized=true;return 0;
}

extern "C" void gguf_writer_free(gguf_writer_t* w) {
    delete w;
}

/* ---- HF -> GGML tensor naming ------------------------------------------------
 * Table-driven so the per-layer suffixes stay readable next to the format they
 * serve. Anything unrecognized passes through unchanged: a tensor we cannot name
 * is still a tensor the caller asked us to write.
 */
extern "C" int gguf_tensor_name_hf_to_ggml(const char* hf_name, char* out_buf, size_t out_cap) {
    if (!hf_name || !out_buf || out_cap == 0) return -1;

    auto emit = [&](const std::string& s) -> int {
        if (s.size() + 1 > out_cap) return -1;
        std::memcpy(out_buf, s.c_str(), s.size() + 1);
        return (int)s.size();
    };

    const std::string hf(hf_name);

    if (hf == "model.embed_tokens.weight") return emit("token_embd.weight");
    if (hf == "model.norm.weight")         return emit("output_norm.weight");
    if (hf == "lm_head.weight")            return emit("output.weight");

    static const char kPrefix[] = "model.layers.";
    const size_t plen = sizeof(kPrefix) - 1;

    if (hf.compare(0, plen, kPrefix) == 0) {
        size_t dot = hf.find('.', plen);
        if (dot != std::string::npos && dot > plen) {
            const std::string idx  = hf.substr(plen, dot - plen);
            const std::string rest = hf.substr(dot + 1);

            static const struct { const char* hf_suffix; const char* ggml_suffix; } kMap[] = {
                {"self_attn.q_proj.weight",        "attn_q.weight"},
                {"self_attn.k_proj.weight",        "attn_k.weight"},
                {"self_attn.v_proj.weight",        "attn_v.weight"},
                {"self_attn.o_proj.weight",        "attn_output.weight"},
                {"mlp.gate_proj.weight",           "ffn_gate.weight"},
                {"mlp.up_proj.weight",             "ffn_up.weight"},
                {"mlp.down_proj.weight",           "ffn_down.weight"},
                {"input_layernorm.weight",         "attn_norm.weight"},
                {"post_attention_layernorm.weight","ffn_norm.weight"},
            };

            const char* mapped = rest.c_str();
            for (const auto& e : kMap) {
                if (rest == e.hf_suffix) { mapped = e.ggml_suffix; break; }
            }
            return emit("blk." + idx + "." + mapped);
        }
    }

    return emit(hf);
}
