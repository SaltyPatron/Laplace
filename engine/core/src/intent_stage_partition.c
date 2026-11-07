#include "intent_stage_internal.h"

#include <stdlib.h>
#include <string.h>

static const uint8_t kCopyBinarySignature[11] = {
    'P', 'G', 'C', 'O', 'P', 'Y', '\n', 0xff, '\r', '\n', '\0'
};

static int import_tuples(byte_buf_t* out, const uint8_t* data, size_t bytes, uint16_t columns) {
    size_t offset = 0u, rows = 0u;
    if (bytes != 0u && data == NULL) return -1;
    while (offset < bytes) {
        if (bytes - offset < 2u || ((uint16_t)((uint16_t)data[offset] << 8u) |
                data[offset + 1u]) != columns) return -1;
        offset += 2u;
        for (size_t field = 0; field < columns; ++field) {
            uint32_t length;
            if (bytes - offset < 4u) return -1;
            length = laplace_intent_be32_at(data + offset);
            offset += 4u;
            if (length != UINT32_MAX) {
                if (length > INT32_MAX || length > bytes - offset) return -1;
                offset += length;
            }
        }
        ++rows;
    }
    if (rows > SIZE_MAX - out->row_count) return -1;
    if (bytes != 0u && laplace_intent_buf_append(out, data, bytes) != 0) return -2;
    out->row_count += rows;
    return 0;
}

int intent_stage_from_tuple_bytes(
    const uint8_t* entities, size_t entity_bytes,
    const uint8_t* physicalities, size_t physicality_bytes,
    const uint8_t* attestations, size_t attestation_bytes,
    size_t maximum_bytes, intent_stage_t** out_stage) {
    int status;
    intent_stage_t* stage;
    if (out_stage == NULL) return -1;
    *out_stage = NULL;
    stage = intent_stage_new_bounded(0u, maximum_bytes);
    if (stage == NULL) return -2;
    status = import_tuples(&stage->entities, entities, entity_bytes, ENTITY_COL_COUNT);
    if (status == 0)
        status = import_tuples(&stage->physicalities, physicalities, physicality_bytes, PHYSICALITY_COL_COUNT);
    if (status == 0)
        status = import_tuples(&stage->attestations, attestations, attestation_bytes, ATTESTATION_COL_COUNT);
    if (status != 0) {
        intent_stage_free(stage);
        return status;
    }
    *out_stage = stage;
    return 0;
}

size_t laplace_intent_row_byte_len(const uint8_t* src, size_t len, size_t off) {
    if (off + 2 > len) return 0;
    uint16_t cols = (uint16_t)((src[off] << 8) | src[off + 1]);
    size_t p = off + 2;
    for (uint16_t c = 0; c < cols; ++c) {
        if (p + 4 > len) return 0;
        int32_t flen = (int32_t)laplace_intent_be32_at(src + p);
        p += 4;
        if (flen < 0) continue;
        if (p + (size_t)flen > len) return 0;
        p += (size_t)flen;
    }
    return p - off;
}

int laplace_intent_row_field_at(const uint8_t* data, size_t len, size_t off, int field_1based,
                                const uint8_t** out, int32_t* out_len) {
    if (off + 2 > len) return -1;
    uint16_t cols = (uint16_t)((data[off] << 8) | data[off + 1]);
    size_t p = off + 2;
    for (uint16_t c = 0; c < cols; ++c) {
        if (p + 4 > len) return -1;
        int32_t flen = (int32_t)laplace_intent_be32_at(data + p);
        p += 4;
        if ((int)(c + 1) == field_1based) {
            if (flen < 0) return -1;
            if (p + (size_t)flen > len) return -1;
            *out = data + p;
            *out_len = flen;
            return 0;
        }
        if (flen < 0) continue;
        p += (size_t)flen;
    }
    return -1;
}

static size_t partition_row(const uint8_t* data, size_t len, size_t off, size_t part_count,
                            int table_kind) {
    int field_1based = (table_kind == 0) ? 1 : 2;

    const uint8_t* fld;
    int32_t fld_len;
    if (laplace_intent_row_field_at(data, len, off, field_1based, &fld, &fld_len) == 0 && fld_len >= 16) {
        uint64_t lo;
        memcpy(&lo, fld + 8, 8);
        return (size_t)(lo % (uint64_t)part_count);
    }

    size_t id_off = off + 2 + 4;
    uint64_t lo;
    memcpy(&lo, data + id_off + 8, 8);
    return (size_t)(lo % (uint64_t)part_count);
}

