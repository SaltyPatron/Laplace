#!/usr/bin/env python3
"""Codegen attestation law: manifest TOML -> relation_law.c/h, pos_law.c/h, seed SQL fragment."""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "engine/manifest"
OUT_CORE = ROOT / "engine/core"
OUT_SEED_FRAG = ROOT / "extension/laplace_substrate/sql/generated/21_seed_relation_types.sql.in"


def parse_simple_toml(path: Path) -> dict:
    """Minimal TOML parser for our manifest shape."""
    text = path.read_text(encoding="utf-8")
    data: dict = {
        "ranks": {},
        "relation": [],
        "alias": [],
        "dynamic": {},
        "upos": {},
        # Source tagsets in manifest order (order = native enum ABI, UPOS = 0).
        "tagsets": {},
    }
    section = None
    current: dict | None = None
    key_stack = []

    for raw in text.splitlines():
        line = raw.split("#", 1)[0].strip()
        if not line:
            continue
        if line.startswith("[") and line.endswith("]"):
            inner = line.strip("[]")
            if inner.startswith("relation"):
                section = "relation"
                current = {}
                data["relation"].append(current)
                continue
            if inner.startswith("alias"):
                section = "alias"
                current = {}
                data["alias"].append(current)
                continue
            if inner.startswith("dynamic."):
                section = "dynamic"
                current = {}
                data["dynamic"][inner.split(".", 1)[1]] = current
                continue
            if inner == "ranks":
                section = "ranks"
                current = None
            elif inner == "upos":
                section = "upos"
                current = data["upos"]
            else:
                # Any other bare [section] is a source tagset map (pos_tags.toml).
                section = inner
                current = data["tagsets"].setdefault(inner, {})
            continue
        if "=" not in line:
            continue
        k, v = [x.strip() for x in line.split("=", 1)]
        if v.startswith('"') and v.endswith('"'):
            val: object = v[1:-1]
        elif v == "null":
            val = None
        elif v in ("true", "false"):
            val = v == "true"
        elif v.startswith("[") and v.endswith("]"):
            val = re.findall(r'"([^"]*)"', v)
        else:
            try:
                val = float(v) if "." in v else int(v)
            except ValueError:
                val = v
        if section == "ranks":
            data["ranks"][k] = val
        elif section == "dynamic" and current is not None:
            if k.startswith('"') and k.endswith('"'):
                k = k[1:-1]
            current[k] = val
        elif current is not None:
            if k.startswith('"') and k.endswith('"'):
                k = k[1:-1]
            current[k] = val
    return data


def c_hash(hi: int, lo: int) -> str:
    return f"{{ .hi = 0x{hi:016x}ULL, .lo = 0x{lo:016x}ULL }}"


