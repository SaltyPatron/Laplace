#!/usr/bin/env python3
"""Vocabulary, entity-type, language, deprel, qualifier, firmware, and trust-class law emitters."""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "engine/manifest"
OUT_CORE = ROOT / "engine/core"
OUT_QUALIFIER_FRAG = ROOT / "extension/laplace_substrate/sql/generated/qualifier_bits.sql.in"


def _write_text(path: Path, text: str) -> None:
    main_mod = sys.modules.get("__main__")
    writer = getattr(main_mod, "_write_text_if_changed", None)
    if writer is not None:
        writer(path, text)
        return
    data = text.encode("utf-8")
    try:
        if path.exists() and path.read_bytes() == data:
            return
    except OSError:
        pass
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def read_vocabulary(name: str) -> list[list[str]]:
    """A generated vocabulary table (engine/manifest/vocabulary/<name>.tsv, derived from
    its authority by scripts/refresh-vocabulary-manifest.py): data rows without the
    provenance comments and the column header."""
    lines = [l for l in (MANIFEST / "vocabulary" / f"{name}.tsv").read_text(encoding="utf-8").splitlines()
             if l and not l.startswith("#")]
    return [l.split("\t") for l in lines[1:]]


def dense_labels(rows: list[list[str]], what: str) -> list[str]:
    """Labels ordered by their append-only code, which must run 1..N without holes."""
    by_code = sorted((int(r[0]), r[1]) for r in rows)
    if [c for c, _ in by_code] != list(range(1, len(by_code) + 1)):
        raise SystemExit(f"{what}: codes must run 1..N")
    return [l for _, l in by_code]