static int partition_one_buf(
    const byte_buf_t* src,
    byte_buf_t*       outs,
    size_t            part_count,
    int               table_kind) {
    const uint8_t* data = src->data;
    const size_t   len  = src->len;
    size_t off = 0;
    for (size_t r = 0; r < src->row_count; ++r) {
        size_t rlen = laplace_intent_row_byte_len(data, len, off);
        if (rlen == 0) return -1;
        size_t part = partition_row(data, len, off, part_count, table_kind);
        if (laplace_intent_buf_append(&outs[part], data + off, rlen) != 0) return -1;
        outs[part].row_count++;
        off += rlen;
    }
    return 0;
}

typedef struct {
    size_t         off;
    size_t         len;
    const uint8_t* hilbert;
} phys_row_span_t;

static int cmp_phys_row_span_hilbert(const void* a, const void* b) {
    const phys_row_span_t* ra = (const phys_row_span_t*)a;
    const phys_row_span_t* rb = (const phys_row_span_t*)b;
    return memcmp(ra->hilbert, rb->hilbert, 16);
}

static int physicalities_sort_by_hilbert(byte_buf_t* buf) {
    if (buf->row_count <= 1) return 0;

    phys_row_span_t* spans = (phys_row_span_t*)malloc(buf->row_count * sizeof(phys_row_span_t));
    if (!spans) return -1;

    size_t off = 0;
    for (size_t r = 0; r < buf->row_count; ++r) {
        size_t rlen = laplace_intent_row_byte_len(buf->data, buf->len, off);
        if (rlen == 0) { free(spans); return -1; }
        const uint8_t* hb;
        int32_t hb_len;
        if (laplace_intent_row_field_at(buf->data, buf->len, off, 5, &hb, &hb_len) != 0 || hb_len < 16) {
            free(spans);
            return -1;
        }
        spans[r].off = off;
        spans[r].len = rlen;
        spans[r].hilbert = hb;
        off += rlen;
    }

    qsort(spans, buf->row_count, sizeof(phys_row_span_t), cmp_phys_row_span_hilbert);

    uint8_t* sorted = (uint8_t*)malloc(buf->len > 0 ? buf->len : 1);
    if (!sorted) { free(spans); return -1; }
    size_t w = 0;
    for (size_t r = 0; r < buf->row_count; ++r) {
        memcpy(sorted + w, buf->data + spans[r].off, spans[r].len);
        w += spans[r].len;
    }
    memcpy(buf->data, sorted, buf->len);
    free(sorted);
    free(spans);
    return 0;
}

int intent_stage_partition(
    const intent_stage_t* src,
    size_t                part_count,
    intent_stage_t**      out_parts) {
    if (!src || !out_parts || part_count == 0) return -1;

    intent_stage_t** parts = out_parts;
    for (size_t i = 0; i < part_count; ++i) parts[i] = NULL;

    for (size_t i = 0; i < part_count; ++i) {
        parts[i] = intent_stage_new(0);
        if (!parts[i]) {
            for (size_t j = 0; j < i; ++j) { intent_stage_free(parts[j]); parts[j] = NULL; }
            return -1;
        }
    }

    for (size_t t = 0; t < 3; ++t) {
        const byte_buf_t* sb;
        switch (t) {
            case 0: sb = &src->entities;      break;
            case 1: sb = &src->physicalities; break;
            default: sb = &src->attestations; break;
        }
        if (sb->row_count == 0) continue;
        byte_buf_t* tbl_outs = (byte_buf_t*)malloc(part_count * sizeof(byte_buf_t));
        if (!tbl_outs) {
            for (size_t i = 0; i < part_count; ++i) { intent_stage_free(parts[i]); parts[i] = NULL; }
            return -1;
        }
        for (size_t i = 0; i < part_count; ++i) {
            switch (t) {
                case 0: tbl_outs[i] = parts[i]->entities;      break;
                case 1: tbl_outs[i] = parts[i]->physicalities; break;
                default: tbl_outs[i] = parts[i]->attestations; break;
            }
        }
        int rc = partition_one_buf(sb, tbl_outs, part_count, (int)t);
        for (size_t i = 0; i < part_count; ++i) {
            switch (t) {
                case 0: parts[i]->entities      = tbl_outs[i]; break;
                case 1: parts[i]->physicalities = tbl_outs[i]; break;
                default: parts[i]->attestations = tbl_outs[i]; break;
            }
        }
        free(tbl_outs);
        if (rc != 0) {
            for (size_t i = 0; i < part_count; ++i) { intent_stage_free(parts[i]); parts[i] = NULL; }
            return -1;
        }

        if (t == 1) {
            for (size_t i = 0; i < part_count; ++i) {
                if (physicalities_sort_by_hilbert(&parts[i]->physicalities) != 0) {
                    for (size_t j = 0; j < part_count; ++j) { intent_stage_free(parts[j]); parts[j] = NULL; }
                    return -1;
                }
            }
        }
    }
    return 0;
}

