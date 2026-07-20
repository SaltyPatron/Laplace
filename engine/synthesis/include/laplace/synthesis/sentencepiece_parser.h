/*
 * SentencePiece model (tokenizer.model) reader and writer.
 *
 * The file is a protobuf ModelProto. Only the piece list is load-bearing here:
 *   field 1 (repeated, wire 2) SentencePiece {
 *       field 1 (wire 2) string piece
 *       field 2 (wire 5) float  score
 *       field 3 (wire 0) varint type
 *   }
 * Every other field is skipped by wire type, so a model carrying trainer_spec,
 * normalizer_spec or future fields still reads.
 *
 * Read and write are inverses over the fields above: writing what was read
 * reproduces the same piece/score/type sequence. Piece bytes are carried verbatim —
 * a SentencePiece vocabulary is full of U+2581 and other non-ASCII, and re-encoding
 * it through a lossy path would change the token identity.
 */
#ifndef LAPLACE_SYNTHESIS_SENTENCEPIECE_PARSER_H
#define LAPLACE_SYNTHESIS_SENTENCEPIECE_PARSER_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct sp_model sp_model_t;

/* Parse a ModelProto buffer. Returns NULL on a null buffer or a malformed varint /
 * truncated length-delimited field — a tokenizer that cannot be read exactly must be
 * refused, since a wrong vocabulary silently changes every token id. */
sp_model_t* sp_model_parse(const void* bytes, size_t len);

int sp_model_piece_count(const sp_model_t* m);

/* Piece text as UTF-8. The returned pointer is valid until sp_model_free.
 * out_len receives the byte length (pieces may contain embedded multi-byte UTF-8). */
const char* sp_model_piece(const sp_model_t* m, int index, size_t* out_len);
float       sp_model_score(const sp_model_t* m, int index);
int         sp_model_type(const sp_model_t* m, int index);

void sp_model_free(sp_model_t* m);

/* Serialize pieces back to a ModelProto buffer.
 * pieces/scores/types are parallel arrays of `count` entries; piece_lens gives each
 * piece's byte length. On success returns 0 and sets *out_buf (free with
 * sp_model_buffer_free) and *out_len. Returns -1 on a null/invalid argument. */
int sp_model_write(const char* const* pieces,
                   const size_t* piece_lens,
                   const float* scores,
                   const int* types,
                   int count,
                   unsigned char** out_buf,
                   size_t* out_len);

void sp_model_buffer_free(unsigned char* buf);

#ifdef __cplusplus
}
#endif

#endif /* LAPLACE_SYNTHESIS_SENTENCEPIECE_PARSER_H */
