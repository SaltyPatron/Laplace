#include "laplace/core/unicode_seed.h"
#include "laplace/core/text_identity.h"
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iterator>
#include <new>
#include <vector>
#include <zlib.h>

static bool read_zip_single_entry(const char* zip_path, std::vector<uint8_t>& out) {
    std::ifstream f(zip_path, std::ios::binary);
    if (!f) return false;
    std::vector<uint8_t> z((std::istreambuf_iterator<char>(f)),
                            std::istreambuf_iterator<char>());
    if (z.size() < 22) return false;

    auto rd16 = [&](size_t o) -> uint32_t {
        return (uint32_t)z[o] | ((uint32_t)z[o + 1] << 8);
    };
    auto rd32 = [&](size_t o) -> uint32_t {
        return (uint32_t)z[o] | ((uint32_t)z[o + 1] << 8)
             | ((uint32_t)z[o + 2] << 16) | ((uint32_t)z[o + 3] << 24);
    };

    size_t eocd = SIZE_MAX;
    size_t floor_off = (z.size() > 22 + 0xFFFFu) ? z.size() - (22 + 0xFFFFu) : 0;
    for (size_t i = z.size() - 21; i-- > floor_off; ) {
        if (rd32(i) == 0x06054b50u) { eocd = i; break; }
    }
    if (eocd == SIZE_MAX) return false;

    uint32_t n_entries = rd16(eocd + 10);
    uint32_t cd_off = rd32(eocd + 16);
    if (n_entries == 0 || (size_t)cd_off + 46 > z.size()) return false;

    size_t cd = cd_off;
    if (rd32(cd) != 0x02014b50u) return false;
    uint32_t method = rd16(cd + 10);
    uint32_t crc = rd32(cd + 16);
    uint32_t comp_size = rd32(cd + 20);
    uint32_t uncomp_size = rd32(cd + 24);
    uint32_t lh_off = rd32(cd + 42);

    if ((size_t)lh_off + 30 > z.size() || rd32(lh_off) != 0x04034b50u) return false;
    size_t data = (size_t)lh_off + 30 + rd16(lh_off + 26) + rd16(lh_off + 28);
    if (data + comp_size > z.size()) return false;

    if (method == 0) {
        if (comp_size != uncomp_size) return false;
        out.assign(z.begin() + data, z.begin() + data + comp_size);
    } else if (method == 8) {
        out.assign(uncomp_size, 0);
        z_stream s{};
        if (inflateInit2(&s, -MAX_WBITS) != Z_OK) return false;
        s.next_in = z.data() + data;
        s.avail_in = comp_size;
        s.next_out = out.data();
        s.avail_out = uncomp_size;
        int rc = inflate(&s, Z_FINISH);
        inflateEnd(&s);
        if (rc != Z_STREAM_END || s.total_out != uncomp_size) return false;
    } else {
        return false;
    }

    return crc32(0, out.data(), (uInt)out.size()) == crc;
}

static bool read_ucd_document(const char* path, std::vector<uint8_t>& document) {
    size_t n = std::strlen(path);
    if (n > 4 && std::strcmp(path + n - 4, ".zip") == 0)
        return read_zip_single_entry(path, document);

    std::ifstream input(path, std::ios::binary);
    if (!input) return false;
    document.assign(std::istreambuf_iterator<char>(input),
                    std::istreambuf_iterator<char>());
    return !document.empty();
}

struct laplace_unicode_seed_snapshot {
    const codepoint_entry_t* records = nullptr;
    std::vector<uint8_t> ucdxml;
};

extern "C" int laplace_unicode_seed_snapshot_open(
    const char* ucdxml_path,
    const char* ducet_path,
    laplace_unicode_seed_snapshot_t** out_snapshot) {
    if (!ucdxml_path || !ducet_path || !out_snapshot) return -1;
    *out_snapshot = nullptr;

    auto* snapshot = new (std::nothrow) laplace_unicode_seed_snapshot_t();
    if (!snapshot) return -4;
    try {
        snapshot->records = static_cast<const codepoint_entry_t*>(text_identity_scalar_floor());
        if (!snapshot->records) { delete snapshot; return -4; }
        if (!read_ucd_document(ucdxml_path, snapshot->ucdxml)) {
            delete snapshot;
            return -2;
        }
    } catch (...) {
        delete snapshot;
        return -4;
    }
    *out_snapshot = snapshot;
    return 0;
}

extern "C" void laplace_unicode_seed_snapshot_free(
    laplace_unicode_seed_snapshot_t* snapshot) {
    delete snapshot;
}

extern "C" size_t laplace_unicode_seed_snapshot_count(
    const laplace_unicode_seed_snapshot_t* snapshot) {
    return snapshot ? LAPLACE_CODEPOINT_COUNT : 0u;
}

extern "C" int laplace_unicode_seed_snapshot_xml_copy(
    const laplace_unicode_seed_snapshot_t* snapshot,
    size_t offset,
    uint8_t* destination,
    size_t destination_capacity,
    size_t* out_copied) {
    if (!snapshot || !out_copied || (!destination && destination_capacity != 0))
        return -1;
    *out_copied = 0;
    if (offset > snapshot->ucdxml.size()) return -1;
    size_t available = snapshot->ucdxml.size() - offset;
    size_t copied = std::min(available, destination_capacity);
    if (copied != 0)
        std::memcpy(destination, snapshot->ucdxml.data() + offset, copied);
    *out_copied = copied;
    return 0;
}

extern "C" int laplace_unicode_seed_snapshot_copy_record(
    const laplace_unicode_seed_snapshot_t* snapshot,
    size_t index,
    codepoint_entry_t* out_record) {
    if (!snapshot || !out_record || index >= LAPLACE_CODEPOINT_COUNT) return -1;
    *out_record = snapshot->records[index];
    return 0;
}
