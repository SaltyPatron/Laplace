#include "laplace/synthesis/arch_template.h"

#include <cstddef>

/* Real impl lands Chunk 7 Stories 7.1/7.2. Stubs satisfy linkage. */

struct arch_template {
    int _placeholder;
};

extern "C" arch_template_t* arch_template_load(const char* template_name) {
    (void)template_name;
    return nullptr;
}

extern "C"
int arch_template_required_tensors(const arch_template_t* tmpl,
                                   const void*            recipe,
                                   tensor_spec_t*         out_specs,
                                   size_t                 cap) {
    (void)tmpl; (void)recipe; (void)out_specs; (void)cap;
    return -1;
}

extern "C" void arch_template_free(arch_template_t* t) {
    delete t;
}
