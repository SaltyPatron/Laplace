#!/usr/bin/env python3
"""Codegen attestation law: manifest TOML -> relation, POS, entity-type, language, deprel,
qualifier and trust-class laws (.c/.h), seed SQL, highway perfcache."""
from __future__ import annotations

import hashlib
import re
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "engine/manifest"
OUT_CORE = ROOT / "engine/core"
CHECK_MODE = False
DRIFT: list[Path] = []


def _write_if_changed(path: Path, data: bytes) -> None:
    """Skip the write when bytes are identical. Two reasons: (1) the runtime
    mmaps the perfcache blobs, and Windows refuses to truncate a user-mapped
    file (ERROR_USER_MAPPED_FILE -> EINVAL) -- an unchanged manifest must not
    fail the build under a live ingest; (2) unchanged outputs keep their mtime,
    so ninja's restat skips downstream rebuilds."""
    try:
        if path.exists() and path.read_bytes() == data:
            return
    except OSError:
        pass
    if CHECK_MODE:
        DRIFT.append(path)
        return
    path.write_bytes(data)


def _write_text_if_changed(path: Path, text: str) -> None:
    _write_if_changed(path, text.encode("utf-8"))

OUT_FAMILY_FRAG = ROOT / "extension/laplace_substrate/sql/generated/relation_family_ids.sql.in"
OUT_SET_FRAG = ROOT / "extension/laplace_substrate/sql/generated/relation_set_ids.sql.in"


def parse_simple_toml(path: Path) -> dict:
    """Minimal TOML parser for our manifest shape."""
    text = path.read_text(encoding="utf-8")
    data: dict = {
        "ranks": {},
        "relation": [],
        "alias": [],
        # DECLARED sets, as opposed to families. A family is DERIVED — codegen walks
        # parent/family_root chains, so its membership is whatever the taxonomy implies.
        # A set is an operator's explicit list, and the two are not interchangeable:
        # measured live, relate_path's upward arm wants exactly {IS_A, IS_INSTANCE_OF}
        # while the IS_A family holds 3, and its lateral arm wants 11 while the
        # IS_SYNONYM_OF family holds only itself. Reusing a family for either would
        # silently change which edges a path may traverse.
        "set": [],
        "dynamic": {},
        "upos": {},
        
        "tagsets": {},
    }
    section = None
    current: dict | None = None
    key_stack = []

    pending = None   # accumulating a multi-line array value

    for raw in text.splitlines():
        line = raw.split("#", 1)[0].strip()
        # MULTI-LINE ARRAYS. A declared set is a list of relation names, and forcing 13
        # of them onto one line makes the manifest — the single source of truth for 78
        # IS_A declaration sites — unreadable at exactly the place it must be read.
        # Accumulate until the closing bracket, then fall through as if it were one line.
        if pending is not None:
            pending += " " + line
            if "]" not in line:
                continue
            line, pending = pending, None
        elif "=" in line and line.split("=", 1)[1].strip().startswith("[") \
                and "]" not in line.split("=", 1)[1]:
            pending = line
            continue
        if not line:
            continue
        if line.startswith("[") and line.endswith("]") and "=" not in line:
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
            if inner.startswith("set"):
                section = "set"
                current = {}
                data["set"].append(current)
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
    """Codegen governed dynamic-family resolvers."""

    def rank_val(key: str) -> float:
        return float(ranks.get(key, 0.09))

    dep = dynamic.get("deprel", {})
    edep = dynamic.get("enhanced_deprel", {})
    feat = dynamic.get("feature", {})
    ucd = dynamic.get("ucd_property", {})

    dep_rank = rank_val(dep.get("rank", "partitive"))
    edep_rank = rank_val(edep.get("rank", "partitive"))
    feat_rank = rank_val(feat.get("rank", "partitive"))
    ucd_rank = rank_val(ucd.get("rank", "standards_structural"))

    sym_map = {
        "symmetric": "LAPLACE_REL_SYMMETRY_SYMMETRIC",
        "asymmetric": "LAPLACE_REL_SYMMETRY_ASYMMETRIC",
    }
    dep_sym = sym_map.get(dep.get("symmetry", "asymmetric"), "LAPLACE_REL_SYMMETRY_ASYMMETRIC")
    edep_sym = sym_map.get(edep.get("symmetry", "asymmetric"), "LAPLACE_REL_SYMMETRY_ASYMMETRIC")
    feat_sym = sym_map.get(feat.get("symmetry", "asymmetric"), "LAPLACE_REL_SYMMETRY_ASYMMETRIC")
    ucd_sym = sym_map.get(ucd.get("symmetry", "asymmetric"), "LAPLACE_REL_SYMMETRY_ASYMMETRIC")

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

int laplace_relation_resolve_ucd_property(
    const char* property_name,
    hash128_t* out_type_id,
    double* out_rank,
    laplace_rel_symmetry_t* out_symmetry,
    uint8_t* out_flip,
    hash128_t* out_parent_id) {{
    return dyn_resolve_prefixed(property_name, "{ucd.get('prefix', 'UCD_')}", '\\0',
                                "{ucd.get('root', 'HAS_ATTRIBUTE')}", {1 if ucd.get('lowercase_input', False) else 0},
                                {ucd_rank}, {ucd_sym},
                                out_type_id, out_rank, out_symmetry, out_flip, out_parent_id);
}}
"""


def emit_relation_law(rel: dict) -> None:
    bad = [r["canonical"] for r in rel["relation"] if not re.fullmatch(r"[A-Za-z0-9_]+", r["canonical"])]
    if bad:
        raise SystemExit(f"relation labels must be single words [A-Za-z0-9_]+: {bad}")
    relations = rel["relation"]
    aliases = rel["alias"]
    ranks = rel["ranks"]

    canon_names = sorted({r["canonical"] for r in relations})
    name_to_idx = {n: i for i, n in enumerate(canon_names)}

    
    alias_entries = []
    for a in sorted(aliases, key=lambda x: x["surface"]):
        canon = a["canonical"]
        if canon not in name_to_idx:
            continue
        alias_entries.append((a["surface"], name_to_idx[canon], a["flip"]))

    header = OUT_CORE / "include/laplace/core/relation_law.h"
    _write_text_if_changed(header, 
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
    int16_t         parent_idx;
    int16_t         family_root_idx;
    uint8_t         flip;
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
int laplace_relation_resolve_ucd_property(const char* property_name, hash128_t* out_type_id,
                                          double* out_rank, laplace_rel_symmetry_t* out_symmetry,
                                          uint8_t* out_flip, hash128_t* out_parent_id);

#ifdef __cplusplus
}
#endif
"""
    )

    lines = [
        '#include "laplace/core/relation_law.h"',
        "",
        "#include <stdio.h>",
        "#include <string.h>",
        "",
        "#include \"laplace/core/hash128.h\"",
        "#include \"laplace/core/content_witness_batch.h\"",
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

    rel_count = len(canon_names)
    bucket_size = 1
    while bucket_size < rel_count * 4:
        bucket_size <<= 1
    bucket_mask = bucket_size - 1

    # Surface bucket covers aliases + canonicals (resolve_surface's lookup
    # domain); canonical bucket covers canonical names only (relation_type_id's
    # domain — aliases must NOT match there, they blake3-fall-through).
    surface_count = len(alias_entries) + rel_count
    surface_bucket_size = 1
    while surface_bucket_size < surface_count * 4:
        surface_bucket_size <<= 1
    surface_bucket_mask = surface_bucket_size - 1

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
    /* A relation's id is the content id of its label: the same entity that text is
     * anywhere else. Governed labels are single words ([A-Za-z0-9_]+); a dynamic label
     * with other characters composes through the loaded Tier-0 content spine. */
    size_t len = strlen(canonical_name);
    if (hash128_label_content_id(canonical_name, len, out_type_id) == 0) return 0;
    return laplace_content_root_id((const uint8_t*)canonical_name, len, out_type_id) == 0 ? 0 : -1;
}}