def emit_pos_law() -> None:
    """UPOS is a governed vocabulary: a tag from a declared tagset resolves to its
    canonical UPOS label (and that label's stable index, the POS mask bit)."""
    upos_list = dense_labels(read_vocabulary("upos"), "upos")
    tagsets: dict = {}
    for tagset, code, upos in read_vocabulary("pos_alias"):
        mapping = tagsets.setdefault(tagset, {})
        if upos:
            mapping[code] = upos
    if len(upos_list) > 64:
        raise SystemExit("pos law: more than 64 UPOS values cannot fit the POS mask")

    enum_lines = ["    LAPLACE_POS_TAGSET_UPOS       = 0,"]
    for i, name in enumerate(tagsets, start=1):
        enum_lines.append(f"    LAPLACE_POS_TAGSET_{name.upper():<10} = {i},")

    header = OUT_CORE / "include/laplace/core/pos_law.h"
    _write_text(header,
        "#pragma once\n\n"
        '#include "laplace/core/hash128.h"\n\n'
        "#ifdef __cplusplus\nextern \"C\" {\n#endif\n\n"
        "typedef enum {\n" + "\n".join(enum_lines) + "\n} laplace_pos_tagset_t;\n\n"
        "/* 0: tag resolves to canonical UPOS *out_canonical at stable index *out_index\n"
        " * (the POS mask bit); 1: unmapped (the source's own value); -1: invalid.\n"
        " * A resolved label is native-owned and remains valid for the library lifetime. */\n"
        "int laplace_pos_resolve_canonical(const char* tag, laplace_pos_tagset_t tagset,\n"
        "                                  const char** out_canonical, int* out_index);\n"
        "/* A declared tagset by name (\"upos\", \"wordnet\", ...); -1 when undeclared. */\n"
        "int laplace_pos_tagset_from_name(const char* name);\n"
        "/* The content id of the resolved UPOS label; an unmapped tag is not resolved (1). */\n"
        "int laplace_pos_resolve_entity(const char* tag, laplace_pos_tagset_t tagset, hash128_t* out_entity_id);\n"
        "const char* const* laplace_pos_upos_canonical(size_t* out_count);\n\n"
        "#ifdef __cplusplus\n}\n#endif\n"
    )

    lines = [
        '#include "laplace/core/pos_law.h"\n\n#include <string.h>\n\n#include "laplace/core/hash128.h"\n\n'
        "static const char* k_upos[] = {",
        *[f'    "{u}",' for u in upos_list],
        "};\n\n"
        "static int str_ieq(const char* a, const char* b) {\n"
        "    if (!a || !b) return 0;\n"
        "    for (; *a && *b; ++a, ++b) {\n"
        "        char ca = *a, cb = *b;\n"
        "        if (ca >= 'A' && ca <= 'Z') ca = (char)(ca + 32);\n"
        "        if (cb >= 'A' && cb <= 'Z') cb = (char)(cb + 32);\n"
        "        if (ca != cb) return 0;\n"
        "    }\n"
        "    return *a == 0 && *b == 0;\n"
        "}\n\n"
        "static int upos_index(const char* tag) {\n"
        "    for (size_t i = 0; i < sizeof(k_upos)/sizeof(k_upos[0]); ++i) {\n"
        "        if (strcmp(k_upos[i], tag) == 0) return (int)i;\n"
        "    }\n"
        "    return -1;\n"
        "}\n\n"
        "typedef struct { const char* key; const char* canon; } tag_map_t;",
    ]
    for name, mapping in tagsets.items():
        lines.append("")
        lines.append(f"static const tag_map_t k_{name}[] = {{")
        for k in sorted(mapping, key=str.lower):
            lines.append(f'    {{ "{k}", "{mapping[k]}" }},')
        lines.append(
            "};\n\n"
            f"static const char* resolve_{name}(const char* tag) {{\n"
            f"    for (size_t i = 0; i < sizeof(k_{name})/sizeof(k_{name}[0]); ++i) {{\n"
            f"        if (str_ieq(k_{name}[i].key, tag)) return k_{name}[i].canon;\n"
            "    }\n"
            "    return NULL;\n"
            "}"
        )
    switch_cases = "\n".join(
        f"        case LAPLACE_POS_TAGSET_{name.upper()}: canon = resolve_{name}(tag); break;"
        for name in tagsets
    )
    name_cases = "\n".join(
        f'    if (str_ieq(name, "{name}")) return LAPLACE_POS_TAGSET_{name.upper()};'
        for name in tagsets
    )
    lines.append(
        "\nint laplace_pos_resolve_canonical(const char* tag, laplace_pos_tagset_t tagset,\n"
        "                                  const char** out_canonical, int* out_index) {\n"
        "    if (!tag || !out_canonical) return -1;\n"
        "    const char* canon = NULL;\n"
        "    switch (tagset) {\n"
        "        case LAPLACE_POS_TAGSET_UPOS: {\n"
        "            const int index = upos_index(tag);\n"
        "            canon = index >= 0 ? k_upos[index] : NULL;\n"
        "            break;\n"
        "        }\n"
        f"{switch_cases}\n"
        "        default: return -1;\n"
        "    }\n"
        "    *out_canonical = canon;\n"
        "    if (out_index) *out_index = canon ? upos_index(canon) : -1;\n"
        "    return canon ? 0 : 1;\n"
        "}\n\n"
        "int laplace_pos_tagset_from_name(const char* name) {\n"
        "    if (!name) return -1;\n"
        '    if (str_ieq(name, "upos")) return LAPLACE_POS_TAGSET_UPOS;\n'
        f"{name_cases}\n"
        "    return -1;\n"
        "}\n\n"
        "int laplace_pos_resolve_entity(const char* tag, laplace_pos_tagset_t tagset, hash128_t* out_entity_id) {\n"
        "    if (!out_entity_id) return -1;\n"
        "    const char* canon = NULL;\n"
        "    const int rc = laplace_pos_resolve_canonical(tag, tagset, &canon, NULL);\n"
        "    if (rc != 0) return rc;\n"
        "    return hash128_label_content_id(canon, strlen(canon), out_entity_id) == 0 ? 0 : -1;\n"
        "}\n\n"
        "const char* const* laplace_pos_upos_canonical(size_t* out_count) {\n"
        "    if (out_count) *out_count = sizeof(k_upos)/sizeof(k_upos[0]);\n"
        "    return k_upos;\n"
        "}"
    )
    _write_text(OUT_CORE / "src/generated/pos_law.c", "\n".join(lines) + "\n")


_ATOMIC_ONCE_GUARD = """#ifdef _WIN32
#include <windows.h>
static volatile LONG g_state = 0;
static int try_begin(void) { return InterlockedCompareExchange(&g_state, 1, 0) == 0; }
static void mark_ready(void) { InterlockedExchange(&g_state, 2); }
static int ready(void) { return InterlockedCompareExchange(&g_state, 2, 2) == 2; }
#else
static volatile int g_state = 0;
static int try_begin(void) { int e = 0; return __atomic_compare_exchange_n(&g_state, &e, 1, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE); }
static void mark_ready(void) { __atomic_store_n(&g_state, 2, __ATOMIC_RELEASE); }
static int ready(void) { return __atomic_load_n(&g_state, __ATOMIC_ACQUIRE) == 2; }
#endif"""


