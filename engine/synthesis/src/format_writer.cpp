#include "laplace/synthesis/format_writer.h"

#include <cerrno>
#include <cstdio>
#include <cstring>
#include <string>
#include <sys/stat.h>
#include <vector>

#ifdef _WIN32
#include <direct.h>
#define laplace_mkdir(p) _mkdir(p)
#ifndef S_ISDIR
#define S_ISDIR(m) (((m) & _S_IFMT) == _S_IFDIR)
#endif
#else
#define laplace_mkdir(p) mkdir((p), 0755)
#endif

namespace {

const char* dtype_str(int dtype) {
    switch (dtype) {
        case 0: return "F32";
        case 1: return "F16";
        case 2: return "BF16";
        default: return "BF16";
    }
}

size_t dtype_elem_size(int dtype) {
    switch (dtype) {
        case 0: return 4;
        case 1: return 2;
        case 2: return 2;
        default: return 2;
    }
}

bool mkdir_p(const std::string& path) {
    struct stat st;
    if (stat(path.c_str(), &st) == 0) return S_ISDIR(st.st_mode);
    if (laplace_mkdir(path.c_str()) != 0 && errno != EEXIST) return false;
    return true;
}

bool write_file(const std::string& path, const void* data, size_t len) {
    FILE* f = std::fopen(path.c_str(), "wb");
    if (!f) return false;
    bool ok = (std::fwrite(data, 1, len, f) == len);
    std::fclose(f);
    return ok;
}

bool write_file(const std::string& path, const std::string& content) {
    return write_file(path, content.data(), content.size());
}

struct TensorRecord {
    std::string          name;
    int                  dtype;
    std::vector<size_t>  shape;
    std::vector<uint8_t> data;
};

}

struct format_writer {
    std::string              format;
    std::string              output_dir;
    std::vector<TensorRecord> tensors;
    std::string              config_json;
    std::string              tokenizer_json;
};

extern "C"
format_writer_t* format_writer_create(const char* format, const char* output_dir_path) {
    if (!format || !output_dir_path) return nullptr;
    if (std::strcmp(format, "safetensors") != 0) return nullptr;
    if (!mkdir_p(output_dir_path)) return nullptr;

    auto* w = new format_writer();
    w->format     = format;
    w->output_dir = output_dir_path;
    return w;
}

extern "C"
int format_writer_add_tensor(format_writer_t* w,
                             const char*      name,
                             int              dtype,
                             const size_t*    shape,
                             size_t           rank,
                             const void*      data,
                             size_t           data_len) {
    if (!w || !name || !shape || !data || rank == 0) return -1;

    TensorRecord tr;
    tr.name  = name;
    tr.dtype = dtype;
    for (size_t d = 0; d < rank; ++d) tr.shape.push_back(shape[d]);

    size_t n_elements = 1;
    for (size_t s : tr.shape) n_elements *= s;
    const size_t expected = n_elements * dtype_elem_size(dtype);
    if (expected != data_len) return -1;

    tr.data.resize(data_len);
    std::memcpy(tr.data.data(), data, data_len);
    w->tensors.push_back(std::move(tr));
    return 0;
}

extern "C"
int format_writer_set_config(format_writer_t* w, const char* config_json, size_t len) {
    if (!w || !config_json) return -1;
    w->config_json.assign(config_json, len);
    return 0;
}

extern "C"
int format_writer_set_tokenizer(format_writer_t* w, const char* tokenizer_json, size_t len) {
    if (!w || !tokenizer_json) return -1;
    w->tokenizer_json.assign(tokenizer_json, len);
    return 0;
}

extern "C"
int format_writer_finalize(format_writer_t* w) {
    if (!w) return -1;

    std::vector<size_t> offsets(w->tensors.size(), 0);
    size_t cur = 0;
    for (size_t i = 0; i < w->tensors.size(); ++i) {
        offsets[i] = cur;
        cur += w->tensors[i].data.size();
    }

    std::string header_json = "{";
    bool first = true;
    for (size_t i = 0; i < w->tensors.size(); ++i) {
        const TensorRecord& tr = w->tensors[i];
        if (!first) header_json += ',';
        first = false;

        std::string shape_arr = "[";
        for (size_t d = 0; d < tr.shape.size(); ++d) {
            if (d > 0) shape_arr += ',';
            shape_arr += std::to_string(tr.shape[d]);
        }
        shape_arr += ']';

        char entry[1024];
        std::snprintf(entry, sizeof(entry),
            R"("%s":{"dtype":"%s","shape":%s,"data_offsets":[%zu,%zu]})",
            tr.name.c_str(),
            dtype_str(tr.dtype),
            shape_arr.c_str(),
            offsets[i],
            offsets[i] + tr.data.size());
        header_json += entry;
    }
    header_json += '}';

    const std::string shard_path = w->output_dir + "/model.safetensors";
    FILE* f = std::fopen(shard_path.c_str(), "wb");
    if (!f) return -1;

    const uint64_t header_len = (uint64_t)header_json.size();
    uint8_t hlen_bytes[8];
    for (int b = 0; b < 8; ++b)
        hlen_bytes[b] = (uint8_t)((header_len >> (8 * b)) & 0xFF);
    std::fwrite(hlen_bytes, 1, 8, f);
    std::fwrite(header_json.data(), 1, header_json.size(), f);

    for (size_t i = 0; i < w->tensors.size(); ++i) {
        const std::vector<uint8_t>& d = w->tensors[i].data;
        if (std::fwrite(d.data(), 1, d.size(), f) != d.size()) {
            std::fclose(f);
            return -1;
        }
    }
    std::fclose(f);

    std::string index_json = R"({"metadata":{"format":"pt"},"weight_map":{)";
    first = true;
    for (const TensorRecord& tr : w->tensors) {
        if (!first) index_json += ',';
        first = false;
        index_json += '"';
        index_json += tr.name;
        index_json += R"(":"model.safetensors")";
    }
    index_json += "}}";
    write_file(w->output_dir + "/model.safetensors.index.json", index_json);

    if (!w->config_json.empty())
        write_file(w->output_dir + "/config.json", w->config_json);
    if (!w->tokenizer_json.empty())
        write_file(w->output_dir + "/tokenizer.json", w->tokenizer_json);

    const char* provenance = R"({"generator":"laplace_substrate","format":"safetensors_substrate_v0","sparse_by_construction":true,"zero_fill_policy":"no_attestation_exact_zero"})";
    write_file(w->output_dir + "/provenance.json",
               provenance, std::strlen(provenance));

    return 0;
}

extern "C" void format_writer_free(format_writer_t* w) {
    delete w;
}