def emit_dynamic_resolvers(dynamic: dict, ranks: dict) -> str:
    """Codegen DEP_/EDEP_/FEAT_ dynamic family resolvers."""

    def rank_val(key: str) -> float:
        return float(ranks.get(key, 0.09))

    dep = dynamic.get("deprel", {})
    edep = dynamic.get("enhanced_deprel", {})
    feat = dynamic.get("feature", {})

    dep_rank = rank_val(dep.get("rank", "partitive"))
    edep_rank = rank_val(edep.get("rank", "partitive"))
    feat_rank = rank_val(feat.get("rank", "partitive"))

    sym_map = {
        "symmetric": "LAPLACE_REL_SYMMETRY_SYMMETRIC",
        "asymmetric": "LAPLACE_REL_SYMMETRY_ASYMMETRIC",
    }
    dep_sym = sym_map.get(dep.get("symmetry", "asymmetric"), "LAPLACE_REL_SYMMETRY_ASYMMETRIC")
    edep_sym = sym_map.get(edep.get("symmetry", "asymmetric"), "LAPLACE_REL_SYMMETRY_ASYMMETRIC")
    feat_sym = sym_map.get(feat.get("symmetry", "asymmetric"), "LAPLACE_REL_SYMMETRY_ASYMMETRIC")

    return f"""
static void dyn_trim(char* s) {{
    size_t n = strlen(s);
    while (n > 0 && (s[n - 1] == ' ' || s[n - 1] == '\\t' || s[n - 1] == '\\r' || s[n - 1] == '\\n'))
        s[--n] = '\\0';
    size_t i = 0;
    while (s[i] == ' ' || s[i] == '\\t' || s[i] == '\\r' || s[i] == '\\n')
        ++i;
    if (i > 0)
        memmove(s, s + i, strlen(s + i) + 1);
}}

static void dyn_lower(char* s) {{
    for (; *s; ++s) {{
        if (*s >= 'A' && *s <= 'Z')
            *s = (char)(*s + 32);
    }}
}}

static void dyn_upper(char* s) {{
    for (; *s; ++s) {{
        if (*s >= 'a' && *s <= 'z')
            *s = (char)(*s - 32);
    }}
}}

static int dyn_build_prefixed(
    const char* input,
    const char* prefix,
    char sep,
    const char* root_canon,
    int lowercase_input,
    char* out_canon,
    size_t out_canon_sz,
    char* out_parent_canon,
    size_t out_parent_sz) {{
    char norm[128];
    size_t i, j;
    if (!input || !prefix || !root_canon || !out_canon || !out_parent_canon)
        return -1;
    if (strlen(input) >= sizeof(norm))
        return -1;
    memcpy(norm, input, strlen(input) + 1);
    dyn_trim(norm);
    if (lowercase_input)
        dyn_lower(norm);
    if (norm[0] == '\\0')
        return -1;
    if (snprintf(out_canon, out_canon_sz, "%s", prefix) <= 0)
        return -1;
    j = strlen(out_canon);
    for (i = 0; norm[i] != '\\0' && j + 1 < out_canon_sz; ++i) {{
        char c = norm[i];
        if (c == sep)
            c = '_';
        out_canon[j++] = c;
    }}
    out_canon[j] = '\\0';
    dyn_upper(out_canon + strlen(prefix));
    if (sep) {{
        const char* colon = strchr(norm, sep);
        if (colon && colon > norm) {{
            char parent_body[96];
            size_t plen = (size_t)(colon - norm);
            if (plen >= sizeof(parent_body))
                return -1;
            memcpy(parent_body, norm, plen);
            parent_body[plen] = '\\0';
            if (snprintf(out_parent_canon, out_parent_sz, "%s%s", prefix, parent_body) <= 0)
                return -1;
            dyn_upper(out_parent_canon + strlen(prefix));
        }} else if (snprintf(out_parent_canon, out_parent_sz, "%s", root_canon) <= 0) {{
            return -1;
        }}
    }} else if (snprintf(out_parent_canon, out_parent_sz, "%s", root_canon) <= 0) {{
        return -1;
    }}
    return 0;
}}

static int dyn_resolve_prefixed(
    const char* input,
    const char* prefix,
    char sep,
    const char* root_canon,
    int lowercase_input,
    double rank_val,
    laplace_rel_symmetry_t symmetry,
    hash128_t* out_type_id,
    double* out_rank,
    laplace_rel_symmetry_t* out_symmetry,
    uint8_t* out_flip,
    hash128_t* out_parent_id) {{
    char canon[128], parent_canon[128];
    if (!out_type_id)
        return -1;
    if (dyn_build_prefixed(input, prefix, sep, root_canon, lowercase_input,
                           canon, sizeof(canon), parent_canon, sizeof(parent_canon)) != 0)
        return -1;
    {{
        int rc = laplace_relation_type_id(canon, out_type_id);
        if (rc < 0)
            return -1;
    }}
    if (out_parent_id) {{
        int rc = laplace_relation_type_id(parent_canon, out_parent_id);
        if (rc < 0)
            return -1;
    }}
    if (out_rank)
        *out_rank = rank_val;
    if (out_symmetry)
        *out_symmetry = symmetry;
    if (out_flip)
        *out_flip = 0;
    return 0;
}}

int laplace_relation_resolve_deprel(
    const char* deprel,
    hash128_t* out_type_id,
    double* out_rank,
    laplace_rel_symmetry_t* out_symmetry,
    uint8_t* out_flip,
    hash128_t* out_parent_id) {{
    return dyn_resolve_prefixed(deprel, "{dep.get('prefix', 'DEP_')}", '{dep.get('separator', ':')}',
                                "{dep.get('root', 'DEPENDS_ON')}", 1, {dep_rank}, {dep_sym},
                                out_type_id, out_rank, out_symmetry, out_flip, out_parent_id);
}}

int laplace_relation_resolve_enhanced_deprel(
    const char* deprel,
    hash128_t* out_type_id,
    double* out_rank,
    laplace_rel_symmetry_t* out_symmetry,
    uint8_t* out_flip,
    hash128_t* out_parent_id) {{
    return dyn_resolve_prefixed(deprel, "{edep.get('prefix', 'EDEP_')}", '{edep.get('separator', ':')}',
                                "{edep.get('root', 'ENHANCED_DEPENDS_ON')}", 1, {edep_rank}, {edep_sym},
                                out_type_id, out_rank, out_symmetry, out_flip, out_parent_id);
}}

int laplace_relation_resolve_feature(
    const char* feature_name,
    hash128_t* out_type_id,
    double* out_rank,
    laplace_rel_symmetry_t* out_symmetry,
    uint8_t* out_flip,
    hash128_t* out_parent_id) {{
    return dyn_resolve_prefixed(feature_name, "{feat.get('prefix', 'FEAT_')}", '\\0',
                                "{feat.get('root', 'HAS_FEATURE')}", {1 if feat.get('lowercase_input', False) else 0},
                                {feat_rank}, {feat_sym},
                                out_type_id, out_rank, out_symmetry, out_flip, out_parent_id);
}}
"""