def emit_entity_type_law(path: Path) -> None:
    """Governed entity types -> entity_type_law.h/.c (label + membership table)."""
    blocks = re.split(r'^\[\[type\]\]\s*$', path.read_text(encoding="utf-8"), flags=re.M)[1:]
    names, text = [], []
    for block in blocks:
        name = re.search(r'^canonical\s*=\s*"([^"]+)"', block, re.M)
        if not name:
            raise SystemExit("entity_types.toml: a [[type]] without a canonical name")
        flag = re.search(r'^text\s*=\s*(true|false)\s*$', block, re.M)
        names.append(name.group(1))
        text.append(flag is not None and flag.group(1) == "true")
    if len(names) != len(set(names)):
        raise SystemExit("entity_types.toml declares a type twice")
    bad = [n for n in names if not re.fullmatch(r"[A-Za-z0-9_]+", n)]
    if bad:
        raise SystemExit(f"entity type labels must be single words [A-Za-z0-9_]+: {bad}")
    _write_text(OUT_CORE / "include/laplace/core/entity_type_law.h",
        "#pragma once\n\n#include <stddef.h>\n\n"
        '#include "laplace/core/hash128.h"\n\n'
        "#ifdef __cplusplus\nextern \"C\" {\n#endif\n\n"
        "extern const char* const laplace_entity_type_canonical[];\n"
        "extern const size_t laplace_entity_type_count;\n\n"
        "/* 0 with *out_canonical set when type_id is a governed entity type, -1 otherwise. */\n"
        "int laplace_entity_type_lookup(const hash128_t* type_id, const char** out_canonical);\n"
        "/* 0 with *out_type_id set when the name is governed, -1 otherwise. */\n"
        "int laplace_entity_type_id(const char* canonical, hash128_t* out_type_id);\n"
        "/* 1 when type_id is a governed type declared text (its composition is its bytes in\n"
        " * order), 0 for a governed record type, -1 when type_id is not governed. */\n"
        "int laplace_entity_type_is_text(const hash128_t* type_id);\n\n"
        "#ifdef __cplusplus\n}\n#endif\n")
    lines = [
        '#include "laplace/core/entity_type_law.h"\n\n#include <string.h>\n\n'
        "const char* const laplace_entity_type_canonical[] = {",
        *[f'    "{n}",' for n in names],
        f"}};\nconst size_t laplace_entity_type_count = {len(names)};\n\n"
        "/* entity_types.toml text = true, by position */\n"
        f"static const unsigned char k_entity_type_text[{len(names)}] = {{",
        *["    " + ", ".join("1" if t else "0" for t in text[i:i + 32]) + "," for i in range(0, len(text), 32)],
        f"}};\n\nstatic hash128_t k_entity_type_ids[{len(names)}];\n\n"
        f"{_ATOMIC_ONCE_GUARD}\n\n"
        "/* A type's id is the content id of its governed single-word label. */\n"
        "static void ensure_ids(void) {\n"
        "    if (ready()) return;\n"
        "    if (try_begin()) {\n"
        "        for (size_t i = 0; i < laplace_entity_type_count; ++i)\n"
        "            hash128_label_content_id(laplace_entity_type_canonical[i],\n"
        "                                     strlen(laplace_entity_type_canonical[i]), &k_entity_type_ids[i]);\n"
        "        mark_ready();\n"
        "        return;\n"
        "    }\n"
        "    while (!ready()) { }\n"
        "}\n\n"
        "int laplace_entity_type_lookup(const hash128_t* type_id, const char** out_canonical) {\n"
        "    if (!type_id) return -1;\n"
        "    ensure_ids();\n"
        "    for (size_t i = 0; i < laplace_entity_type_count; ++i)\n"
        "        if (k_entity_type_ids[i].hi == type_id->hi && k_entity_type_ids[i].lo == type_id->lo) {\n"
        "            if (out_canonical) *out_canonical = laplace_entity_type_canonical[i];\n"
        "            return 0;\n"
        "        }\n"
        "    return -1;\n"
        "}\n\n"
        "int laplace_entity_type_id(const char* canonical, hash128_t* out_type_id) {\n"
        "    if (!canonical || !out_type_id) return -1;\n"
        "    ensure_ids();\n"
        "    for (size_t i = 0; i < laplace_entity_type_count; ++i)\n"
        "        if (strcmp(laplace_entity_type_canonical[i], canonical) == 0) {\n"
        "            *out_type_id = k_entity_type_ids[i];\n"
        "            return 0;\n"
        "        }\n"
        "    return -1;\n"
        "}\n\n"
        "int laplace_entity_type_is_text(const hash128_t* type_id) {\n"
        "    if (!type_id) return -1;\n"
        "    ensure_ids();\n"
        "    for (size_t i = 0; i < laplace_entity_type_count; ++i)\n"
        "        if (k_entity_type_ids[i].hi == type_id->hi && k_entity_type_ids[i].lo == type_id->lo)\n"
        "            return k_entity_type_text[i];\n"
        "    return -1;\n"
        "}",
    ]
    _write_text(OUT_CORE / "src/generated/entity_type_law.c", "\n".join(lines) + "\n")


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
    _write_text(OUT_CORE / "include/laplace/core/language_law.h",
        "#pragma once\n\n#include <stddef.h>\n\n#ifdef __cplusplus\nextern \"C\" {\n#endif\n\n"
        "/* 0: tag resolves to its canonical ISO 639-3 Id (*out_canonical); 1: no governed\n"
        " * authority maps it (the source's own value); -1: invalid. Case-insensitive; '_'\n"
        " * reads as '-'; a BCP-47 tag falls back to its primary subtag. */\n"
        "int laplace_language_canonical(const char* tag, const char** out_canonical);\n"
        "size_t laplace_language_count(void);\n\n#ifdef __cplusplus\n}\n#endif\n")
    lines = ['#include "laplace/core/language_law.h"\n\n#include <stdlib.h>\n#include <string.h>\n\n'
             "typedef struct { const char* tag; const char* canon; } language_row_t;\n\n"
             "static const language_row_t k_languages[] = {"]
    for i in range(0, len(rows), 18):
        chunk = rows[i:i + 18]
        lines.append("    " + " ".join(f'{{"{tag}", "{canon}"}},' for tag, canon in chunk))
    lines.append(
        "};\n\n"
        "static int row_cmp(const void* key, const void* row) {\n"
        "    return strcmp((const char*)key, ((const language_row_t*)row)->tag);\n"
        "}\n\n"
        "int laplace_language_canonical(const char* tag, const char** out_canonical) {\n"
        "    char norm[64];\n"
        "    size_t n = 0;\n"
        "    if (!tag || !out_canonical) return -1;\n"
        "    for (; tag[n] && n < sizeof(norm) - 1; ++n) {\n"
        "        char c = tag[n];\n"
        "        if (c >= 'A' && c <= 'Z') c = (char)(c + 32);\n"
        "        if (c == '_') c = '-';\n"
        "        norm[n] = c;\n"
        "    }\n"
        "    if (tag[n]) return 1;\n"
        "    norm[n] = 0;\n"
        "    for (int pass = 0; pass < 2; ++pass) {\n"
        "        const language_row_t* hit = (const language_row_t*)bsearch(\n"
        "            norm, k_languages, sizeof(k_languages)/sizeof(k_languages[0]), sizeof(k_languages[0]), row_cmp);\n"
        "        if (hit) { *out_canonical = hit->canon; return 0; }\n"
        "        char* dash = strchr(norm, '-');\n"
        "        if (!dash) break;\n"
        "        *dash = 0;\n"
        "    }\n"
        "    *out_canonical = NULL;\n"
        "    return 1;\n"
        "}\n\n"
        "size_t laplace_language_count(void) { return sizeof(k_languages)/sizeof(k_languages[0]); }"
    )
    _write_text(OUT_CORE / "src/generated/language_law.c", "\n".join(lines) + "\n")


