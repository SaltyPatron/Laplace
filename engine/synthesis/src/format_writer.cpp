#include "laplace/synthesis/format_writer.h"

#include <array>
#include <cerrno>
#include <cstdio>
#include <cstring>
#include <limits>
#include <string>
#include <sys/stat.h>
#include <unordered_set>
#include <utility>
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
// These are the architecture tensor-spec ABI codes, not the decode codec enum.
const char* dtype_str(int dtype) {
    switch (dtype) {
        case 0: return "F32";
        case 1: return "F16";
        case 2: return "BF16";
        default: return nullptr;
    }
}

bool mkdir_p(const std::string& path) {
    struct stat st;
    if (stat(path.c_str(), &st) == 0) return S_ISDIR(st.st_mode);
    return laplace_mkdir(path.c_str()) == 0;
}

bool write_file(const std::string& path, const std::string& content) {
    FILE* f = std::fopen(path.c_str(), "wb");
    if (!f) return false;
    bool ok = std::fwrite(content.data(), 1, content.size(), f) == content.size();
    return std::fclose(f) == 0 && ok;
}

std::string json_string(const std::string& value) {
    static const char hex[] = "0123456789abcdef";
    std::string out = "\"";
    for (unsigned char c : value) {
        if (c == '"' || c == '\\') { out += '\\'; out += c; }
        else if (c < 0x20) {
            out += "\\u00"; out += hex[c >> 4]; out += hex[c & 15];
        } else out += c;
    }
    out += '"';
    return out;
}

struct TensorRecord {
    std::string name;
    int dtype;
    std::vector<size_t> shape;
    size_t offset;
    size_t bytes;
};
}

struct format_writer {
    std::string output_dir;
    std::vector<TensorRecord> tensors;
    std::unordered_set<std::string> names;
    std::string config_json;
    std::string tokenizer_json;
    FILE* payload = nullptr;
    size_t payload_bytes = 0;
    bool failed = false;
    bool finalized = false;
    ~format_writer() { if (payload) std::fclose(payload); }
};

extern "C"
format_writer_t* format_writer_create(const char* format, const char* output_dir_path) {
    if (!format || !output_dir_path || std::strcmp(format, "safetensors") != 0)
        return nullptr;
    if (!mkdir_p(output_dir_path)) return nullptr;
    auto* w = new format_writer();
    w->output_dir = output_dir_path;
    // Tensor bytes are streamed once. Export size does not become resident RAM.
    w->payload = std::tmpfile();
    if (!w->payload) { delete w; return nullptr; }
    return w;
}

extern "C"
int format_writer_add_tensor(format_writer_t* w, const char* name, int dtype,
                             const size_t* shape, size_t rank,
                             const void* data, size_t data_len) {
    if (!w || w->failed || w->finalized || !name || (rank && !shape)
        || (data_len && !data) || !dtype_str(dtype)
        || std::strcmp(name, "__metadata__") == 0 || w->names.count(name)) return -1;
    size_t elements = 1;
    for (size_t i = 0; i < rank; ++i) {
        if (shape[i] > static_cast<size_t>(std::numeric_limits<int64_t>::max())) return -1;
        if (shape[i] && elements > std::numeric_limits<size_t>::max() / shape[i]) return -1;
        elements *= shape[i];
    }
    size_t element_bytes = dtype == 0 ? 4 : 2;
    if (elements > std::numeric_limits<size_t>::max() / element_bytes
        || elements * element_bytes != data_len
        || w->payload_bytes > std::numeric_limits<size_t>::max() - data_len) return -1;
    if (data_len && std::fwrite(data, 1, data_len, w->payload) != data_len) {
        w->failed = true;
        return -1;
    }
    TensorRecord tr{name, dtype, {}, w->payload_bytes, data_len};
    if (rank) tr.shape.assign(shape, shape + rank);
    w->tensors.push_back(std::move(tr));
    w->names.insert(name);
    w->payload_bytes += data_len;
    return 0;
}

extern "C"
int format_writer_set_config(format_writer_t* w, const char* config_json, size_t len) {
    if (!w || w->failed || w->finalized || !config_json) return -1;
    w->config_json.assign(config_json, len);
    return 0;
}

extern "C"
int format_writer_set_tokenizer(format_writer_t* w, const char* tokenizer_json, size_t len) {
    if (!w || w->failed || w->finalized || !tokenizer_json) return -1;
    w->tokenizer_json.assign(tokenizer_json, len);
    return 0;
}

extern "C"
int format_writer_finalize(format_writer_t* w) {
    if (!w || w->failed || w->finalized) return -1;
    std::string header = "{\"__metadata__\":{\"format\":\"pt\"}";
    std::string index = "{\"metadata\":{\"total_size\":" + std::to_string(w->payload_bytes)
        + "},\"weight_map\":{";
    bool first = true;
    for (const auto& tr : w->tensors) {
        const std::string name = json_string(tr.name);
        header += ',' + name + ":{\"dtype\":\"" + dtype_str(tr.dtype) + "\",\"shape\":[";
        for (size_t i = 0; i < tr.shape.size(); ++i) {
            if (i) header += ',';
            header += std::to_string(tr.shape[i]);
        }
        header += "],\"data_offsets\":[" + std::to_string(tr.offset) + ','
            + std::to_string(tr.offset + tr.bytes) + "]}";
        if (!first) index += ',';
        first = false;
        index += name + ":\"model.safetensors\"";
    }
    header += '}';
    header.append((8 - header.size() % 8) % 8, ' ');
    index += "}}";
    if (std::fflush(w->payload) != 0 || std::fseek(w->payload, 0, SEEK_SET) != 0) return -1;
    const std::string shard = w->output_dir + "/model.safetensors";
    const std::string pending = shard + ".partial";
    FILE* out = std::fopen(pending.c_str(), "wb");
    if (!out) return -1;
    uint64_t header_len = header.size();
    uint8_t length[8];
    for (int i = 0; i < 8; ++i) length[i] = static_cast<uint8_t>(header_len >> (8 * i));
    bool ok = std::fwrite(length, 1, 8, out) == 8
        && std::fwrite(header.data(), 1, header.size(), out) == header.size();
    std::array<unsigned char, 65536> buffer;
    size_t remaining = w->payload_bytes;
    while (ok && remaining) {
        size_t n = remaining < buffer.size() ? remaining : buffer.size();
        ok = std::fread(buffer.data(), 1, n, w->payload) == n
            && std::fwrite(buffer.data(), 1, n, out) == n;
        remaining -= n;
    }
    ok = std::fclose(out) == 0 && ok;
    if (!ok) { std::remove(pending.c_str()); return -1; }
    // Do not report a completed export while a requested sidecar failed to write.
    if (!write_file(w->output_dir + "/model.safetensors.index.json", index)
        || (!w->config_json.empty() && !write_file(w->output_dir + "/config.json", w->config_json))
        || (!w->tokenizer_json.empty() && !write_file(w->output_dir + "/tokenizer.json", w->tokenizer_json))) {
        std::remove(pending.c_str()); return -1;
    }
    if (std::rename(pending.c_str(), shard.c_str()) != 0) return -1;
    w->finalized = true;
    return 0;
}

extern "C" void format_writer_free(format_writer_t* w) { delete w; }