def emit_relation_law(rel: dict) -> None:
    relations = rel["relation"]
    aliases = rel["alias"]
    ranks = rel["ranks"]

    canon_names = sorted({r["canonical"] for r in relations})
    name_to_idx = {n: i for i, n in enumerate(canon_names)}

    # resolve alias surfaces -> canon index + flip
    alias_entries = []
    for a in sorted(aliases, key=lambda x: x["surface"]):
        canon = a["canonical"]
        if canon not in name_to_idx:
            continue
        alias_entries.append((a["surface"], name_to_idx[canon], a["flip"]))

    header = OUT_CORE / "include/laplace/core/relation_law.h"
    header.write_text(
        """#pragma once

#include <stddef.h>
#include <stdint.h>

#include "laplace/core/hash128.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    LAPLACE_REL_SYMMETRY_ASYMMETRIC = 0,
    LAPLACE_REL_SYMMETRY_SYMMETRIC   = 1,
} laplace_rel_symmetry_t;

typedef struct {
    const char*     canonical;
    hash128_t       type_id;
    double          rank;
    laplace_rel_symmetry_t symmetry;
    int16_t         parent_idx;   /* -1 if none */
    int16_t         family_root_idx;
    uint8_t         flip;         /* alias-only; canonical entries 0 */
} laplace_relation_def_t;

typedef struct {
    const char* surface;
    int16_t     canon_idx;
    uint8_t     flip;
} laplace_relation_alias_t;

extern const laplace_relation_def_t* laplace_relation_table;
extern const size_t laplace_relation_table_count;

extern const laplace_relation_alias_t* laplace_relation_alias_table;
extern const size_t laplace_relation_alias_table_count;

int laplace_relation_type_id(const char* canonical_name, hash128_t* out_type_id);
int laplace_relation_resolve_surface(const char* surface, hash128_t* out_type_id,
                                     double* out_rank, laplace_rel_symmetry_t* out_symmetry,
                                     uint8_t* out_flip, hash128_t* out_parent_id);
int laplace_relation_lookup(const hash128_t* type_id, const laplace_relation_def_t** out_def);
int laplace_relation_in_family(const hash128_t* type_id, const char* family_root, int* out);

int laplace_relation_resolve_deprel(const char* deprel, hash128_t* out_type_id,
                                    double* out_rank, laplace_rel_symmetry_t* out_symmetry,
                                    uint8_t* out_flip, hash128_t* out_parent_id);
int laplace_relation_resolve_enhanced_deprel(const char* deprel, hash128_t* out_type_id,
                                             double* out_rank, laplace_rel_symmetry_t* out_symmetry,
                                             uint8_t* out_flip, hash128_t* out_parent_id);
int laplace_relation_resolve_feature(const char* feature_name, hash128_t* out_type_id,
                                     double* out_rank, laplace_rel_symmetry_t* out_symmetry,
                                     uint8_t* out_flip, hash128_t* out_parent_id);

#ifdef __cplusplus
}
#endif
""",
        encoding="utf-8",
    )

    lines = [
        "/* AUTO-GENERATED by scripts/codegen-attestation-law.ps1 — do not edit */",
        '#include "laplace/core/relation_law.h"',
        "",
        "#include <stdio.h>",
        "#include <string.h>",
        "",
        "#include \"laplace/core/hash128.h\"",
        "",
        "static const laplace_relation_def_t k_relations[] = {",
    ]

    for name in canon_names:
        r = next(x for x in relations if x["canonical"] == name)
        sym = "LAPLACE_REL_SYMMETRY_SYMMETRIC" if r["symmetry"] == "symmetric" else "LAPLACE_REL_SYMMETRY_ASYMMETRIC"
        parent = r.get("parent")
        parent_idx = str(name_to_idx[parent]) if parent and parent in name_to_idx else "-1"
        fr = r.get("family_root") or name
        fr_idx = str(name_to_idx[fr]) if fr in name_to_idx else str(name_to_idx[name])
        rank_key = r["rank"]
        rank_val = ranks.get(rank_key, 0.09)
        lines.append(
            f'    {{ "{name}", {{0}}, {rank_val}, {sym}, {parent_idx}, {fr_idx}, 0 }},'
        )

    lines.append("};")
    lines.append("")
    lines.append("static const laplace_relation_alias_t k_alias_storage[] = {")
    for surf, idx, flip in alias_entries:
        lines.append(f'    {{ "{surf}", {idx}, {1 if flip else 0} }},')
    lines.append("};")
    lines.append("")
    # Append runtime API (tables k_relations / k_alias_storage populated above)
    impl = f"""
const laplace_relation_def_t* laplace_relation_table = k_relations;
const size_t laplace_relation_table_count = {len(canon_names)};
const laplace_relation_alias_t* laplace_relation_alias_table = k_alias_storage;
const size_t laplace_relation_alias_table_count = {len(alias_entries)};

static int cmp_str(const char* a, const char* b) {{
    return strcmp(a, b);
}}

static int type_id_from_canonical(const char* canonical_name, hash128_t* out_type_id) {{
    if (!canonical_name || !out_type_id) return -1;
    char path[256];
    int n = snprintf(path, sizeof(path), "substrate/type/%s/v1", canonical_name);
    if (n <= 0 || (size_t)n >= sizeof(path)) return -1;
    hash128_blake3((const uint8_t*)path, (size_t)n, out_type_id);
    return 0;
}}

/* Type-id cache: lookups are the hot path of every attestation build (model
 * deposits issue hundreds of millions). Recomputing BLAKE3 per table entry per
 * call made laplace_relation_lookup cost ~{len(canon_names)} hashes per MISS —
 * billions of hashes per deposit. Ids are computed once, thread-safely. */
static hash128_t k_relation_type_id_cache[{len(canon_names)}];

#ifdef _WIN32
#include <windows.h>
static volatile LONG g_relation_ids_state = 0;
static int ids_try_begin(void) {{ return InterlockedCompareExchange(&g_relation_ids_state, 1, 0) == 0; }}
static void ids_mark_ready(void) {{ InterlockedExchange(&g_relation_ids_state, 2); }}
static int ids_ready(void) {{ return InterlockedCompareExchange(&g_relation_ids_state, 2, 2) == 2; }}
#else
static volatile int g_relation_ids_state = 0;
static int ids_try_begin(void) {{ int expected = 0; return __atomic_compare_exchange_n(&g_relation_ids_state, &expected, 1, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE); }}
static void ids_mark_ready(void) {{ __atomic_store_n(&g_relation_ids_state, 2, __ATOMIC_RELEASE); }}
static int ids_ready(void) {{ return __atomic_load_n(&g_relation_ids_state, __ATOMIC_ACQUIRE) == 2; }}
#endif

static void relation_ids_ensure(void) {{
    if (ids_ready()) return;
    if (ids_try_begin()) {{
        for (size_t i = 0; i < laplace_relation_table_count; ++i)
            type_id_from_canonical(laplace_relation_table[i].canonical, &k_relation_type_id_cache[i]);
        ids_mark_ready();
    }} else {{
        while (!ids_ready()) {{ /* another thread is filling the cache */ }}
    }}
}}

static int table_entry_type_id(size_t idx, hash128_t* out_type_id) {{
    if (idx >= laplace_relation_table_count || !out_type_id) return -1;
    relation_ids_ensure();
    *out_type_id = k_relation_type_id_cache[idx];
    return 0;
}}

int laplace_relation_type_id(const char* canonical_name, hash128_t* out_type_id) {{
    if (!canonical_name || !out_type_id) return -1;
    for (size_t i = 0; i < laplace_relation_table_count; ++i) {{
        if (cmp_str(laplace_relation_table[i].canonical, canonical_name) == 0) {{
            return table_entry_type_id(i, out_type_id);
        }}
    }}
    return type_id_from_canonical(canonical_name, out_type_id) == 0 ? 1 : -1;
}}

static int alias_lookup(const char* surface, int16_t* out_idx, uint8_t* out_flip) {{
    for (size_t i = 0; i < laplace_relation_alias_table_count; ++i) {{
        if (cmp_str(laplace_relation_alias_table[i].surface, surface) == 0) {{
            *out_idx = laplace_relation_alias_table[i].canon_idx;
            *out_flip = laplace_relation_alias_table[i].flip;
            return 0;
        }}
    }}
    return -1;
}}

int laplace_relation_resolve_surface(const char* surface, hash128_t* out_type_id,
                                     double* out_rank, laplace_rel_symmetry_t* out_symmetry,
                                     uint8_t* out_flip, hash128_t* out_parent_id) {{
    if (!surface || !out_type_id) return -1;
    const char* canon_name = surface;
    uint8_t flip = 0;
    int16_t idx = -1;
    if (alias_lookup(surface, &idx, &flip) == 0) {{
        canon_name = laplace_relation_table[idx].canonical;
    }} else {{
        for (size_t i = 0; i < laplace_relation_table_count; ++i) {{
            if (cmp_str(laplace_relation_table[i].canonical, surface) == 0) {{
                idx = (int16_t)i;
                break;
            }}
        }}
    }}
    if (idx < 0) {{
        int rc = laplace_relation_type_id(surface, out_type_id);
        if (rc < 0) return rc;
        if (out_rank) *out_rank = {ranks.get('probationary', 0.09)};
        if (out_symmetry) *out_symmetry = LAPLACE_REL_SYMMETRY_ASYMMETRIC;
        if (out_flip) *out_flip = 0;
        if (out_parent_id) hash128_zero(out_parent_id);
        return 1;
    }}
    const laplace_relation_def_t* def = &laplace_relation_table[idx];
    if (table_entry_type_id((size_t)idx, out_type_id) != 0) return -1;
    if (out_rank) *out_rank = def->rank;
    if (out_symmetry) *out_symmetry = def->symmetry;
    if (out_flip) *out_flip = flip;
    if (out_parent_id) {{
        if (def->parent_idx >= 0)
            table_entry_type_id((size_t)def->parent_idx, out_parent_id);
        else
            hash128_zero(out_parent_id);
    }}
    return 0;
}}

int laplace_relation_lookup(const hash128_t* type_id, const laplace_relation_def_t** out_def) {{
    if (!type_id || !out_def) return -1;
    relation_ids_ensure();
    for (size_t i = 0; i < laplace_relation_table_count; ++i) {{
        if (hash128_equals(type_id, &k_relation_type_id_cache[i])) {{
            *out_def = &laplace_relation_table[i];
            return 0;
        }}
    }}
    return -1;
}}

static int family_contains(int16_t idx, int16_t root_idx) {{
    if (idx < 0) return 0;
    if (idx == root_idx) return 1;
    int16_t cur = idx;
    for (int guard = 0; guard < 64; ++guard) {{
        const laplace_relation_def_t* d = &laplace_relation_table[cur];
        if (d->family_root_idx == root_idx) return 1;
        if (d->parent_idx < 0) return 0;
        if (d->parent_idx == root_idx) return 1;
        cur = d->parent_idx;
    }}
    return 0;
}}

int laplace_relation_in_family(const hash128_t* type_id, const char* family_root, int* out) {{
    if (!type_id || !family_root || !out) return -1;
    *out = 0;
    int16_t root_idx = -1;
    for (size_t i = 0; i < laplace_relation_table_count; ++i) {{
        if (cmp_str(laplace_relation_table[i].canonical, family_root) == 0) {{
            root_idx = (int16_t)i;
            break;
        }}
    }}
    if (root_idx < 0) return -1;
    hash128_t entry_id;
    if (table_entry_type_id((size_t)root_idx, &entry_id) == 0
        && hash128_equals(type_id, &entry_id)) {{
        *out = 1;
        return 0;
    }}
    for (size_t i = 0; i < laplace_relation_table_count; ++i) {{
        if (table_entry_type_id(i, &entry_id) != 0) continue;
        if (hash128_equals(type_id, &entry_id)) {{
            *out = family_contains((int16_t)i, root_idx);
            return 0;
        }}
    }}
    return 1;
}}
"""
    dynamic_impl = emit_dynamic_resolvers(rel.get("dynamic", {}), ranks)
    (OUT_CORE / "src/generated/relation_law.c").parent.mkdir(parents=True, exist_ok=True)
    (OUT_CORE / "src/generated/relation_law.c").write_text(
        "\n".join(lines) + impl + dynamic_impl, encoding="utf-8"
    )

    # Seed SQL: canonical relation type paths
    seed_names = sorted({f"substrate/type/{n}/v1" for n in canon_names})
    for a in aliases:
        seed_names.append(f"substrate/type/{a['surface']}/v1")
    seed_names = sorted(set(seed_names))
    sql_lines = [
        "-- AUTO-GENERATED relation type seeds from engine/manifest/relation_types.toml",
        "INSERT INTO canonical_names (id, name)",
        "SELECT canonical_id(v.name), v.name",
        "FROM (VALUES",
    ]
    for i, n in enumerate(seed_names):
        comma = "," if i < len(seed_names) - 1 else ""
        sql_lines.append(f"    ('{n}'){comma}")
    sql_lines.append(") AS v(name)")
    sql_lines.append("ON CONFLICT (id) DO NOTHING;")
    OUT_SEED_FRAG.parent.mkdir(parents=True, exist_ok=True)
    OUT_SEED_FRAG.write_text("\n".join(sql_lines) + "\n", encoding="utf-8")