def emit_deprel_law() -> None:
    """UD relations (vocabulary/deprel.tsv, vocabulary/deprel_subtype.tsv)."""
    labels = dense_labels(read_vocabulary("deprel"), "deprel")
    subtypes = dense_labels(read_vocabulary("deprel_subtype"), "deprel_subtype")
    if len(labels) > 63:
        raise SystemExit("deprel: more than 63 universal relations cannot fit the parse vertex")
    if len(subtypes) > 4095:
        raise SystemExit("deprel_subtype: more than 4095 subtypes cannot fit the parse vertex")
    _write_text(OUT_CORE / "include/laplace/core/deprel_law.h",
        "#pragma once\n\n#ifdef __cplusplus\nextern \"C\" {\n#endif\n\n"
        "/* A UD deprel label (\"nsubj:pass\") -> its universal relation's code (1..N);\n"
        " * 0 when the universal part is not declared. */\n"
        "int laplace_deprel_code(const char* label);\n"
        "/* The universal relation label of a code, NULL when undeclared. */\n"
        "const char* laplace_deprel_label(int code);\n"
        "int laplace_deprel_count(void);\n"
        "/* The subtype of a label (\"pass\" in \"nsubj:pass\") -> its code (1..M); 0 when\n"
        " * the label has no subtype, -1 when the subtype is not declared. */\n"
        "int laplace_deprel_subtype_code(const char* label);\n"
        "const char* laplace_deprel_subtype_label(int code);\n"
        "int laplace_deprel_subtype_count(void);\n\n#ifdef __cplusplus\n}\n#endif\n")
    lines = [
        '#include "laplace/core/deprel_law.h"\n\n#include <string.h>\n\n'
        "static const char* k_deprels[] = {",
        *[f'    "{l}",' for l in labels],
        "};\n\nstatic const char* k_subtypes[] = {",
        *[f'    "{l}",' for l in subtypes],
        "};\n\n"
        "int laplace_deprel_code(const char* label) {\n"
        "    if (!label) return 0;\n"
        "    size_t n = strcspn(label, \":\");\n"
        "    for (int i = 0; i < (int)(sizeof(k_deprels)/sizeof(k_deprels[0])); ++i)\n"
        "        if (strlen(k_deprels[i]) == n && strncmp(k_deprels[i], label, n) == 0) return i + 1;\n"
        "    return 0;\n"
        "}\n\n"
        "const char* laplace_deprel_label(int code) {\n"
        "    return code >= 1 && code <= (int)(sizeof(k_deprels)/sizeof(k_deprels[0])) ? k_deprels[code - 1] : NULL;\n"
        "}\n\n"
        "int laplace_deprel_count(void) { return (int)(sizeof(k_deprels)/sizeof(k_deprels[0])); }\n\n"
        "int laplace_deprel_subtype_code(const char* label) {\n"
        "    if (!label) return 0;\n"
        "    const char* colon = strchr(label, ':');\n"
        "    if (!colon || colon[1] == 0) return 0;\n"
        "    for (int i = 0; i < (int)(sizeof(k_subtypes)/sizeof(k_subtypes[0])); ++i)\n"
        "        if (strcmp(k_subtypes[i], colon + 1) == 0) return i + 1;\n"
        "    return -1;\n"
        "}\n\n"
        "const char* laplace_deprel_subtype_label(int code) {\n"
        "    return code >= 1 && code <= (int)(sizeof(k_subtypes)/sizeof(k_subtypes[0])) ? k_subtypes[code - 1] : NULL;\n"
        "}\n\n"
        "int laplace_deprel_subtype_count(void) { return (int)(sizeof(k_subtypes)/sizeof(k_subtypes[0])); }",
    ]
    _write_text(OUT_CORE / "src/generated/deprel_law.c", "\n".join(lines) + "\n")


