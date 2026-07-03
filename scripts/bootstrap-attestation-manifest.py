#!/usr/bin/env python3
"""One-shot bootstrap: extract relation_types.toml + pos_tags.toml from C# registries."""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REG = ROOT / "app/Laplace.Substrate/Abstractions/RelationTypeRegistry.cs"
POS = ROOT / "app/Laplace.Substrate/Abstractions/PosReference.cs"
MANIFEST = ROOT / "engine/manifest"

RANK_MAP = {
    "Mandate": "mandate",
    "StandardsStructural": "standards_structural",
    "Taxonomic": "taxonomic",
    "Partitive": "partitive",
    "Causal": "causal",
    "Equivalence": "equivalence",
    "Oppositional": "oppositional",
    "Associative": "associative",
    "TensorCalculation": "tensor_calculation",
    "ScalarValued": "scalar_valued",
    "Probationary": "probationary",
}

RANK_VALUES = {
    "mandate": 1.00,
    "standards_structural": 0.91,
    "taxonomic": 0.82,
    "partitive": 0.73,
    "causal": 0.64,
    "equivalence": 0.55,
    "oppositional": 0.45,
    "associative": 0.36,
    "tensor_calculation": 0.27,
    "scalar_valued": 0.18,
    "probationary": 0.09,
}


def parse_canon(cs: str) -> dict:
    start = cs.find("private static readonly Dictionary<string, RelationTypeDef> Canon")
    block = cs[start : cs.find("};", start)]
    out = {}
    for m in re.finditer(
        r'\["([^"]+)"\]\s*=\s*new\(RelationTypeRank\.(\w+),\s*Symmetry\.(\w+),\s*(null|"([^"]+)")\)',
        block,
    ):
        name, rank, sym, _, parent = m.groups()
        out[name] = {
            "rank": RANK_MAP.get(rank, rank.lower()),
            "symmetry": sym.lower(),
            "parent": parent,
        }
    return out


def parse_aliases(cs: str) -> dict:
    start = cs.find("private static readonly Dictionary<string, (string Canon, bool Flip)> Alias")
    block = cs[start : cs.find("};", start)]
    out = {}
    for m in re.finditer(r'\["([^"]+)"\]\s*=\s*\("([^"]+)",\s*(true|false)\)', block):
        out[m.group(1)] = {"canonical": m.group(2), "flip": m.group(3) == "true"}
    return out


def family_root(name: str, canon: dict, aliases: dict) -> str:
    cur = name
    seen = set()
    while cur and cur not in seen:
        seen.add(cur)
        if cur in aliases:
            cur = aliases[cur]["canonical"]
            continue
        parent = canon.get(cur, {}).get("parent")
        if not parent:
            return cur
        cur = parent
    return name


def write_relation_toml(canon: dict, aliases: dict) -> None:
    lines = ["# Generated from RelationTypeRegistry — edit here; run codegen-attestation-law.ps1", ""]
    lines.append("[ranks]")
    for k, v in RANK_VALUES.items():
        lines.append(f"{k} = {v}")
    lines.append("")

    for name in sorted(canon):
        c = canon[name]
        fr = family_root(name, canon, aliases)
        lines.append(f"[[relation]]")
        lines.append(f'canonical = "{name}"')
        lines.append(f'rank = "{c["rank"]}"')
        lines.append(f'symmetry = "{c["symmetry"]}"')
        if c["parent"]:
            lines.append(f'parent = "{c["parent"]}"')
        else:
            lines.append("parent = null")
        lines.append(f'family_root = "{fr}"')
        lines.append("")

    for surf in sorted(aliases):
        a = aliases[surf]
        lines.append("[[alias]]")
        lines.append(f'surface = "{surf}"')
        lines.append(f'canonical = "{a["canonical"]}"')
        lines.append(f'flip = {"true" if a["flip"] else "false"}')
        lines.append("")

    MANIFEST.mkdir(parents=True, exist_ok=True)
    (MANIFEST / "relation_types.toml").write_text("\n".join(lines) + "\n", encoding="utf-8")


def parse_pos(cs: str) -> None:
    canon_m = re.search(r'public static readonly string\[\] Canonical\s*=\s*\[(.*?)\];', cs, re.S)
    canon = re.findall(r'"([^"]+)"', canon_m.group(1)) if canon_m else []

    wiki = {}
    wiki_start = cs.find("private static readonly Dictionary<string, string> WiktionaryMap")
    wiki_block = cs[wiki_start : cs.find("};", wiki_start)]
    for m in re.finditer(r'\["([^"]+)"\]\s*=\s*"([^"]+)"', wiki_block):
        wiki[m.group(1)] = m.group(2)

    lines = [
        "# POS tag law manifest — run codegen-attestation-law.ps1 after edits",
        "",
        "[upos]",
        "canonical = " + json_list(canon),
        "",
        "[wordnet]",
        'n = "NOUN"',
        'v = "VERB"',
        'a = "ADJ"',
        's = "ADJ"',
        'r = "ADV"',
        "",
        "[wiktionary]",
    ]
    for k in sorted(wiki, key=str.lower):
        lines.append(f'"{k}" = "{wiki[k]}"')

    (MANIFEST / "pos_tags.toml").write_text("\n".join(lines) + "\n", encoding="utf-8")


def json_list(items):
    return "[" + ", ".join(f'"{x}"' for x in items) + "]"


def main():
    cs = REG.read_text(encoding="utf-8")
    canon = parse_canon(cs)
    aliases = parse_aliases(cs)
    write_relation_toml(canon, aliases)
    parse_pos(POS.read_text(encoding="utf-8"))
    print(f"Wrote {len(canon)} relations, {len(aliases)} aliases to {MANIFEST}")


if __name__ == "__main__":
    main()