static hash128_t k_relation_type_id_cache[{len(canon_names)}];

/* Reverse index: type_id -> table ordinal, O(1). The type_id is a full BLAKE3 digest, so its low
 * 64 bits are already a uniform hash; we bucket on (lo & mask) with linear probing. Built once in
 * relation_ids_ensure() under the same init guard as the id cache. -1 == empty slot. The .shx to
 * k_relations' .shp: index then read. */
#define LAPLACE_REL_BUCKET_SIZE {bucket_size}u
#define LAPLACE_REL_BUCKET_MASK {bucket_mask}u
static int16_t k_relation_bucket[LAPLACE_REL_BUCKET_SIZE];

/* String-keyed buckets for the surface->idx and canonical->idx lookups.
 * These were linear scans of the 23-alias + {len(canon_names)}-canonical tables run once
 * per attestation built through the categorical builders — hundreds of
 * millions of strcmps per seed recomputing a constant. FNV-1a over the
 * surface with linear probing, built under the same init guard as the id
 * bucket. Two tables because the domains differ: resolve_surface matches
 * aliases first then canonicals; relation_type_id must match canonicals
 * ONLY (an alias string falls through to the blake3 path there). */
typedef struct {{
    const char* surface;
    int16_t     idx;
    uint8_t     flip;
}} rel_surface_slot_t;

#define LAPLACE_REL_SURFACE_BUCKET_SIZE {surface_bucket_size}u
#define LAPLACE_REL_SURFACE_BUCKET_MASK {surface_bucket_mask}u
static rel_surface_slot_t k_relation_surface_bucket[LAPLACE_REL_SURFACE_BUCKET_SIZE];
static rel_surface_slot_t k_relation_canonical_bucket[LAPLACE_REL_BUCKET_SIZE];

static uint64_t rel_surface_hash(const char* s) {{
    uint64_t h = 1469598103934665603ull;
    for (const unsigned char* p = (const unsigned char*)s; *p; ++p) {{
        h ^= (uint64_t)*p;
        h *= 1099511628211ull;
    }}
    return h;
}}

static void rel_slot_insert(rel_surface_slot_t* tab, size_t mask,
                            const char* surface, int16_t idx, uint8_t flip,
                            int keep_existing) {{
    size_t b = (size_t)(rel_surface_hash(surface) & mask);
    for (;;) {{
        if (tab[b].surface == NULL) {{
            tab[b].surface = surface;
            tab[b].idx = idx;
            tab[b].flip = flip;
            return;
        }}
        if (keep_existing && strcmp(tab[b].surface, surface) == 0)
            return; /* alias inserted first keeps priority */
        b = (b + 1) & mask;
    }}
}}

static int rel_slot_lookup(const rel_surface_slot_t* tab, size_t mask,
                           const char* surface, int16_t* out_idx, uint8_t* out_flip) {{
    size_t b = (size_t)(rel_surface_hash(surface) & mask);
    for (size_t probes = 0; probes <= mask; ++probes) {{
        if (tab[b].surface == NULL) return -1;
        if (strcmp(tab[b].surface, surface) == 0) {{
            *out_idx = tab[b].idx;
            if (out_flip) *out_flip = tab[b].flip;
            return 0;
        }}
        b = (b + 1) & mask;
    }}
    return -1;
}}

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
        for (size_t b = 0; b < LAPLACE_REL_BUCKET_SIZE; ++b) k_relation_bucket[b] = -1;
        for (size_t i = 0; i < laplace_relation_table_count; ++i) {{
            size_t b = (size_t)(k_relation_type_id_cache[i].lo & LAPLACE_REL_BUCKET_MASK);
            while (k_relation_bucket[b] >= 0) b = (b + 1) & LAPLACE_REL_BUCKET_MASK;
            k_relation_bucket[b] = (int16_t)i;
        }}
        for (size_t b = 0; b < LAPLACE_REL_SURFACE_BUCKET_SIZE; ++b)
            k_relation_surface_bucket[b].surface = NULL;
        for (size_t b = 0; b < LAPLACE_REL_BUCKET_SIZE; ++b)
            k_relation_canonical_bucket[b].surface = NULL;
        /* aliases first: resolve_surface gives alias hits priority */
        for (size_t i = 0; i < laplace_relation_alias_table_count; ++i)
            rel_slot_insert(k_relation_surface_bucket, LAPLACE_REL_SURFACE_BUCKET_MASK,
                            laplace_relation_alias_table[i].surface,
                            laplace_relation_alias_table[i].canon_idx,
                            laplace_relation_alias_table[i].flip, 0);
        for (size_t i = 0; i < laplace_relation_table_count; ++i) {{
            rel_slot_insert(k_relation_surface_bucket, LAPLACE_REL_SURFACE_BUCKET_MASK,
                            laplace_relation_table[i].canonical, (int16_t)i, 0, 1);
            rel_slot_insert(k_relation_canonical_bucket, LAPLACE_REL_BUCKET_MASK,
                            laplace_relation_table[i].canonical, (int16_t)i, 0, 0);
        }}
        ids_mark_ready();
    }} else {{
        while (!ids_ready()) {{ }}
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
    relation_ids_ensure();
    {{
        int16_t idx = -1;
        if (rel_slot_lookup(k_relation_canonical_bucket, LAPLACE_REL_BUCKET_MASK,
                            canonical_name, &idx, NULL) == 0)
            return table_entry_type_id((size_t)idx, out_type_id);
    }}
    return type_id_from_canonical(canonical_name, out_type_id) == 0 ? 1 : -1;
}}