def read_qualifier_families(path: Path) -> list[dict]:
    """qualifiers.toml families: name, base, values, and the relations they qualify (None = every relation)."""
    body = path.read_text(encoding="utf-8")
    families = []
    for block in body.split("[[family]]")[1:]:
        block = "\n".join(l.split("#", 1)[0] for l in block.splitlines())
        name = re.search(r'name\s*=\s*"([^"]+)"', block).group(1)
        base = int(re.search(r"base\s*=\s*(\d+)", block).group(1))
        values = re.findall(r'"([^"]+)"', re.search(r"values\s*=\s*\[(.*?)\]", block, re.S).group(1))
        rel = re.search(r"relations\s*=\s*\[(.*?)\]", block, re.S)
        relations = set(re.findall(r'"([^"]+)"', rel.group(1))) if rel else None
        if base % 32 or base + len(values) > 256 or len(values) > 32 or len(values) != len(set(values)):
            raise SystemExit(f"qualifiers.toml: family {name} is out of its 32-bit range")
        families.append({"name": name, "base": base, "values": values, "relations": relations})
    names = [f["name"] for f in families]
    if len(names) != len(set(names)):
        raise SystemExit("qualifiers.toml declares a family twice")
    for i, a in enumerate(families):
        for b in families[i + 1:]:
            if a["base"] < b["base"] + len(b["values"]) and b["base"] < a["base"] + len(a["values"]):
                if a["relations"] is None or b["relations"] is None or a["relations"] & b["relations"]:
                    raise SystemExit(f"qualifiers.toml: families {a['name']} and {b['name']} share bits "
                                     "under a common relation")
    return families


