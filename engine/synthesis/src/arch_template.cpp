#include "laplace/synthesis/arch_template.h"
#include "laplace/synthesis/recipe.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#ifdef LAPLACE_HAS_MKL
#  include <mkl_cblas.h>
#endif

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

namespace {

/* Helper: write a float to bytes per dtype (0=f32, 2=bf16). */
inline void write_dtype(float v, int dtype, uint8_t* dst) {
    if (dtype == 0) {
        std::memcpy(dst, &v, 4);
    } else { // bf16 (truncate to upper 16 bits)
        uint32_t bits;
        std::memcpy(&bits, &v, 4);
        uint16_t bf = (uint16_t)(bits >> 16);
        std::memcpy(dst, &bf, 2);
    }
}

inline size_t dtype_elem_size(int dtype) {
    return dtype == 0 ? 4 : 2;
}

/* Materialize a per-token-axis tensor (shape [vocab, hidden]) by broadcasting
 * per-token consensus through the token basis (spectral embedding). Each
 * output cell out[t, d] = per_token_consensus[t] * basis[t, d]. If basis is
 * NULL, falls back to uniform broadcast (per_token_consensus[t] / sqrt(hidden)). */
void materialize_token_axis(const substrate_view_t* v, int dtype,
                            size_t vocab, size_t hidden, uint8_t* out) {
    size_t es = dtype_elem_size(dtype);
    double inv_sqrt_h = 1.0 / std::sqrt((double)hidden);
    for (size_t t = 0; t < vocab; ++t) {
        double tc = (t < v->vocab) ? v->per_token_consensus[t] : 0.0;
        for (size_t d = 0; d < hidden; ++d) {
            double b = (v->token_basis && v->basis_dim >= hidden)
                       ? v->token_basis[t * v->basis_dim + d]
                       : inv_sqrt_h;
            write_dtype((float)(tc * b), dtype, out + (t * hidden + d) * es);
        }
    }
}

/* Materialize an interior tensor [out_dim × in_dim] for a unary kind
 * (V_PROJECTS, O_PROJECTS, GATES, UP_PROJECTS, DOWN_PROJECTS).
 *
 * With gram: out[o, i] = unary_gram[o % basis_dim, i % basis_dim] / N
 *   where N = sqrt(out_dim * in_dim).  The gram is E^T · diag(perToken) · E
 *   precomputed once; any shape is handled by cycling mod basis_dim.
 *
 * Without gram (basis not yet computed): constant fill from aggregate. */
void materialize_interior_uniform(const substrate_view_t* v, int dtype,
                                   size_t out_dim, size_t in_dim, uint8_t* out) {
    size_t es = dtype_elem_size(dtype);
    if (v->unary_gram && v->basis_dim > 0) {
        double N = std::sqrt((double)(out_dim * in_dim));
        for (size_t o = 0; o < out_dim; ++o) {
            size_t bo = o % v->basis_dim;
            for (size_t i = 0; i < in_dim; ++i) {
                size_t bi = i % v->basis_dim;
                double val = v->unary_gram[bo * v->basis_dim + bi] / N;
                write_dtype((float)val, dtype, out + (o * in_dim + i) * es);
            }
        }
    } else {
        double total = 0.0;
        for (size_t t = 0; t < v->vocab; ++t) total += v->per_token_consensus[t];
        double avg = (v->vocab > 0) ? total / (double)v->vocab : 0.0;
        double per_cell = avg / std::sqrt((double)(out_dim * in_dim));
        for (size_t cell = 0; cell < out_dim * in_dim; ++cell)
            write_dtype((float)per_cell, dtype, out + cell * es);
    }
}

/* Materialize a Q/K-style binary interior tensor [out_dim × in_dim].
 *
 * With gram: out[o, i] = binary_gram[o % basis_dim, i % basis_dim] / N
 *   where the gram is E^T · S_qk · E, S_qk = sparse Q_PROJECTS adjacency.
 *
 * Without gram: constant fill from mean(|per_pair_vals|). */
void materialize_interior_binary_uniform(const substrate_view_t* v, int dtype,
                                          size_t out_dim, size_t in_dim, uint8_t* out) {
    size_t es = dtype_elem_size(dtype);
    if (v->binary_gram && v->basis_dim > 0) {
        double N = std::sqrt((double)(out_dim * in_dim));
        for (size_t o = 0; o < out_dim; ++o) {
            size_t bo = o % v->basis_dim;
            for (size_t i = 0; i < in_dim; ++i) {
                size_t bi = i % v->basis_dim;
                double val = v->binary_gram[bo * v->basis_dim + bi] / N;
                write_dtype((float)val, dtype, out + (o * in_dim + i) * es);
            }
        }
    } else {
        double total = 0.0;
        for (size_t e = 0; e < v->per_pair_nnz; ++e) total += std::abs(v->per_pair_vals[e]);
        double mass = (v->per_pair_nnz > 0) ? total / (double)v->per_pair_nnz : 0.0;
        double per_cell = mass / std::sqrt((double)(out_dim * in_dim));
        for (size_t cell = 0; cell < out_dim * in_dim; ++cell)
            write_dtype((float)per_cell, dtype, out + cell * es);
    }
}

/* Materialize a norm-weight tensor (shape [hidden]) via the norm_aggregate
 * value broadcast per-dim. RMSNorm convention: identity 1.0 plus
 * norm_aggregate-derived perturbation. */
void materialize_norm(const substrate_view_t* v, int dtype,
                       size_t hidden, uint8_t* out) {
    /* Avoid degenerate-zero norm weights; if substrate has no NORMALIZES
     * data, default to identity 1.0 (RMSNorm pass-through). */
    double scale = (v->norm_aggregate > 0.0)
                   ? std::min(2.0, std::max(0.5, v->norm_aggregate))
                   : 1.0;
    size_t es = dtype_elem_size(dtype);
    for (size_t d = 0; d < hidden; ++d) {
        write_dtype((float)scale, dtype, out + d * es);
    }
}

bool name_ends_with(const std::string& s, const std::string& suffix) {
    return s.size() >= suffix.size()
        && s.compare(s.size() - suffix.size(), suffix.size(), suffix) == 0;
}

} // namespace