def emit_pos_law(pos: dict) -> None:
    upos_list = pos["upos"].get("canonical", [])
    # Manifest section order = native enum ABI (UPOS = 0, then 1, 2, ... in file order).
    tagsets: dict = pos["tagsets"]

    enum_lines = ["    LAPLACE_POS_TAGSET_UPOS       = 0,"]
    for i, name in enumerate(tagsets, start=1):
        enum_lines.append(f"    LAPLACE_POS_TAGSET_{name.upper():<10} = {i},")

    header = OUT_CORE / "include/laplace/core/pos_law.h"
    header.write_text(
        "#pragma once\n"
        "\n"
        '#include "laplace/core/hash128.h"\n'
        "\n"
        "#ifdef __cplusplus\n"
        'extern "C" {\n'
        "#endif\n"
        "\n"
        "typedef enum {\n"
        + "\n".join(enum_lines) + "\n"
        "} laplace_pos_tagset_t;\n"
        "\n"
        "/* Returns 0 = canonical UPOS entity, 1 = probationary entity (unmapped tag --\n"
        " * the witnessing change MUST emit the probationary entity), <0 = error. */\n"
        "int laplace_pos_resolve_entity(const char* tag, laplace_pos_tagset_t tagset, hash128_t* out_entity_id);\n"
        "const char* const* laplace_pos_upos_canonical(size_t* out_count);\n"
        "\n"
        "#ifdef __cplusplus\n"
        "}\n"
        "#endif\n",
        encoding="utf-8",
    )

    lines = [
        "/* AUTO-GENERATED by scripts/codegen-attestation-law.ps1 -- do not edit */",
        '#include "laplace/core/pos_law.h"',
        "",
        "#include <stdio.h>",
        "#include <string.h>",
        "",
        '#include "laplace/core/hash128.h"',
        "",
        "static const char* k_upos[] = {",
    ]
    for u in upos_list:
        lines.append(f'    "{u}",')
    lines.append("};")
    lines.append("")
    lines.append("static int str_ieq(const char* a, const char* b) {")
    lines.append("    if (!a || !b) return 0;")
    lines.append("    for (; *a && *b; ++a, ++b) {")
    lines.append("        char ca = *a, cb = *b;")
    lines.append("        if (ca >= 'A' && ca <= 'Z') ca = (char)(ca + 32);")
    lines.append("        if (cb >= 'A' && cb <= 'Z') cb = (char)(cb + 32);")
    lines.append("        if (ca != cb) return 0;")
    lines.append("    }")
    lines.append("    return *a == 0 && *b == 0;")
    lines.append("}")
    lines.append("")
    lines.append("static void hash_canonical(const char* s, hash128_t* out) {")
    lines.append("    hash128_blake3((const uint8_t*)s, strlen(s), out);")
    lines.append("}")
    lines.append("")
    lines.append("static int upos_index(const char* tag) {")
    lines.append("    for (size_t i = 0; i < sizeof(k_upos)/sizeof(k_upos[0]); ++i) {")
    lines.append("        if (strcmp(k_upos[i], tag) == 0) return (int)i;")
    lines.append("    }")
    lines.append("    return -1;")
    lines.append("}")
    lines.append("")
    lines.append("static const char* resolve_upos_canonical(const char* tag) {")
    lines.append("    return upos_index(tag) >= 0 ? tag : NULL;")
    lines.append("}")
    lines.append("")
    lines.append("typedef struct { const char* key; const char* canon; } tag_map_t;")
    for name, mapping in tagsets.items():
        lines.append("")
        lines.append(f"static const tag_map_t k_{name}[] = {{")
        for k in sorted(mapping, key=str.lower):
            lines.append(f'    {{ "{k}", "{mapping[k]}" }},')
        lines.append("};")
        lines.append("")
        lines.append(f"static const char* resolve_{name}(const char* tag) {{")
        lines.append(f"    for (size_t i = 0; i < sizeof(k_{name})/sizeof(k_{name}[0]); ++i) {{")
        lines.append(f"        if (str_ieq(k_{name}[i].key, tag)) return k_{name}[i].canon;")
        lines.append("    }")
        lines.append("    return NULL;")
        lines.append("}")
    lines.append("")
    lines.append("int laplace_pos_resolve_entity(const char* tag, laplace_pos_tagset_t tagset, hash128_t* out_entity_id) {")
    lines.append("    if (!tag || !out_entity_id) return -1;")
    lines.append("    const char* canon = NULL;")
    lines.append("    const char* ns = NULL;")
    lines.append("    switch (tagset) {")
    lines.append('        case LAPLACE_POS_TAGSET_UPOS: canon = resolve_upos_canonical(tag); ns = "upos"; break;')
    for name in tagsets:
        lines.append(f'        case LAPLACE_POS_TAGSET_{name.upper()}: canon = resolve_{name}(tag); ns = "{name}"; break;')
    lines.append("        default: return -1;")
    lines.append("    }")
    lines.append("    if (canon) {")
    lines.append("        char path[64];")
    lines.append('        int n = snprintf(path, sizeof(path), "substrate/pos/%s/v1", canon);')
    lines.append("        if (n <= 0 || (size_t)n >= sizeof(path)) return -1;")
    lines.append("        hash_canonical(path, out_entity_id);")
    lines.append("        return 0;")
    lines.append("    }")
    lines.append("    char path[128];")
    lines.append('    int n = snprintf(path, sizeof(path), "substrate/pos/probationary/%s/%s/v1", ns, tag);')
    lines.append("    if (n <= 0 || (size_t)n >= sizeof(path)) return -1;")
    lines.append("    hash_canonical(path, out_entity_id);")
    lines.append("    return 1;")
    lines.append("}")
    lines.append("")
    lines.append("const char* const* laplace_pos_upos_canonical(size_t* out_count) {")
    lines.append("    if (out_count) *out_count = sizeof(k_upos)/sizeof(k_upos[0]);")
    lines.append("    return k_upos;")
    lines.append("}")

    (OUT_CORE / "src/generated/pos_law.c").write_text("\n".join(lines) + "\n", encoding="utf-8")

    # POS seed paths
    pos_seeds = [f"substrate/pos/{u}/v1" for u in upos_list]
    pos_frag = OUT_SEED_FRAG.parent / "21_seed_pos.sql.in"
    sql = [
        "-- AUTO-GENERATED POS entity seeds from engine/manifest/pos_tags.toml",
        "INSERT INTO canonical_names (id, name)",
        "SELECT canonical_id(v.name), v.name",
        "FROM (VALUES",
    ]
    for i, n in enumerate(pos_seeds):
        sql.append(f"    ('{n}')" + ("," if i < len(pos_seeds) - 1 else ""))
    sql.append(") AS v(name)")
    sql.append("ON CONFLICT (id) DO NOTHING;")
    pos_frag.write_text("\n".join(sql) + "\n", encoding="utf-8")


def main() -> int:
    rel = parse_simple_toml(MANIFEST / "relation_types.toml")
    pos = parse_simple_toml(MANIFEST / "pos_tags.toml")
    emit_relation_law(rel)
    emit_pos_law(pos)
    print("codegen ok:", OUT_CORE / "src/generated/relation_law.c")
    return 0


if __name__ == "__main__":
    sys.exit(main())