def emit_qualifier_law(path: Path, relation_names: set[str]) -> None:
    """Governed claim qualifiers (engine/manifest/qualifiers.toml): family/value -> bit
    of the attestation's 256-bit qualifier mask, meaningful under the family's relations."""
    families = read_qualifier_families(path)
    for f in families:
        for r in f["relations"] or ():
            if r not in relation_names:
                raise SystemExit(f"qualifiers.toml: family {f['name']} qualifies undeclared relation {r}")
    rows = [(f["name"], v, f["base"] + i) for f in families for i, v in enumerate(f["values"])]
    _write_text(OUT_CORE / "include/laplace/core/qualifier_law.h",
        "#pragma once\n\n#ifdef __cplusplus\nextern \"C\" {\n#endif\n\n"
        "/* \"family/value\" (\"identifier/iso639-1\") -> its bit in an attestation's 256-bit\n"
        " * qualifier mask; -1 when undeclared. */\n"
        "int laplace_qualifier_bit(const char* family, const char* value);\n\n"
        "#ifdef __cplusplus\n}\n#endif\n")
    lines = [
        '#include "laplace/core/qualifier_law.h"\n\n#include <string.h>\n\n'
        "typedef struct { const char* family; const char* value; int bit; } qualifier_row_t;\n\n"
        "static const qualifier_row_t k_qualifiers[] = {",
        *[f'    {{ "{f}", "{v}", {b} }},' for f, v, b in rows],
        "};\n\n"
        "int laplace_qualifier_bit(const char* family, const char* value) {\n"
        "    if (!family || !value) return -1;\n"
        "    for (size_t i = 0; i < sizeof(k_qualifiers)/sizeof(k_qualifiers[0]); ++i)\n"
        "        if (strcmp(k_qualifiers[i].family, family) == 0 && strcmp(k_qualifiers[i].value, value) == 0)\n"
        "            return k_qualifiers[i].bit;\n"
        "    return -1;\n"
        "}",
    ]
    _write_text(OUT_CORE / "src/generated/qualifier_law.c", "\n".join(lines) + "\n")

    sql = """-- GENERATED by scripts/codegen-attestation-law.py - do not edit.
-- Claim qualifiers (engine/manifest/qualifiers.toml): family/value -> bit of an
-- attestation's qualifier_mask (32 bytes, bit b = get_bit(mask, b)).
CREATE OR REPLACE FUNCTION laplace.qualifier_bit(p_family text, p_value text)
    RETURNS integer
    AS 'EXECUTION_LIBRARY', 'pg_laplace_qualifier_bit'
    LANGUAGE C IMMUTABLE STRICT PARALLEL SAFE;

-- Whether a claim states that qualifier; a NULL mask states none.
CREATE OR REPLACE FUNCTION laplace.has_qualifier(p_mask bytea, p_family text, p_value text)
    RETURNS boolean
    AS 'EXECUTION_LIBRARY', 'pg_laplace_has_qualifier'
    LANGUAGE C IMMUTABLE PARALLEL SAFE;
"""
    OUT_QUALIFIER_FRAG.parent.mkdir(parents=True, exist_ok=True)
    _write_text(OUT_QUALIFIER_FRAG, sql)


FIRMWARE_FIELDS = [
    ("salience_floor", "double"), ("semantic_hops", "int"), ("fanout", "int"),
    ("meeting_hops", "int"), ("meeting_fanout", "int"), ("meeting_frontier", "int"),
    ("sharing_window", "int"), ("sharing_cap", "int"), ("spread", "double"),
    ("top_k", "int"), ("steps", "int"), ("max_stride", "int"),
]