size_t intent_stage_emit_copy_binary(
    const intent_stage_t* stage,
    intent_stage_table_t  table,
    uint8_t*              buf,
    size_t                buf_capacity) {
    const byte_buf_t* src = laplace_intent_stage_buf(stage, table);
    if (!src) return 0;
    const size_t required = (size_t)INTENT_STAGE_HEADER_BYTES + src->len + (size_t)INTENT_STAGE_TRAILER_BYTES;
    if (!buf || buf_capacity < required) return required;

    size_t off = 0;
    memcpy(buf + off, kCopyBinarySignature, sizeof(kCopyBinarySignature));
    off += sizeof(kCopyBinarySignature);
    memset(buf + off, 0, 4); off += 4;
    memset(buf + off, 0, 4); off += 4;
    if (src->len > 0) {
        memcpy(buf + off, src->data, src->len);
        off += src->len;
    }
    buf[off++] = 0xff;
    buf[off++] = 0xff;
    return required;
}

const uint8_t* intent_stage_tuple_ptr(
    const intent_stage_t* stage,
    intent_stage_table_t  table,
    size_t*               out_len) {
    const byte_buf_t* src = laplace_intent_stage_buf(stage, table);
    if (!src) { if (out_len) *out_len = 0; return NULL; }
    if (out_len) *out_len = src->len;
    return src->data;
}

static int semantic_hash_compare(const void* a, const void* b) {
    return memcmp(a, b, sizeof(hash128_t));
}

int intent_stage_semantic_digest_batch(const intent_stage_t* const* stages,
                                      size_t count, hash128_t* out) {
    if (!out || (count && !stages)) return -1;
    size_t rows = 0;
    for (size_t s = 0; s < count; ++s) {
        if (!stages[s]) return -1;
        for (int t = 1; t <= 3; ++t) {
            const byte_buf_t* b = laplace_intent_stage_buf(stages[s], (intent_stage_table_t)t);
            if (b->row_count > SIZE_MAX - rows) return -1;
            rows += b->row_count;
        }
    }
    if (rows > (SIZE_MAX - 8) / sizeof(hash128_t)) return -1;
    uint8_t* hashes = (uint8_t*)malloc(8 + rows * sizeof(hash128_t));
    if (!hashes) return -1;
    memcpy(hashes, "LPISv001", 8);
    byte_buf_t row = {0};
    size_t at = 0;
    int rc = -1;
    for (size_t s = 0; s < count; ++s) {
        for (int t = 1; t <= 3; ++t) {
            const byte_buf_t* b = laplace_intent_stage_buf(stages[s], (intent_stage_table_t)t);
            int fields = t == 1 ? ENTITY_COL_COUNT :
                         t == 2 ? PHYSICALITY_COL_COUNT : ATTESTATION_COL_COUNT;
            int omitted = t == 2 ? 10 : t == 3 ? 8 : 0;
            size_t offset = 0;
            for (size_t n = 0; n < b->row_count; ++n) {
                size_t len = laplace_intent_row_byte_len(b->data, b->len, offset);
                if (!len || at >= rows) goto done;
                row.len = 0;
                if (laplace_intent_buf_append_u8(&row, (uint8_t)t)) goto done;
                size_t field_offset = offset + 2;
                for (int f = 1; f <= fields; ++f) {
                    if (field_offset + 4 > offset + len) goto done;
                    int32_t length = (int32_t)laplace_intent_be32_at(b->data + field_offset);
                    field_offset += 4;
                    if (length < -1 || (length > 0 && (size_t)length > offset + len - field_offset)) goto done;
                    if (f != omitted) {
                        if (laplace_intent_buf_append_u8(&row, (uint8_t)f) || laplace_intent_buf_append_be32(&row, length)) goto done;
                        if (length > 0 && laplace_intent_buf_append(&row, b->data + field_offset, (size_t)length)) goto done;
                    }
                    if (length > 0) field_offset += (size_t)length;
                }
                if (field_offset != offset + len) goto done;
                hash128_t digest;
                hash128_blake3(row.data, row.len, &digest);
                memcpy(hashes + 8 + at++ * sizeof(digest), &digest, sizeof(digest));
                offset += len;
            }
            if (offset != b->len) goto done;
        }
    }
    if (rows > 1) qsort(hashes + 8, rows, sizeof(hash128_t), semantic_hash_compare);
    hash128_blake3(hashes, 8 + rows * sizeof(hash128_t), out);
    rc = 0;
done:
    free(row.data);
    free(hashes);
    return rc;
}

int intent_stage_semantic_digest(const intent_stage_t* stage, hash128_t* out) {
    return intent_stage_semantic_digest_batch(&stage, 1, out);
}
