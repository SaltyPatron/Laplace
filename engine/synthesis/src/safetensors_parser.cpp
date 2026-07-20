#include "laplace/synthesis/safetensors_parser.h"

#include <algorithm>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

namespace {

struct Tensor {
    std::string name;
    std::string dtype;
    std::vector<long long> shape;
    long long data_start = 0;
    long long data_end = 0;
};

/* A focused reader for the safetensors header schema. Deliberately not a general
 * JSON library: it accepts exactly the shapes this container defines and refuses
 * anything else, which is what "refuse rather than half-ingest" requires. */
class Reader {
public:
    Reader(const char* p, const char* end) : p_(p), end_(end) {}

    void ws() {
        while (p_ < end_ && (*p_ == ' ' || *p_ == '\t' || *p_ == '\n' || *p_ == '\r')) ++p_;
    }

    bool ch(char c) {
        ws();
        if (p_ < end_ && *p_ == c) { ++p_; return true; }
        return false;
    }

    bool peek(char c) {
        ws();
        return p_ < end_ && *p_ == c;
    }

    bool string_(std::string& out) {
        ws();
        if (p_ >= end_ || *p_ != '"') return false;
        ++p_;
        out.clear();
        while (p_ < end_ && *p_ != '"') {
            if (*p_ == '\\' && p_ + 1 < end_) {
                ++p_;
                switch (*p_) {
                    case '"':  out += '"';  break;
                    case '\\': out += '\\'; break;
                    case '/':  out += '/';  break;
                    case 'b':  out += '\b'; break;
                    case 'f':  out += '\f'; break;
                    case 'n':  out += '\n'; break;
                    case 'r':  out += '\r'; break;
                    case 't':  out += '\t'; break;
                    case 'u': {
                        /* \uXXXX -> UTF-8. Tensor names are ASCII in practice, but a
                         * silently mangled name would mint a different entity. */
                        if (p_ + 4 >= end_) return false;
                        unsigned cp = 0;
                        for (int k = 1; k <= 4; ++k) {
                            char c = p_[k];
                            unsigned d;
                            if (c >= '0' && c <= '9') d = (unsigned)(c - '0');
                            else if (c >= 'a' && c <= 'f') d = (unsigned)(c - 'a' + 10);
                            else if (c >= 'A' && c <= 'F') d = (unsigned)(c - 'A' + 10);
                            else return false;
                            cp = (cp << 4) | d;
                        }
                        p_ += 4;
                        if (cp < 0x80) {
                            out += (char)cp;
                        } else if (cp < 0x800) {
                            out += (char)(0xC0 | (cp >> 6));
                            out += (char)(0x80 | (cp & 0x3F));
                        } else {
                            out += (char)(0xE0 | (cp >> 12));
                            out += (char)(0x80 | ((cp >> 6) & 0x3F));
                            out += (char)(0x80 | (cp & 0x3F));
                        }
                        break;
                    }
                    default: return false;
                }
            } else {
                out += *p_;
            }
            ++p_;
        }
        if (p_ >= end_) return false;
        ++p_;
        return true;
    }

    bool integer(long long& out) {
        ws();
        const char* start = p_;
        if (p_ < end_ && (*p_ == '-' || *p_ == '+')) ++p_;
        bool any = false;
        long long v = 0;
        bool neg = (start < end_ && *start == '-');
        while (p_ < end_ && *p_ >= '0' && *p_ <= '9') {
            v = v * 10 + (*p_ - '0');
            ++p_;
            any = true;
        }
        if (!any) return false;
        /* A fractional or exponent form is not a valid offset or dimension. */
        if (p_ < end_ && (*p_ == '.' || *p_ == 'e' || *p_ == 'E')) return false;
        out = neg ? -v : v;
        return true;
    }

    /* Skip any well-formed value (used for __metadata__ and unknown keys). */
    bool skip_value() {
        ws();
        if (p_ >= end_) return false;
        if (*p_ == '"') { std::string tmp; return string_(tmp); }
        if (*p_ == '{' || *p_ == '[') {
            char open = *p_, close = (open == '{') ? '}' : ']';
            int depth = 0;
            while (p_ < end_) {
                if (*p_ == '"') { std::string tmp; if (!string_(tmp)) return false; continue; }
                if (*p_ == open) ++depth;
                else if (*p_ == close) { --depth; if (depth == 0) { ++p_; return true; } }
                ++p_;
            }
            return false;
        }
        while (p_ < end_ && *p_ != ',' && *p_ != '}' && *p_ != ']' &&
               *p_ != ' ' && *p_ != '\n' && *p_ != '\r' && *p_ != '\t') ++p_;
        return true;
    }

    bool done() const { return p_ >= end_; }

private:
    const char* p_;
    const char* end_;
};

}  // namespace

struct safetensors_header {
    long long header_bytes = 0;
    bool has_metadata = false;
    std::vector<Tensor> tensors;
};