def emit_firmware_law(path: Path) -> None:
    """Personality firmware images (engine/manifest/firmware.toml, spec 39)."""
    body = path.read_text(encoding="utf-8")
    images = []
    for block in body.split("[[image]]")[1:]:
        def field(key, pattern=r'"([^"]+)"'):
            m = re.search(rf"^{key}\s*=\s*{pattern}\s*$", block, re.M)
            if not m:
                raise SystemExit(f"firmware.toml: image is missing {key}")
            return m.group(1)
        name, klass, version = field("name"), field("class"), int(field("version", r"([0-9]+)"))
        values = {}
        for key, kind in FIRMWARE_FIELDS:
            raw = field(key, r"([0-9.]+)")
            values[key] = float(raw) if kind == "double" else int(raw)
            if values[key] < 0:
                raise SystemExit(f"firmware.toml: {name}.{key} is negative")
        canonical = ";".join([f"name={name}", f"class={klass}", f"version={version}"] +
                             [f"{k}={values[k]!r}" for k, _ in FIRMWARE_FIELDS])
        images.append((name, klass, version, values, canonical))
    names = [i[0] for i in images]
    if "default" not in names or len(names) != len(set(names)):
        raise SystemExit("firmware.toml must declare each image once, including default")
    members = "\n".join(f"    {'double' if kind == 'double' else 'int32_t'} {key};"
                         for key, kind in FIRMWARE_FIELDS)
    _write_text(OUT_CORE / "include/laplace/core/firmware_law.h",
        "#pragma once\n\n#include <stddef.h>\n#include <stdint.h>\n\n"
        '#include "laplace/core/hash128.h"\n\n'
        "#ifdef __cplusplus\nextern \"C\" {\n#endif\n\n"
        "/* A personality firmware image (spec 39): cognition policy, never content. */\n"
        "typedef struct laplace_firmware_image {\n"
        "    const char* name;\n    const char* image_class;\n    uint32_t version;\n"
        f"{members}\n"
        "    const char* canonical;   /* the serialization its id is the content id of */\n"
        "} laplace_firmware_image_t;\n\n"
        "extern const laplace_firmware_image_t laplace_firmware_images[];\n"
        "extern const size_t laplace_firmware_image_count;\n\n"
        "/* The named default image; never NULL. */\n"
        "const laplace_firmware_image_t* laplace_firmware_default(void);\n"
        "/* The governed image of that name, or NULL. */\n"
        "const laplace_firmware_image_t* laplace_firmware_lookup(const char* name);\n"
        "/* 0 with *out set to the image's content id. */\n"
        "int laplace_firmware_id(const laplace_firmware_image_t* image, hash128_t* out);\n\n"
        "#ifdef __cplusplus\n}\n#endif\n")
    lines = ['#include "laplace/core/firmware_law.h"\n\n#include <string.h>\n\n'
             "const laplace_firmware_image_t laplace_firmware_images[] = {"]
    for name, klass, version, values, canonical in images:
        vals = ", ".join(repr(values[k]) for k, _ in FIRMWARE_FIELDS)
        lines.append(f'    {{"{name}", "{klass}", {version}u, {vals}, "{canonical}"}},')
    lines.append(
        f"}};\nconst size_t laplace_firmware_image_count = {len(images)};\n\n"
        "const laplace_firmware_image_t* laplace_firmware_lookup(const char* name) {\n"
        "    if (!name) return NULL;\n"
        "    for (size_t i = 0; i < laplace_firmware_image_count; ++i)\n"
        "        if (strcmp(laplace_firmware_images[i].name, name) == 0) return &laplace_firmware_images[i];\n"
        "    return NULL;\n"
        "}\n\n"
        "const laplace_firmware_image_t* laplace_firmware_default(void) {\n"
        '    return laplace_firmware_lookup("default");\n'
        "}\n\n"
        "int laplace_firmware_id(const laplace_firmware_image_t* image, hash128_t* out) {\n"
        "    if (!image || !out) return -1;\n"
        "    return hash128_label_content_id(image->canonical, strlen(image->canonical), out);\n"
        "}\n"
    )
    _write_text(OUT_CORE / "src/generated/firmware_law.c", "\n".join(lines))


