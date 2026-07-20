#include "laplace/synthesis/sentencepiece_parser.h"

#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

namespace {

struct Piece {
    std::string text;
    float score = 0.0f;
    int type = 1;  /* protobuf default: NORMAL */
};

/* Protobuf wire types actually present in a ModelProto. */
constexpr int kVarint = 0;
constexpr int kFixed64 = 1;
constexpr int kLenDelim = 2;
constexpr int kFixed32 = 5;

bool read_varint(const unsigned char* d, size_t len, size_t& pos, uint64_t& out) {
    uint64_t v = 0;
    int shift = 0;
    while (pos < len) {
        unsigned char b = d[pos++];
        v |= (uint64_t)(b & 0x7F) << shift;
        if ((b & 0x80) == 0) { out = v; return true; }
        shift += 7;
        if (shift > 63) return false;  /* overlong varint */
    }
    return false;  /* ran off the end mid-varint */
}

bool skip_field(const unsigned char* d, size_t len, size_t& pos, int wire) {
    switch (wire) {
        case kVarint: { uint64_t t; return read_varint(d, len, pos, t); }
        case kFixed64: if (pos + 8 > len) return false; pos += 8; return true;
        case kFixed32: if (pos + 4 > len) return false; pos += 4; return true;
        case kLenDelim: {
            uint64_t l;
            if (!read_varint(d, len, pos, l)) return false;
            if (l > len - pos) return false;
            pos += (size_t)l;
            return true;
        }
        default: return false;  /* groups (3/4) are not valid here */
    }
}

void put_varint(std::vector<unsigned char>& out, uint64_t v) {
    while (v >= 0x80) { out.push_back((unsigned char)(v | 0x80u)); v >>= 7; }
    out.push_back((unsigned char)v);
}

void put_tag(std::vector<unsigned char>& out, int field, int wire) {
    put_varint(out, ((uint64_t)(uint32_t)field << 3) | (uint32_t)wire);
}

}  // namespace

struct sp_model {
    std::vector<Piece> pieces;
};

extern "C" sp_model_t* sp_model_parse(const void* bytes, size_t len) {
    if (!bytes) return nullptr;

    const unsigned char* d = (const unsigned char*)bytes;
    auto* m = new sp_model();
    size_t pos = 0;

    while (pos < len) {
        uint64_t key;
        if (!read_varint(d, len, pos, key)) { delete m; return nullptr; }
        int field = (int)(key >> 3);
        int wire = (int)(key & 7);

        if (field == 1 && wire == kLenDelim) {
            uint64_t sub_len;
            if (!read_varint(d, len, pos, sub_len)) { delete m; return nullptr; }
            if (sub_len > len - pos) { delete m; return nullptr; }

            size_t end = pos + (size_t)sub_len;
            Piece p;

            while (pos < end) {
                uint64_t k2;
                if (!read_varint(d, end, pos, k2)) { delete m; return nullptr; }
                int f2 = (int)(k2 >> 3);
                int w2 = (int)(k2 & 7);

                if (f2 == 1 && w2 == kLenDelim) {
                    uint64_t l;
                    if (!read_varint(d, end, pos, l)) { delete m; return nullptr; }
                    if (l > end - pos) { delete m; return nullptr; }
                    /* verbatim bytes: the vocabulary is full of U+2581 and friends */
                    p.text.assign((const char*)d + pos, (size_t)l);
                    pos += (size_t)l;
                } else if (f2 == 2 && w2 == kFixed32) {
                    if (pos + 4 > end) { delete m; return nullptr; }
                    uint32_t bits;
                    std::memcpy(&bits, d + pos, 4);
                    std::memcpy(&p.score, &bits, 4);
                    pos += 4;
                } else if (f2 == 3 && w2 == kVarint) {
                    uint64_t t;
                    if (!read_varint(d, end, pos, t)) { delete m; return nullptr; }
                    p.type = (int)t;
                } else {
                    if (!skip_field(d, end, pos, w2)) { delete m; return nullptr; }
                }
            }

            m->pieces.push_back(std::move(p));
            pos = end;
        } else {
            /* trainer_spec, normalizer_spec, self_test_data, denormalizer_spec, ... */
            if (!skip_field(d, len, pos, wire)) { delete m; return nullptr; }
        }
    }

    return m;
}

extern "C" int sp_model_piece_count(const sp_model_t* m) {
    return m ? (int)m->pieces.size() : -1;
}

static bool in_range(const sp_model_t* m, int i) {
    return m && i >= 0 && (size_t)i < m->pieces.size();
}

extern "C" const char* sp_model_piece(const sp_model_t* m, int index, size_t* out_len) {
    if (!in_range(m, index)) {
        if (out_len) *out_len = 0;
        return nullptr;
    }
    const std::string& s = m->pieces[(size_t)index].text;
    if (out_len) *out_len = s.size();
    return s.c_str();
}

extern "C" float sp_model_score(const sp_model_t* m, int index) {
    return in_range(m, index) ? m->pieces[(size_t)index].score : 0.0f;
}

extern "C" int sp_model_type(const sp_model_t* m, int index) {
    return in_range(m, index) ? m->pieces[(size_t)index].type : -1;
}

extern "C" void sp_model_free(sp_model_t* m) {
    delete m;
}

extern "C" int sp_model_write(const char* const* pieces,
                              const size_t* piece_lens,
                              const float* scores,
                              const int* types,
                              int count,
                              unsigned char** out_buf,
                              size_t* out_len) {
    if (!out_buf || !out_len) return -1;
    if (count < 0) return -1;
    if (count > 0 && (!pieces || !piece_lens || !scores || !types)) return -1;

    std::vector<unsigned char> out;
    std::vector<unsigned char> inner;

    for (int i = 0; i < count; ++i) {
        if (!pieces[i] && piece_lens[i] != 0) return -1;

        inner.clear();
        put_tag(inner, 1, kLenDelim);
        put_varint(inner, (uint64_t)piece_lens[i]);
        inner.insert(inner.end(), (const unsigned char*)pieces[i],
                     (const unsigned char*)pieces[i] + piece_lens[i]);

        put_tag(inner, 2, kFixed32);
        uint32_t bits;
        std::memcpy(&bits, &scores[i], 4);
        for (int b = 0; b < 4; ++b) inner.push_back((unsigned char)((bits >> (8 * b)) & 0xFF));

        put_tag(inner, 3, kVarint);
        put_varint(inner, (uint64_t)(uint32_t)types[i]);

        put_tag(out, 1, kLenDelim);
        put_varint(out, (uint64_t)inner.size());
        out.insert(out.end(), inner.begin(), inner.end());
    }

    unsigned char* buf = (unsigned char*)std::malloc(out.empty() ? 1 : out.size());
    if (!buf) return -1;
    if (!out.empty()) std::memcpy(buf, out.data(), out.size());
    *out_buf = buf;
    *out_len = out.size();
    return 0;
}

extern "C" void sp_model_buffer_free(unsigned char* buf) {
    std::free(buf);
}
