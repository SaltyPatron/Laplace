#include "laplace/synthesis/arch_template.h"
#include "laplace/synthesis/recipe.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

/* LlamaTemplate: tensor manifest for the Llama architecture family.
 *
 * Handles Llama 2/3, TinyLlama, Qwen-Llama, and other Llama-derived models.
 * Tensor names follow the HuggingFace safetensors convention used by all
 * Llama-family models. */

namespace {

struct TensorSpec {
    std::string name;
    int         dtype;
    std::vector<size_t> shape;
};

std::vector<TensorSpec> llama_tensor_manifest(const recipe_t* r) {
    std::vector<TensorSpec> specs;

    /* Extract recipe fields with safe defaults */
    auto get_int = [&](const char* field, size_t def) -> size_t {
        const char* v = recipe_get_field(r, field);
        if (!v) return def;
        char* end;
        long val = std::strtol(v, &end, 10);
        return (end != v && val > 0) ? (size_t)val : def;
    };

    const size_t vocab_size   = get_int("vocab_size",             32000);
    const size_t hidden_size  = get_int("hidden_size",             2048);
    const size_t n_layers     = get_int("num_hidden_layers",         22);
    const size_t n_heads      = get_int("num_attention_heads",       32);
    const size_t n_kv_heads   = get_int("num_key_value_heads",        4);
    const size_t interm_size  = get_int("intermediate_size",       5632);

    const size_t head_dim    = hidden_size / n_heads;
    const size_t kv_dim      = n_kv_heads * head_dim;

    /* Determine dtype from recipe (default BF16 = 2) */
    int dtype = 2; /* bf16 */
    const char* dt = recipe_get_field(r, "torch_dtype");
    if (dt) {
        if (std::strcmp(dt, "float32") == 0) dtype = 0;
        else if (std::strcmp(dt, "float16") == 0) dtype = 1;
        else if (std::strcmp(dt, "bfloat16") == 0) dtype = 2;
    }

    char name_buf[128];

    /* embed_tokens */
    specs.push_back({"model.embed_tokens.weight", dtype, {vocab_size, hidden_size}});

    /* Per-layer tensors */
    for (size_t i = 0; i < n_layers; ++i) {
        /* Attention projections */
        std::snprintf(name_buf, sizeof(name_buf),
                      "model.layers.%zu.self_attn.q_proj.weight", i);
        specs.push_back({name_buf, dtype, {hidden_size, hidden_size}});

        std::snprintf(name_buf, sizeof(name_buf),
                      "model.layers.%zu.self_attn.k_proj.weight", i);
        specs.push_back({name_buf, dtype, {kv_dim, hidden_size}});

        std::snprintf(name_buf, sizeof(name_buf),
                      "model.layers.%zu.self_attn.v_proj.weight", i);
        specs.push_back({name_buf, dtype, {kv_dim, hidden_size}});

        std::snprintf(name_buf, sizeof(name_buf),
                      "model.layers.%zu.self_attn.o_proj.weight", i);
        specs.push_back({name_buf, dtype, {hidden_size, hidden_size}});

        /* MLP projections */
        std::snprintf(name_buf, sizeof(name_buf),
                      "model.layers.%zu.mlp.gate_proj.weight", i);
        specs.push_back({name_buf, dtype, {interm_size, hidden_size}});

        std::snprintf(name_buf, sizeof(name_buf),
                      "model.layers.%zu.mlp.up_proj.weight", i);
        specs.push_back({name_buf, dtype, {interm_size, hidden_size}});

        std::snprintf(name_buf, sizeof(name_buf),
                      "model.layers.%zu.mlp.down_proj.weight", i);
        specs.push_back({name_buf, dtype, {hidden_size, interm_size}});

        /* Layer norms (1D, f32 always — RMSNorm scale is fp32 even in bf16 models) */
        std::snprintf(name_buf, sizeof(name_buf),
                      "model.layers.%zu.input_layernorm.weight", i);
        specs.push_back({name_buf, 0 /* f32 */, {hidden_size}});

        std::snprintf(name_buf, sizeof(name_buf),
                      "model.layers.%zu.post_attention_layernorm.weight", i);
        specs.push_back({name_buf, 0 /* f32 */, {hidden_size}});
    }

    /* Final norm */
    specs.push_back({"model.norm.weight", 0 /* f32 */, {hidden_size}});

    /* Language model head */
    specs.push_back({"lm_head.weight", dtype, {vocab_size, hidden_size}});

    return specs;
}

} /* namespace */

/* arch_template holds the cached tensor spec list (generated once per
 * arch_template_required_tensors call, owned here so tensor_spec_t.name
 * pointers remain valid for the lifetime of the template handle). */
struct arch_template {
    std::string              arch_name;
    std::vector<TensorSpec>  cached_specs;
    std::vector<tensor_spec_t> c_specs; /* C-ABI view into cached_specs */
};

extern "C" arch_template_t* arch_template_load(const char* template_name) {
    if (!template_name) return nullptr;
    if (std::strcmp(template_name, "llama") != 0) return nullptr; /* Only llama in v0.1 */
    auto* t = new arch_template();
    t->arch_name = "llama";
    return t;
}

extern "C"
int arch_template_required_tensors(const arch_template_t* tmpl,
                                   const void*            recipe,
                                   tensor_spec_t*         out_specs,
                                   size_t                 cap) {
    if (!tmpl || !recipe || !out_specs) return -1;
    const recipe_t* r = static_cast<const recipe_t*>(recipe);

    /* Generate (and cache) the spec list */
    auto* mut = const_cast<arch_template_t*>(tmpl);
    mut->cached_specs = llama_tensor_manifest(r);

    const size_t n = mut->cached_specs.size();
    if (cap < n) return (int)n; /* tell caller how big a buffer it needs */

    mut->c_specs.resize(n);
    for (size_t i = 0; i < n; ++i) {
        TensorSpec& ts = mut->cached_specs[i];
        tensor_spec_t& cs = mut->c_specs[i];
        cs.name  = ts.name.c_str();
        cs.rank  = ts.shape.size();
        cs.dtype = ts.dtype;
        std::memset(cs.shape, 0, sizeof(cs.shape));
        for (size_t d = 0; d < ts.shape.size() && d < 8; ++d)
            cs.shape[d] = ts.shape[d];
        out_specs[i] = cs;
    }
    return (int)n;
}

extern "C" void arch_template_free(arch_template_t* t) {
    delete t;
}