def emit_trust_class_law(path: Path) -> None:
    """Governed witness trust classes (engine/manifest/trust_classes.toml)."""
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
    _write_text(OUT_CORE / "include/laplace/core/trust_class_law.h",
        "#pragma once\n\n#include <stddef.h>\n\n"
        '#include "laplace/core/hash128.h"\n\n'
        "#ifdef __cplusplus\nextern \"C\" {\n#endif\n\n"
        "extern const char* const laplace_trust_class_canonical[];\n"
        "extern const double laplace_trust_class_priors[];\n"
        "extern const size_t laplace_trust_class_count;\n\n"
        "/* 0 with *out_id set (the content id of the class label) when the class is\n"
        " * governed, -1 otherwise. */\n"
        "int laplace_trust_class_id(const char* name, hash128_t* out_id);\n"
        "/* 0 with *out_prior set when id is a governed trust class, -1 otherwise. */\n"
        "int laplace_trust_class_prior(const hash128_t* id, double* out_prior);\n"
        "/* 0 with *out_prior set when the class name is governed, -1 otherwise. */\n"
        "int laplace_trust_class_prior_by_name(const char* name, double* out_prior);\n"
        "/* 0 with *out_name set when id is a governed trust class, -1 otherwise. */\n"
        "int laplace_trust_class_lookup(const hash128_t* id, const char** out_name);\n\n"
        "#ifdef __cplusplus\n}\n#endif\n")
    lines = [
        '#include "laplace/core/trust_class_law.h"\n\n#include <string.h>\n\n'
        "const char* const laplace_trust_class_canonical[] = {",
        *[f'    "{n}",' for n, _ in rows],
        "};\n\nconst double laplace_trust_class_priors[] = {",
        *[f"    {p}," for _, p in rows],
        f"}};\nconst size_t laplace_trust_class_count = {len(rows)};\n\n"
        f"static hash128_t k_trust_class_ids[{len(rows)}];\n\n"
        f"{_ATOMIC_ONCE_GUARD}\n\n"
        "/* A class's id is the content id of its governed single-word label. */\n"
        "static void ensure_ids(void) {\n"
        "    if (ready()) return;\n"
        "    if (try_begin()) {\n"
        "        for (size_t i = 0; i < laplace_trust_class_count; ++i)\n"
        "            hash128_label_content_id(laplace_trust_class_canonical[i],\n"
        "                                     strlen(laplace_trust_class_canonical[i]), &k_trust_class_ids[i]);\n"
        "        mark_ready();\n"
        "        return;\n"
        "    }\n"
        "    while (!ready()) { }\n"
        "}\n\n"
        "static int index_of_name(const char* name) {\n"
        "    if (!name) return -1;\n"
        "    for (size_t i = 0; i < laplace_trust_class_count; ++i)\n"
        "        if (strcmp(laplace_trust_class_canonical[i], name) == 0) return (int)i;\n"
        "    return -1;\n"
        "}\n\n"
        "static int index_of_id(const hash128_t* id) {\n"
        "    if (!id) return -1;\n"
        "    ensure_ids();\n"
        "    for (size_t i = 0; i < laplace_trust_class_count; ++i)\n"
        "        if (k_trust_class_ids[i].hi == id->hi && k_trust_class_ids[i].lo == id->lo) return (int)i;\n"
        "    return -1;\n"
        "}\n\n"
        "int laplace_trust_class_id(const char* name, hash128_t* out_id) {\n"
        "    int i = index_of_name(name);\n"
        "    if (i < 0 || !out_id) return -1;\n"
        "    ensure_ids();\n"
        "    *out_id = k_trust_class_ids[i];\n"
        "    return 0;\n"
        "}\n\n"
        "int laplace_trust_class_prior(const hash128_t* id, double* out_prior) {\n"
        "    int i = index_of_id(id);\n"
        "    if (i < 0 || !out_prior) return -1;\n"
        "    *out_prior = laplace_trust_class_priors[i];\n"
        "    return 0;\n"
        "}\n\n"
        "int laplace_trust_class_prior_by_name(const char* name, double* out_prior) {\n"
        "    int i = index_of_name(name);\n"
        "    if (i < 0 || !out_prior) return -1;\n"
        "    *out_prior = laplace_trust_class_priors[i];\n"
        "    return 0;\n"
        "}\n\n"
        "int laplace_trust_class_lookup(const hash128_t* id, const char** out_name) {\n"
        "    int i = index_of_id(id);\n"
        "    if (i < 0) return -1;\n"
        "    if (out_name) *out_name = laplace_trust_class_canonical[i];\n"
        "    return 0;\n"
        "}",
    ]
    _write_text(OUT_CORE / "src/generated/trust_class_law.c", "\n".join(lines) + "\n")
