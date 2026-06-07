#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct format_writer format_writer_t;

format_writer_t* format_writer_create(const char* format, const char* output_dir_path);

int format_writer_add_tensor(format_writer_t* w,
                             const char*      name,
                             int              dtype,
                             const size_t*    shape,
                             size_t           rank,
                             const void*      data,
                             size_t           data_len);

int format_writer_set_config(format_writer_t* w, const char* config_json, size_t len);

int format_writer_set_tokenizer(format_writer_t* w, const char* tokenizer_json, size_t len);

int format_writer_finalize(format_writer_t* w);

void format_writer_free(format_writer_t* w);

#ifdef __cplusplus
}
#endif