int laplace_relation_resolve_surface(const char* surface, hash128_t* out_type_id,
                                     double* out_rank, laplace_rel_symmetry_t* out_symmetry,
                                     uint8_t* out_flip, hash128_t* out_parent_id) {{
    if (!surface || !out_type_id) return -1;
    uint8_t flip = 0;
    int16_t idx = -1;
    relation_ids_ensure();
    rel_slot_lookup(k_relation_surface_bucket, LAPLACE_REL_SURFACE_BUCKET_MASK,
                    surface, &idx, &flip);
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
    size_t b = (size_t)(type_id->lo & LAPLACE_REL_BUCKET_MASK);
    for (size_t probes = 0; probes < LAPLACE_REL_BUCKET_SIZE; ++probes) {{
        int16_t idx = k_relation_bucket[b];
        if (idx < 0) return -1;                 /* empty slot => not present */
        if (hash128_equals(type_id, &k_relation_type_id_cache[idx])) {{
            *out_def = &laplace_relation_table[idx];
            return 0;
        }}
        b = (b + 1) & LAPLACE_REL_BUCKET_MASK;
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
    const laplace_relation_def_t* def = NULL;
    if (laplace_relation_lookup(type_id, &def) == 0 && def) {{
        *out = family_contains((int16_t)(def - laplace_relation_table), root_idx);
        return 0;
    }}
    return 1;
}}
"""
    dynamic_impl = emit_dynamic_resolvers(rel.get("dynamic", {}), ranks)
    (OUT_CORE / "src/generated/relation_law.c").parent.mkdir(parents=True, exist_ok=True)
    _write_text_if_changed(OUT_CORE / "src/generated/relation_law.c",
        "\n".join(lines) + impl + dynamic_impl
    )

    

    # FOLDABLE FAMILY MEMBERSHIP. consensus.relation_family_members() derives a family
    # by SCANNING laplace.entities for all RelationType rows and testing each with the
    # IMMUTABLE C predicate -- a compiled constant restated as a table scan, wrapped in
    # a STABLE set-returning function that can never fold. Plan-time partition pruning
    # needs the partition key compared to CONSTANTS, so every family-scoped read plans
    # across the whole partition set: measured on one read, same answer, literal ids
    # plan 3 scan nodes where the function form plans 54.
    #
    # This emits the same membership as a pure function of the manifest. Every element
    # is laplace.relation_type_id(<literal>), which is IMMUTABLE (a BLAKE3 of the name,
    # no table access), so the whole array folds at plan time and the LIST partitions
    # prune before execution.
    # Replicates family_contains() above EXACTLY: self-match, then walk the PARENT
    # chain, matching when a node's family_root or parent is the target. Grouping by
    # direct family_root only is wrong -- IS_TRANSLATION_OF has family_root
    # SEMANTIC_EQUIVALENCE and parent RELATED_TO, so it belongs to both.
    by_name = {x["canonical"]: x for x in relations}
    def in_family(name, root):
        if name == root:
            return True
        cur = name
        for _ in range(64):
            d = by_name.get(cur)
            if d is None:
                return False
            if (d.get("family_root") or cur) == root:
                return True
            par = d.get("parent")
            if not par:
                return False
            if par == root:
                return True
            cur = par
        return False
    roots = sorted({(x.get("family_root") or x["canonical"]) for x in relations}
                   | {x.get("parent") for x in relations if x.get("parent")}
                   | set(canon_names))
    fam_members = {}
    for root in roots:
        members = [n for n in canon_names if in_family(n, root)]
        if members:
            fam_members[root] = members
    fam_lines = [
        "-- GENERATED by scripts/codegen-attestation-law.py - do not edit.",
        "-- Foldable family membership: see the note in the generator.",
        "CREATE OR REPLACE FUNCTION consensus.relation_family_ids(p_family text)",
        "    RETURNS bytea[]",
        "    LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $fam$",
        "    SELECT CASE p_family",
    ]
    for root in sorted(fam_members):
        members = ", ".join(f"laplace.relation_type_id('{m}')" for m in sorted(fam_members[root]))
        fam_lines.append(f"        WHEN '{root}' THEN ARRAY[{members}]")
    fam_lines.append("        ELSE NULL::bytea[]")
    fam_lines.append("    END")
    fam_lines.append("$fam$;")
    OUT_FAMILY_FRAG.parent.mkdir(parents=True, exist_ok=True)
    _write_text_if_changed(OUT_FAMILY_FRAG, "\n".join(fam_lines) + "\n")

    # DECLARED SETS -> consensus.relation_set_ids(text), same foldability contract as
    # relation_family_ids: every element is laplace.relation_type_id(<literal>), which is
    # IMMUTABLE (a BLAKE3 of the name, no table access), so the array folds at plan time
    # and the LIST partitions prune before execution.
    #
    # A set is NOT a family. Families are derived from parent/family_root chains, so their
    # membership is whatever the taxonomy implies; a set is an explicit roster. Measured
    # live 2026-08-16 on the running substrate, which is why relate_path could not simply
    # reuse a family: its upward arm wants exactly {IS_A, IS_INSTANCE_OF} while the IS_A
    # FAMILY holds 3 members, and its lateral arm wants 11 while the IS_SYNONYM_OF family
    # holds only itself. Substituting either would silently change which edges a path may
    # traverse — a correctness change wearing a performance change's clothes.
    sets = rel.get("set", [])
    # Aliases count as declared vocabulary: an [[alias]] surface resolves to a canonical
    # relation, so naming one in a set is not drift. FOLLOWS reached this check as an
    # alias and was rejected -- the validator was wrong, not the call site.
    known = set(canon_names) | {a.get("surface") for a in rel.get("alias", []) if a.get("surface")}
    set_members: dict[str, list[str]] = {}
    for s in sets:
        nm = s.get("name")
        if not nm:
            raise SystemExit("codegen: a [[set]] has no name")
        members = s.get("members") or []
        if not members:
            raise SystemExit(f"codegen: set '{nm}' declares no members")
        # FAIL, never emit an empty arm. An unknown name silently yields NULL from the
        # CASE, and `type_id = ANY (NULL)` matches nothing — a traversal that quietly
        # returns no rows instead of erroring. Typos must stop the build.
        unknown = [m for m in members if m not in known]
        if unknown:
            raise SystemExit(
                f"codegen: set '{nm}' names relations that are not in the manifest: "
                + ", ".join(sorted(unknown)))
        if nm in set_members:
            raise SystemExit(f"codegen: set '{nm}' declared twice")
        set_members[nm] = list(members)

    set_lines = [
        "-- GENERATED by scripts/codegen-attestation-law.py - do not edit.",
        "-- Foldable DECLARED relation sets: see the note in the generator.",
        # The signature is stable and CREATE OR REPLACE preserves every live SQL
        # dependent.  Dropping this base function turned an ordinary body refresh
        # into a needless transitive teardown of mask, taxonomy, and lexical reads.
        "CREATE OR REPLACE FUNCTION consensus.relation_set_ids(p_set text)",
        "    RETURNS bytea[]",
        "    LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $set$",
        "    SELECT CASE p_set",
    ]
    for nm in sorted(set_members):
        # Sorted members: the emitted array is a set, so a manifest reordering must not
        # produce a diff in generated SQL and re-trigger every downstream rebuild.
        elems = ", ".join(f"laplace.relation_type_id('{m}')" for m in sorted(set_members[nm]))
        set_lines.append(f"        WHEN '{nm}' THEN ARRAY[{elems}]")
    set_lines.append("        ELSE NULL::bytea[]")
    set_lines.append("    END")
    set_lines.append("$set$;")
    _write_text_if_changed(OUT_SET_FRAG, "\n".join(set_lines) + "\n")



def emit_pos_law(pos: dict) -> None:
    """UPOS is a governed vocabulary: a tag from a declared tagset resolves to its
    canonical UPOS label (and that label's stable index, the POS mask bit). A POS
    value's identity is the content id of its label, like relation and entity-type
    labels; an unmapped tag is the source's own value and stays unresolved."""
    upos_list = pos["upos"].get("canonical", [])
    tagsets: dict = pos["tagsets"]
    if len(upos_list) > 64:
        raise SystemExit("pos law: more than 64 UPOS values cannot fit the POS mask")

    enum_lines = ["    LAPLACE_POS_TAGSET_UPOS       = 0,"]
    for i, name in enumerate(tagsets, start=1):
        enum_lines.append(f"    LAPLACE_POS_TAGSET_{name.upper():<10} = {i},")

    header = OUT_CORE / "include/laplace/core/pos_law.h"
    _write_text_if_changed(header,
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
        "/* 0: tag resolves to canonical UPOS *out_canonical at stable index *out_index\n"
        " * (the POS mask bit); 1: unmapped (the source's own value); -1: invalid. */\n"
        "int laplace_pos_resolve_canonical(const char* tag, laplace_pos_tagset_t tagset,\n"
        "                                  const char** out_canonical, int* out_index);\n"
        "/* A declared tagset by name (\"upos\", \"wordnet\", ...); -1 when undeclared. */\n"
        "int laplace_pos_tagset_from_name(const char* name);\n"
        "/* The content id of the resolved UPOS label; an unmapped tag is not resolved (1). */\n"
        "int laplace_pos_resolve_entity(const char* tag, laplace_pos_tagset_t tagset, hash128_t* out_entity_id);\n"
        "const char* const* laplace_pos_upos_canonical(size_t* out_count);\n"
        "\n"
        "#ifdef __cplusplus\n"
        "}\n"
        "#endif\n"
    )

    lines = [
        '#include "laplace/core/pos_law.h"',
        "",
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
    lines.append("static int upos_index(const char* tag) {")
    lines.append("    for (size_t i = 0; i < sizeof(k_upos)/sizeof(k_upos[0]); ++i) {")
    lines.append("        if (strcmp(k_upos[i], tag) == 0) return (int)i;")
    lines.append("    }")
    lines.append("    return -1;")
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
    lines.append("int laplace_pos_resolve_canonical(const char* tag, laplace_pos_tagset_t tagset,")
    lines.append("                                  const char** out_canonical, int* out_index) {")
    lines.append("    if (!tag || !out_canonical) return -1;")
    lines.append("    const char* canon = NULL;")
    lines.append("    switch (tagset) {")
    lines.append("        case LAPLACE_POS_TAGSET_UPOS: canon = upos_index(tag) >= 0 ? tag : NULL; break;")
    for name in tagsets:
        lines.append(f"        case LAPLACE_POS_TAGSET_{name.upper()}: canon = resolve_{name}(tag); break;")
    lines.append("        default: return -1;")
    lines.append("    }")
    lines.append("    *out_canonical = canon;")
    lines.append("    if (out_index) *out_index = canon ? upos_index(canon) : -1;")
    lines.append("    return canon ? 0 : 1;")
    lines.append("}")
    lines.append("")
    lines.append("int laplace_pos_tagset_from_name(const char* name) {")
    lines.append("    if (!name) return -1;")
    lines.append('    if (str_ieq(name, "upos")) return LAPLACE_POS_TAGSET_UPOS;')
    for name in tagsets:
        lines.append(f'    if (str_ieq(name, "{name}")) return LAPLACE_POS_TAGSET_{name.upper()};')
    lines.append("    return -1;")
    lines.append("}")
    lines.append("")
    lines.append("int laplace_pos_resolve_entity(const char* tag, laplace_pos_tagset_t tagset, hash128_t* out_entity_id) {")
    lines.append("    if (!out_entity_id) return -1;")
    lines.append("    const char* canon = NULL;")
    lines.append("    const int rc = laplace_pos_resolve_canonical(tag, tagset, &canon, NULL);")
    lines.append("    if (rc != 0) return rc;")
    lines.append("    return hash128_label_content_id(canon, strlen(canon), out_entity_id) == 0 ? 0 : -1;")
    lines.append("}")
    lines.append("")
    lines.append("const char* const* laplace_pos_upos_canonical(size_t* out_count) {")
    lines.append("    if (out_count) *out_count = sizeof(k_upos)/sizeof(k_upos[0]);")
    lines.append("    return k_upos;")
    lines.append("}")

    _write_text_if_changed(OUT_CORE / "src/generated/pos_law.c", "\n".join(lines) + "\n")

    


_HIGHWAY_MAGIC = 0x5957484C
_RANK_BANDS = [
    "mandate",
    "definitional",
    "taxonomic",
    "equivalence",
    "partitive",
    "causal",
    "oppositional",
    "associative",
    "tensor_calculation",
    "lexical_glue",
    "scalar_valued",
    "standards_structural",
    "probationary",
]


def emit_entity_type_law(path: Path) -> None:
    """Governed entity types -> entity_type_law.h/.c (label + membership table)."""
    names = re.findall(r'^canonical\s*=\s*"([^"]+)"', path.read_text(encoding="utf-8"), re.M)
    if len(names) != len(set(names)):
        raise SystemExit("entity_types.toml declares a type twice")
    bad = [n for n in names if not re.fullmatch(r"[A-Za-z0-9_]+", n)]
    if bad:
        raise SystemExit(f"entity type labels must be single words [A-Za-z0-9_]+: {bad}")
    _write_text_if_changed(OUT_CORE / "include/laplace/core/entity_type_law.h",
        "#pragma once\n"
        "\n"
        "#include <stddef.h>\n"
        "\n"
        '#include "laplace/core/hash128.h"\n'
        "\n"
        "#ifdef __cplusplus\n"
        'extern "C" {\n'
        "#endif\n"
        "\n"
        "extern const char* const laplace_entity_type_canonical[];\n"
        "extern const size_t laplace_entity_type_count;\n"
        "\n"
        "/* 0 with *out_canonical set when type_id is a governed entity type, -1 otherwise. */\n"
        "int laplace_entity_type_lookup(const hash128_t* type_id, const char** out_canonical);\n"
        "/* 0 with *out_type_id set when the name is governed, -1 otherwise. */\n"
        "int laplace_entity_type_id(const char* canonical, hash128_t* out_type_id);\n"
        "\n"
        "#ifdef __cplusplus\n"
        "}\n"
        "#endif\n")
    lines = [
        '#include "laplace/core/entity_type_law.h"',
        "",
        "#include <string.h>",
        "",
        "const char* const laplace_entity_type_canonical[] = {",
    ]
    lines += [f'    "{n}",' for n in names]
    lines += [
        "};",
        f"const size_t laplace_entity_type_count = {len(names)};",
        "",
        f"static hash128_t k_entity_type_ids[{len(names)}];",
        "",
        "#ifdef _WIN32",
        "#include <windows.h>",
        "static volatile LONG g_state = 0;",
        "static int try_begin(void) { return InterlockedCompareExchange(&g_state, 1, 0) == 0; }",
        "static void mark_ready(void) { InterlockedExchange(&g_state, 2); }",
        "static int ready(void) { return InterlockedCompareExchange(&g_state, 2, 2) == 2; }",
        "#else",
        "static volatile int g_state = 0;",
        "static int try_begin(void) { int e = 0; return __atomic_compare_exchange_n(&g_state, &e, 1, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE); }",
        "static void mark_ready(void) { __atomic_store_n(&g_state, 2, __ATOMIC_RELEASE); }",
        "static int ready(void) { return __atomic_load_n(&g_state, __ATOMIC_ACQUIRE) == 2; }",
        "#endif",
        "",
        "/* A type's id is the content id of its governed single-word label. */",
        "static void ensure_ids(void) {",
        "    if (ready()) return;",
        "    if (try_begin()) {",
        "        for (size_t i = 0; i < laplace_entity_type_count; ++i)",
        "            hash128_label_content_id(laplace_entity_type_canonical[i],",
        "                                     strlen(laplace_entity_type_canonical[i]), &k_entity_type_ids[i]);",
        "        mark_ready();",
        "        return;",
        "    }",
        "    while (!ready()) { }",
        "}",
        "",
        "int laplace_entity_type_lookup(const hash128_t* type_id, const char** out_canonical) {",
        "    if (!type_id) return -1;",
        "    ensure_ids();",
        "    for (size_t i = 0; i < laplace_entity_type_count; ++i)",
        "        if (k_entity_type_ids[i].hi == type_id->hi && k_entity_type_ids[i].lo == type_id->lo) {",
        "            if (out_canonical) *out_canonical = laplace_entity_type_canonical[i];",
        "            return 0;",
        "        }",
        "    return -1;",
        "}",
        "",
        "int laplace_entity_type_id(const char* canonical, hash128_t* out_type_id) {",
        "    if (!canonical || !out_type_id) return -1;",
        "    ensure_ids();",
        "    for (size_t i = 0; i < laplace_entity_type_count; ++i)",
        "        if (strcmp(laplace_entity_type_canonical[i], canonical) == 0) {",
        "            *out_type_id = k_entity_type_ids[i];",
        "            return 0;",
        "        }",
        "    return -1;",
        "}",
    ]
    _write_text_if_changed(OUT_CORE / "src/generated/entity_type_law.c", "\n".join(lines) + "\n")


def _rank_to_band(rank_key: str) -> int:
    try:
        return _RANK_BANDS.index(rank_key)
    except ValueError:
        return len(_RANK_BANDS) - 1


def emit_highway_perfcache(rel: dict, bin_out_dir: Path) -> None:
    """Generate laplace_highway_perfcache.bin + highway_manifest.h from relation_types.toml.

    ADR 0001 / GH #551: bits are an explicit append-only registry in the TOML
    (`bit = N`). Codegen VALIDATES (required, unique, in-range, no reuse) and
    never reassigns alphabetically — adding a relation must not renumber peers.
    """
    relations = rel["relation"]
    ranks     = rel["ranks"]

    HIGHWAY_MASK_BITS = 256

    missing = [r["canonical"] for r in relations if "bit" not in r]
    if missing:
        raise SystemExit(
            "codegen-attestation-law: every [[relation]] requires an explicit bit = N "
            f"(ADR 0001 / GH #551). Missing on: {', '.join(missing[:8])}"
            + ("…" if len(missing) > 8 else "")
        )

    name_to_rel = {r["canonical"]: r for r in relations}
    bit_to_name: dict[int, str] = {}
    for r in relations:
        name = r["canonical"]
        bit = int(r["bit"])
        if bit < 0 or bit >= HIGHWAY_MASK_BITS:
            raise SystemExit(
                f"codegen-attestation-law: {name} bit={bit} out of range "
                f"[0, {HIGHWAY_MASK_BITS})"
            )
        if bit in bit_to_name:
            raise SystemExit(
                f"codegen-attestation-law: bit {bit} claimed by both "
                f"{bit_to_name[bit]!r} and {name!r} — bits are append-only and unique"
            )
        bit_to_name[bit] = name

    # Emit order: ascending bit (stable layout). Alphabetical sort is dead.
    ordered = sorted(bit_to_name.items())  # (bit, name)
    N      = len(ordered)
    N_BANDS = len(_RANK_BANDS)

    if N > HIGHWAY_MASK_BITS:
        raise SystemExit(
            f"codegen-attestation-law: {N} governed relations exceed the "
            f"{HIGHWAY_MASK_BITS}-bit highway mask. Widen entities.highway_mask "
            f"or reduce the governed set — do not let this generate."
        )

    band_masks: list[bytearray] = [bytearray(32) for _ in range(N_BANDS)]

    string_section = bytearray()
    name_off: dict[str, int] = {}
    for _bit, name in ordered:
        name_off[name] = len(string_section)
        string_section.extend(name.encode("utf-8"))
        string_section.append(0)

    HEADER_SIZE   = 128
    REL_REC_SIZE  = 32
    BAND_MASK_SIZE = 32
    rel_offset  = HEADER_SIZE
    band_offset = rel_offset  + N      * REL_REC_SIZE
    str_offset  = band_offset + N_BANDS * BAND_MASK_SIZE

    toml_bytes  = (MANIFEST / "relation_types.toml").read_bytes()
    fingerprint = hashlib.sha256(toml_bytes).digest()[:8]

    header = struct.pack(
        "<IIQQQQQQ8s64x",
        _HIGHWAY_MAGIC, 1,
        N, rel_offset,
        N_BANDS, band_offset,
        str_offset, len(string_section),
        fingerprint,
    )
    assert len(header) == HEADER_SIZE, f"header {len(header)}"

    records = bytearray()
    for bit_pos, name in ordered:
        r        = name_to_rel[name]
        rank_key = r["rank"]
        rank_val = float(ranks.get(rank_key, 0.09))
        band     = _rank_to_band(rank_key)
        sym      = 1 if r.get("symmetry") == "symmetric" else 0
        noff     = name_off[name]
        nlen     = len(name.encode("utf-8"))

        parent    = r.get("parent")
        parent_bit: int = -1
        if parent and parent in name_to_rel:
            parent_bit = int(name_to_rel[parent]["bit"])

        rec = struct.pack("<IBBBBfh18x",
                          noff, nlen, band, bit_pos, sym, rank_val, parent_bit)
        assert len(rec) == REL_REC_SIZE, f"rec {len(rec)}"
        records.extend(rec)

        band_masks[band][bit_pos // 8] |= 1 << (bit_pos % 8)

    binary = bytearray(header)
    binary.extend(records)
    for bm in band_masks:
        binary.extend(bm)
    binary.extend(string_section)

    bin_out_dir.mkdir(parents=True, exist_ok=True)
    out_bin = bin_out_dir / "laplace_highway_perfcache.bin"
    _write_if_changed(out_bin, bytes(binary))

    bucket_size = 1
    while bucket_size < N * 4:
        bucket_size <<= 1
    bucket_mask = bucket_size - 1

    lines = [
        "#pragma once",
        "",
        "/* Generated from engine/manifest/relation_types.toml — do not edit. */",
        "/* Bits are the explicit append-only registry (ADR 0001 / GH #551). */",
        "",
        f"#define LAPLACE_HIGHWAY_MAGIC    0x{_HIGHWAY_MAGIC:08X}u",
        "#define LAPLACE_HIGHWAY_VERSION  1u",
        f"#define LAPLACE_HIGHWAY_REL_COUNT   {N}u",
        f"#define LAPLACE_HIGHWAY_BAND_COUNT  {N_BANDS}u",
        f"#define HIGHWAY_BUCKET_SIZE         {bucket_size}u",
        f"#define HIGHWAY_BUCKET_MASK         {bucket_mask}u",
        "",
        "/* Bit position of each relation type in the 256-bit highway mask */",
    ]

    for bit_pos, name in ordered:
        lines.append(f"#define HIGHWAY_BIT_{name:<50} {bit_pos}u")

    lines += [
        "",
        "/* Band index constants */",
    ]
    for band_idx, band_name in enumerate(_RANK_BANDS):
        lines.append(f"#define HIGHWAY_BAND_{band_name.upper():<45} {band_idx}u")

    out_manifest = OUT_CORE / "include/laplace/core/highway_manifest.h"
    _write_text_if_changed(out_manifest, "\n".join(lines) + "\n")

    print(f"highway perfcache ok: {out_bin} ({len(binary)} bytes,"
          f" {N} relations, {N_BANDS} bands, {bucket_size}-slot table)")



def emit_language_law(path: Path) -> None:
    """The governed language vocabulary (engine/manifest/languages.tsv): a tag as a
    source writes it resolves to its canonical ISO 639-3 Id; a BCP-47 tag resolves by
    its primary subtag when the full tag is not listed."""
    rows = []
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line or line.startswith("#") or line.startswith("tag\t"):
            continue
        tag, canon, _authority = line.split("\t")
        rows.append((tag, canon))
    rows.sort()
    _write_text_if_changed(OUT_CORE / "include/laplace/core/language_law.h",
        "#pragma once\n\n#include <stddef.h>\n\n#ifdef __cplusplus\nextern \"C\" {\n#endif\n\n"
        "/* 0: tag resolves to its canonical ISO 639-3 Id (*out_canonical); 1: no governed\n"
        " * authority maps it (the source's own value); -1: invalid. Case-insensitive; '_'\n"
        " * reads as '-'; a BCP-47 tag falls back to its primary subtag. */\n"
        "int laplace_language_canonical(const char* tag, const char** out_canonical);\n"
        "size_t laplace_language_count(void);\n\n#ifdef __cplusplus\n}\n#endif\n")
    lines = ['#include "laplace/core/language_law.h"', "", "#include <stdlib.h>", "#include <string.h>", "",
             "typedef struct { const char* tag; const char* canon; } language_row_t;", "",
             "static const language_row_t k_languages[] = {"]
    for tag, canon in rows:
        lines.append(f'    {{ "{tag}", "{canon}" }},')
    lines += ["};", "",
              "static int row_cmp(const void* key, const void* row) {",
              "    return strcmp((const char*)key, ((const language_row_t*)row)->tag);",
              "}", "",
              "int laplace_language_canonical(const char* tag, const char** out_canonical) {",
              "    char norm[64];",
              "    size_t n = 0;",
              "    if (!tag || !out_canonical) return -1;",
              "    for (; tag[n] && n < sizeof(norm) - 1; ++n) {",
              "        char c = tag[n];",
              "        if (c >= 'A' && c <= 'Z') c = (char)(c + 32);",
              "        if (c == '_') c = '-';",
              "        norm[n] = c;",
              "    }",
              "    if (tag[n]) return 1;",
              "    norm[n] = 0;",
              "    for (int pass = 0; pass < 2; ++pass) {",
              "        const language_row_t* hit = (const language_row_t*)bsearch(",
              "            norm, k_languages, sizeof(k_languages)/sizeof(k_languages[0]), sizeof(k_languages[0]), row_cmp);",
              "        if (hit) { *out_canonical = hit->canon; return 0; }",
              "        char* dash = strchr(norm, '-');",
              "        if (!dash) break;",
              "        *dash = 0;",
              "    }",
              "    *out_canonical = NULL;",
              "    return 1;",
              "}", "",
              "size_t laplace_language_count(void) { return sizeof(k_languages)/sizeof(k_languages[0]); }"]
    _write_text_if_changed(OUT_CORE / "src/generated/language_law.c", "\n".join(lines) + "\n")


def emit_deprel_law(path: Path) -> None:
    """UD universal relations (engine/manifest/deprels.toml): a deprel label resolves to
    its universal relation's stable 1-based code; the subtype after ':' is the
    treebank's refinement of it. 0 = not a universal relation."""
    import re as _re
    body = path.read_text(encoding="utf-8")
    m = _re.search(r"universal\s*=\s*\[(.*?)\]", body, _re.S)
    if not m:
        raise SystemExit("deprels.toml: no [ud] universal list")
    labels = _re.findall(r'"([a-z]+)"', m.group(1))
    if len(labels) != len(set(labels)) or len(labels) > 63:
        raise SystemExit("deprels.toml: duplicate labels or more than 63 relations")
    _write_text_if_changed(OUT_CORE / "include/laplace/core/deprel_law.h",
        "#pragma once\n\n#ifdef __cplusplus\nextern \"C\" {\n#endif\n\n"
        "/* A UD deprel label (\"nsubj:pass\") -> its universal relation's code (1..N);\n"
        " * 0 when the universal part is not declared. */\n"
        "int laplace_deprel_code(const char* label);\n"
        "/* The universal relation label of a code, NULL when undeclared. */\n"
        "const char* laplace_deprel_label(int code);\n"
        "int laplace_deprel_count(void);\n\n#ifdef __cplusplus\n}\n#endif\n")
    lines = ['#include "laplace/core/deprel_law.h"', "", "#include <string.h>", "",
             "static const char* k_deprels[] = {"]
    lines += [f'    "{l}",' for l in labels]
    lines += ["};", "",
              "int laplace_deprel_code(const char* label) {",
              "    if (!label) return 0;",
              "    size_t n = strcspn(label, \":\");",
              "    for (int i = 0; i < (int)(sizeof(k_deprels)/sizeof(k_deprels[0])); ++i)",
              "        if (strlen(k_deprels[i]) == n && strncmp(k_deprels[i], label, n) == 0) return i + 1;",
              "    return 0;",
              "}", "",
              "const char* laplace_deprel_label(int code) {",
              "    return code >= 1 && code <= (int)(sizeof(k_deprels)/sizeof(k_deprels[0])) ? k_deprels[code - 1] : NULL;",
              "}", "",
              "int laplace_deprel_count(void) { return (int)(sizeof(k_deprels)/sizeof(k_deprels[0])); }"]
    _write_text_if_changed(OUT_CORE / "src/generated/deprel_law.c", "\n".join(lines) + "\n")


def emit_qualifier_law(path: Path) -> None:
    """Governed claim qualifiers (engine/manifest/qualifiers.toml): family/value -> bit
    of the attestation's 256-bit qualifier mask."""
    import re as _re
    body = path.read_text(encoding="utf-8")
    rows = []
    for block in body.split("[[family]]")[1:]:
        name = _re.search(r'name\s*=\s*"([^"]+)"', block).group(1)
        base = int(_re.search(r"base\s*=\s*(\d+)", block).group(1))
        values = _re.findall(r'"([^"]+)"', _re.search(r"values\s*=\s*\[(.*?)\]", block, _re.S).group(1))
        if base % 32 or base + len(values) > 256 or len(values) > 32 or len(values) != len(set(values)):
            raise SystemExit(f"qualifiers.toml: family {name} is out of its 32-bit range")
        rows += [(name, v, base + i) for i, v in enumerate(values)]
    _write_text_if_changed(OUT_CORE / "include/laplace/core/qualifier_law.h",
        "#pragma once\n\n#ifdef __cplusplus\nextern \"C\" {\n#endif\n\n"
        "/* \"family/value\" (\"identifier/iso639-1\") -> its bit in an attestation's 256-bit\n"
        " * qualifier mask; -1 when undeclared. */\n"
        "int laplace_qualifier_bit(const char* family, const char* value);\n\n"
        "#ifdef __cplusplus\n}\n#endif\n")
    lines = ['#include "laplace/core/qualifier_law.h"', "", "#include <string.h>", "",
             "typedef struct { const char* family; const char* value; int bit; } qualifier_row_t;", "",
             "static const qualifier_row_t k_qualifiers[] = {"]
    lines += [f'    {{ "{f}", "{v}", {b} }},' for f, v, b in rows]
    lines += ["};", "",
              "int laplace_qualifier_bit(const char* family, const char* value) {",
              "    if (!family || !value) return -1;",
              "    for (size_t i = 0; i < sizeof(k_qualifiers)/sizeof(k_qualifiers[0]); ++i)",
              "        if (strcmp(k_qualifiers[i].family, family) == 0 && strcmp(k_qualifiers[i].value, value) == 0)",
              "            return k_qualifiers[i].bit;",
              "    return -1;",
              "}"]
    _write_text_if_changed(OUT_CORE / "src/generated/qualifier_law.c", "\n".join(lines) + "\n")


def emit_trust_class_law(path: Path) -> None:
    """Governed witness trust classes (engine/manifest/trust_classes.toml): a class's id is
    the content id of its single-word label; its prior in [0,1] seeds the standing of
    every claim a witness of that class makes."""
    body = path.read_text(encoding="utf-8")
    rows = []
    for block in body.split("[[class]]")[1:]:
        name = re.search(r'^name\s*=\s*"([^"]+)"', block, re.M)
        prior = re.search(r"^prior\s*=\s*([0-9.]+)\s*$", block, re.M)
        desc = re.search(r'^description\s*=\s*"([^"]+)"', block, re.M)
        if not name or not prior or not desc:
            raise SystemExit("trust_classes.toml: every class needs name, prior and description")
        value = float(prior.group(1))
        if not re.fullmatch(r"[A-Za-z0-9_]+", name.group(1)):
            raise SystemExit(f"trust_classes.toml: class labels are single words: {name.group(1)}")
        if not 0.0 <= value <= 1.0:
            raise SystemExit(f"trust_classes.toml: {name.group(1)} prior {value} is outside [0,1]")
        rows.append((name.group(1), prior.group(1)))
    names = [n for n, _ in rows]
    if not rows or len(names) != len(set(names)):
        raise SystemExit("trust_classes.toml declares no class, or a class twice")
    _write_text_if_changed(OUT_CORE / "include/laplace/core/trust_class_law.h",
        "#pragma once\n"
        "\n"
        "#include <stddef.h>\n"
        "\n"
        '#include "laplace/core/hash128.h"\n'
        "\n"
        "#ifdef __cplusplus\n"
        'extern "C" {\n'
        "#endif\n"
        "\n"
        "extern const char* const laplace_trust_class_canonical[];\n"
        "extern const double laplace_trust_class_priors[];\n"
        "extern const size_t laplace_trust_class_count;\n"
        "\n"
        "/* 0 with *out_id set (the content id of the class label) when the class is\n"
        " * governed, -1 otherwise. */\n"
        "int laplace_trust_class_id(const char* name, hash128_t* out_id);\n"
        "/* 0 with *out_prior set when id is a governed trust class, -1 otherwise. */\n"
        "int laplace_trust_class_prior(const hash128_t* id, double* out_prior);\n"
        "/* 0 with *out_prior set when the class name is governed, -1 otherwise. */\n"
        "int laplace_trust_class_prior_by_name(const char* name, double* out_prior);\n"
        "/* 0 with *out_name set when id is a governed trust class, -1 otherwise. */\n"
        "int laplace_trust_class_lookup(const hash128_t* id, const char** out_name);\n"
        "\n"
        "#ifdef __cplusplus\n"
        "}\n"
        "#endif\n")
    lines = [
        '#include "laplace/core/trust_class_law.h"',
        "",
        "#include <string.h>",
        "",
        "const char* const laplace_trust_class_canonical[] = {",
    ]
    lines += [f'    "{n}",' for n, _ in rows]
    lines += ["};", "", "const double laplace_trust_class_priors[] = {"]
    lines += [f"    {p}," for _, p in rows]
    lines += [
        "};",
        f"const size_t laplace_trust_class_count = {len(rows)};",
        "",
        f"static hash128_t k_trust_class_ids[{len(rows)}];",
        "",
        "#ifdef _WIN32",
        "#include <windows.h>",
        "static volatile LONG g_state = 0;",
        "static int try_begin(void) { return InterlockedCompareExchange(&g_state, 1, 0) == 0; }",
        "static void mark_ready(void) { InterlockedExchange(&g_state, 2); }",
        "static int ready(void) { return InterlockedCompareExchange(&g_state, 2, 2) == 2; }",
        "#else",
        "static volatile int g_state = 0;",
        "static int try_begin(void) { int e = 0; return __atomic_compare_exchange_n(&g_state, &e, 1, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE); }",
        "static void mark_ready(void) { __atomic_store_n(&g_state, 2, __ATOMIC_RELEASE); }",
        "static int ready(void) { return __atomic_load_n(&g_state, __ATOMIC_ACQUIRE) == 2; }",
        "#endif",
        "",
        "/* A class's id is the content id of its governed single-word label. */",
        "static void ensure_ids(void) {",
        "    if (ready()) return;",
        "    if (try_begin()) {",
        "        for (size_t i = 0; i < laplace_trust_class_count; ++i)",
        "            hash128_label_content_id(laplace_trust_class_canonical[i],",
        "                                     strlen(laplace_trust_class_canonical[i]), &k_trust_class_ids[i]);",
        "        mark_ready();",
        "        return;",
        "    }",
        "    while (!ready()) { }",
        "}",
        "",
        "static int index_of_name(const char* name) {",
        "    if (!name) return -1;",
        "    for (size_t i = 0; i < laplace_trust_class_count; ++i)",
        "        if (strcmp(laplace_trust_class_canonical[i], name) == 0) return (int)i;",
        "    return -1;",
        "}",
        "",
        "static int index_of_id(const hash128_t* id) {",
        "    if (!id) return -1;",
        "    ensure_ids();",
        "    for (size_t i = 0; i < laplace_trust_class_count; ++i)",
        "        if (k_trust_class_ids[i].hi == id->hi && k_trust_class_ids[i].lo == id->lo) return (int)i;",
        "    return -1;",
        "}",
        "",
        "int laplace_trust_class_id(const char* name, hash128_t* out_id) {",
        "    int i = index_of_name(name);",
        "    if (i < 0 || !out_id) return -1;",
        "    ensure_ids();",
        "    *out_id = k_trust_class_ids[i];",
        "    return 0;",
        "}",
        "",
        "int laplace_trust_class_prior(const hash128_t* id, double* out_prior) {",
        "    int i = index_of_id(id);",
        "    if (i < 0 || !out_prior) return -1;",
        "    *out_prior = laplace_trust_class_priors[i];",
        "    return 0;",
        "}",
        "",
        "int laplace_trust_class_prior_by_name(const char* name, double* out_prior) {",
        "    int i = index_of_name(name);",
        "    if (i < 0 || !out_prior) return -1;",
        "    *out_prior = laplace_trust_class_priors[i];",
        "    return 0;",
        "}",
        "",
        "int laplace_trust_class_lookup(const hash128_t* id, const char** out_name) {",
        "    int i = index_of_id(id);",
        "    if (i < 0) return -1;",
        "    if (out_name) *out_name = laplace_trust_class_canonical[i];",
        "    return 0;",
        "}",
    ]
    _write_text_if_changed(OUT_CORE / "src/generated/trust_class_law.c", "\n".join(lines) + "\n")

def main() -> int:
    global CHECK_MODE
    CHECK_MODE = "--check" in sys.argv[1:]
    bin_out_dir = OUT_CORE / "src/generated"
    for i, arg in enumerate(sys.argv[1:], 1):
        if arg == "--bin-out-dir" and i < len(sys.argv) - 1:
            bin_out_dir = Path(sys.argv[i + 1])
            break
        if arg.startswith("--bin-out-dir="):
            bin_out_dir = Path(arg.split("=", 1)[1])
            break

    rel = parse_simple_toml(MANIFEST / "relation_types.toml")
    pos = parse_simple_toml(MANIFEST / "pos_tags.toml")
    emit_relation_law(rel)
    emit_pos_law(pos)
    emit_entity_type_law(MANIFEST / "entity_types.toml")
    emit_language_law(MANIFEST / "languages.tsv")
    emit_deprel_law(MANIFEST / "deprels.toml")
    emit_qualifier_law(MANIFEST / "qualifiers.toml")
    emit_trust_class_law(MANIFEST / "trust_classes.toml")
    emit_highway_perfcache(rel, bin_out_dir)
    if CHECK_MODE:
        if DRIFT:
            print("generated outputs differ from engine/manifest", file=sys.stderr)
            for path in DRIFT:
                try:
                    path = path.relative_to(ROOT)
                except ValueError:
                    pass
                print(f"  {path}", file=sys.stderr)
            return 1
        print("codegen check ok")
        return 0
    print("codegen ok:", OUT_CORE / "src/generated/relation_law.c")
    return 0


if __name__ == "__main__":
    sys.exit(main())