extern "C"
int arch_template_materialize_tensor(const arch_template_t*  tmpl,
                                     const tensor_spec_t*    spec,
                                     const substrate_view_t* view,
                                     void*                   out_values) {
    if (!tmpl || !spec || !view || !out_values) return -1;
    if (spec->rank == 0 || spec->rank > 2) return -2;

    std::string name = spec->name ? std::string(spec->name) : std::string();
    uint8_t* out = static_cast<uint8_t*>(out_values);

    // Token-axis tensors: shape [vocab, hidden]
    if (name == "model.embed_tokens.weight" || name == "lm_head.weight") {
        if (spec->rank != 2) return -2;
        return materialize_token_axis(view, spec->dtype,
            spec->shape[0], spec->shape[1], out), 0;
    }

    // Norm weights: shape [hidden]
    if (name_ends_with(name, "_norm.weight") || name == "model.norm.weight") {
        if (spec->rank != 1) return -2;
        materialize_norm(view, spec->dtype, spec->shape[0], out);
        return 0;
    }

    // Layer interior tensors: shape [out_dim, in_dim]
    if (name.rfind("model.layers.", 0) == 0 && spec->rank == 2) {
        // Q_PROJECTS uses binary consensus; others use unary.
        bool is_qk = name_ends_with(name, ".self_attn.q_proj.weight")
                  || name_ends_with(name, ".self_attn.k_proj.weight");
        if (is_qk && view->per_pair_nnz > 0) {
            materialize_interior_binary_uniform(view, spec->dtype,
                spec->shape[0], spec->shape[1], out);
        } else {
            materialize_interior_uniform(view, spec->dtype,
                spec->shape[0], spec->shape[1], out);
        }
        return 0;
    }

    // Unknown tensor — zero-fill (R4 sparse-by-construction).
    size_t nelem = 1;
    for (size_t d = 0; d < spec->rank; ++d) nelem *= spec->shape[d];
    std::memset(out, 0, nelem * dtype_elem_size(spec->dtype));
    return 0;
}

extern "C"
int compute_substrate_gram(
    const double* token_basis,
    const double* per_token,
    std::size_t   vocab,
    std::size_t   basis_dim,
    const int*    qk_rows,
    const int*    qk_cols,
    const double* qk_vals,
    std::size_t   nnz,
    double*       unary_gram,
    double*       binary_gram)
{
    if (!token_basis || !per_token || !unary_gram || !binary_gram) return -1;

#ifdef LAPLACE_HAS_MKL
    const MKL_INT V  = static_cast<MKL_INT>(vocab);
    const MKL_INT D  = static_cast<MKL_INT>(basis_dim);

    /* ── unary_gram = E^T · diag(perToken) · E ──────────────────────────
     * Scale each row of E by sqrt(perToken[t]), then DGEMM: E_s^T · E_s.
     * E_s[t, d] = E[t, d] * sqrt(perToken[t]). */
    std::vector<double> E_scaled(vocab * basis_dim);
    for (std::size_t t = 0; t < vocab; ++t) {
        double s = std::sqrt(std::max(0.0, per_token[t]));
        for (std::size_t d = 0; d < basis_dim; ++d)
            E_scaled[t * basis_dim + d] = token_basis[t * basis_dim + d] * s;
    }
    /* unary_gram [D×D] = E_s^T [D×V] · E_s [V×D] */
    cblas_dgemm(CblasRowMajor, CblasTrans, CblasNoTrans,
                D, D, V,
                1.0, E_scaled.data(), D,
                     E_scaled.data(), D,
                0.0, unary_gram, D);

    /* ── binary_gram = E^T · S_qk · E ───────────────────────────────────
     * Step 1: SB[t, d] = sum_{j: S[t,j] present} qk_vals[e] * E[j, d]
     *         (sparse-dense row-scatter, S is the Q_PROJECTS COO adj).
     * Step 2: binary_gram [D×D] = E^T [D×V] · SB [V×D] via DGEMM. */
    std::vector<double> SB(vocab * basis_dim, 0.0);
    if (qk_rows && qk_cols && qk_vals) {
        for (std::size_t e = 0; e < nnz; ++e) {
            int r = qk_rows[e], c = qk_cols[e];
            if (r < 0 || c < 0 || (std::size_t)r >= vocab || (std::size_t)c >= vocab)
                continue;
            double w = qk_vals[e];
            const double* Ec = token_basis + (std::size_t)c * basis_dim;
            double*       Br = SB.data()   + (std::size_t)r * basis_dim;
            for (std::size_t d = 0; d < basis_dim; ++d)
                Br[d] += w * Ec[d];
        }
    }
    cblas_dgemm(CblasRowMajor, CblasTrans, CblasNoTrans,
                D, D, V,
                1.0, token_basis, D,
                     SB.data(),   D,
                0.0, binary_gram, D);

    return 0;
#else
    (void)token_basis; (void)per_token; (void)vocab; (void)basis_dim;
    (void)qk_rows; (void)qk_cols; (void)qk_vals; (void)nnz;
    return -2;
#endif
}