extern "C" safetensors_header_t* safetensors_parse_header(const void* bytes, size_t len) {
    if (!bytes || len < 8) return nullptr;

    const unsigned char* raw = (const unsigned char*)bytes;

    uint64_t json_len = 0;
    for (int i = 0; i < 8; ++i) json_len |= (uint64_t)raw[i] << (8 * i);

    /* Same plausibility bound the managed reader used: a header larger than this is
     * a corrupt or hostile length field, not a real model. */
    if (json_len == 0 || json_len > (uint64_t)256 * 1024 * 1024) return nullptr;
    if ((uint64_t)len < 8 + json_len) return nullptr;

    const char* p = (const char*)raw + 8;
    const char* end = p + json_len;

    Reader r(p, end);
    if (!r.ch('{')) return nullptr;

    auto* h = new safetensors_header();
    h->header_bytes = (long long)(8 + json_len);

    if (r.peek('}')) { r.ch('}'); return h; }  /* empty header is structurally valid */

    while (true) {
        std::string key;
        if (!r.string_(key)) { delete h; return nullptr; }
        if (!r.ch(':')) { delete h; return nullptr; }

        if (key == "__metadata__") {
            h->has_metadata = true;
            if (!r.skip_value()) { delete h; return nullptr; }
        } else {
            if (!r.ch('{')) { delete h; return nullptr; }

            Tensor t;
            t.name = key;
            bool have_dtype = false, have_shape = false, have_offsets = false;

            if (!r.peek('}')) {
                while (true) {
                    std::string field;
                    if (!r.string_(field)) { delete h; return nullptr; }
                    if (!r.ch(':')) { delete h; return nullptr; }

                    if (field == "dtype") {
                        if (!r.string_(t.dtype)) { delete h; return nullptr; }
                        have_dtype = true;
                    } else if (field == "shape") {
                        if (!r.ch('[')) { delete h; return nullptr; }
                        if (!r.peek(']')) {
                            while (true) {
                                long long d;
                                if (!r.integer(d)) { delete h; return nullptr; }
                                t.shape.push_back(d);
                                if (r.ch(',')) continue;
                                break;
                            }
                        }
                        if (!r.ch(']')) { delete h; return nullptr; }
                        have_shape = true;
                    } else if (field == "data_offsets") {
                        if (!r.ch('[')) { delete h; return nullptr; }
                        if (!r.integer(t.data_start)) { delete h; return nullptr; }
                        if (!r.ch(',')) { delete h; return nullptr; }
                        if (!r.integer(t.data_end)) { delete h; return nullptr; }
                        if (!r.ch(']')) { delete h; return nullptr; }
                        have_offsets = true;
                    } else {
                        if (!r.skip_value()) { delete h; return nullptr; }
                    }

                    if (r.ch(',')) continue;
                    break;
                }
            }
            if (!r.ch('}')) { delete h; return nullptr; }

            /* Every field is required: a tensor entry that does not say what it is,
             * what shape it is, or where its bytes live cannot be read safely. */
            if (!have_dtype || !have_shape || !have_offsets) { delete h; return nullptr; }
            if (t.data_start < 0 || t.data_end < t.data_start) { delete h; return nullptr; }
            for (long long d : t.shape) if (d < 0) { delete h; return nullptr; }

            h->tensors.push_back(std::move(t));
        }

        if (r.ch(',')) continue;
        break;
    }

    if (!r.ch('}')) { delete h; return nullptr; }

    /* On-disk order, so index order is a forward read. */
    std::stable_sort(h->tensors.begin(), h->tensors.end(),
                     [](const Tensor& a, const Tensor& b) { return a.data_start < b.data_start; });
    return h;
}

extern "C" long long safetensors_header_bytes(const safetensors_header_t* h) {
    return h ? h->header_bytes : -1;
}

extern "C" int safetensors_tensor_count(const safetensors_header_t* h) {
    return h ? (int)h->tensors.size() : -1;
}

extern "C" int safetensors_has_metadata(const safetensors_header_t* h) {
    return (h && h->has_metadata) ? 1 : 0;
}

static bool in_range(const safetensors_header_t* h, int i) {
    return h && i >= 0 && (size_t)i < h->tensors.size();
}

extern "C" const char* safetensors_tensor_name(const safetensors_header_t* h, int index) {
    return in_range(h, index) ? h->tensors[(size_t)index].name.c_str() : nullptr;
}

extern "C" const char* safetensors_tensor_dtype(const safetensors_header_t* h, int index) {
    return in_range(h, index) ? h->tensors[(size_t)index].dtype.c_str() : nullptr;
}

extern "C" int safetensors_tensor_rank(const safetensors_header_t* h, int index) {
    return in_range(h, index) ? (int)h->tensors[(size_t)index].shape.size() : -1;
}

extern "C" long long safetensors_tensor_dim(const safetensors_header_t* h, int index, int axis) {
    if (!in_range(h, index)) return -1;
    const auto& s = h->tensors[(size_t)index].shape;
    if (axis < 0 || (size_t)axis >= s.size()) return -1;
    return s[(size_t)axis];
}

extern "C" long long safetensors_tensor_data_start(const safetensors_header_t* h, int index) {
    return in_range(h, index) ? h->tensors[(size_t)index].data_start : -1;
}

extern "C" long long safetensors_tensor_data_end(const safetensors_header_t* h, int index) {
    return in_range(h, index) ? h->tensors[(size_t)index].data_end : -1;
}

extern "C" void safetensors_header_free(safetensors_header_t* h) {
    delete h;
}
