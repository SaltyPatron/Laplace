#pragma once

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Architecture template — per-architecture knowledge of how to
 * materialize tensors from substrate state (per ADR 0011 plugin
 * interface IArchitectureTemplate + DESIGN.md VI).
 *
 * Substrate-specific invention (no upstream provides this) — opaque
 * handle, internal C++ implementation per template. Initial templates:
 *   - LlamaTemplate (Chunk 7 Story 7.2; handles Llama / Qwen family)
 *
 * Per RULES.md R10: adding a new architecture = one new template
 * plugin; never touches schema/query/synthesis core. */
typedef struct arch_template arch_template_t;

/* Load an architecture template by name (e.g., "llama", "mamba",
 * "diffusion"). Returns NULL if no template registered with that name. */
arch_template_t* arch_template_load(const char* template_name);

/* Tensor specification (shape + dtype + role) returned by the template
 * when asked what tensors a recipe requires. Substrate-specific
 * concept — no upstream equivalent. */
typedef struct {
    const char* name;        /* canonical tensor name in the architecture */
    size_t      rank;        /* number of dimensions */
    size_t      shape[8];    /* up to 8 dims supported initially */
    int         dtype;       /* enum: 0=fp32, 1=fp16, 2=bf16, 3=q8, 4=q4, ... */
} tensor_spec_t;

/* Get the list of tensors this template needs to materialize for a
 * given recipe. Caller allocates `out_specs` array; returns count
 * written, or -1 if `cap` too small. */
int arch_template_required_tensors(const arch_template_t* tmpl,
                                   const void*            recipe,  /* recipe_t* */
                                   tensor_spec_t*         out_specs,
                                   size_t                 cap);

void arch_template_free(arch_template_t* t);

#ifdef __cplusplus
}
#endif
