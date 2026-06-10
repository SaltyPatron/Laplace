#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "app"

REPLACEMENTS = [
    ("RelationTypeRegistry.AttestWeighted(", "NativeAttestation.Categorical("),
    ("RelationTypeRegistry.AttestDeprel(", "NativeAttestation.Categorical("),
    ("RelationTypeRegistry.AttestEnhancedDeprel(", "NativeAttestation.Categorical("),
    ("RelationTypeRegistry.AttestFeature(", "NativeAttestation.Categorical("),
    ("RelationTypeRegistry.Attest(", "NativeAttestation.Categorical("),
    ("AttestationFactory.CreateCategorical(", "NativeAttestation.CategoricalResolved("),
    ("AttestationFactory.Create(", "NativeAttestation.CategoricalResolved("),
]

for path in APP.rglob("*.cs"):
    if "Decomposers.Abstractions" in path.as_posix():
        continue
    if ".Tests" in path.as_posix():
        continue
    if "Laplace.Decomposers." not in path.as_posix():
        continue
    text = path.read_text(encoding="utf-8")
    orig = text
    for a, b in REPLACEMENTS:
        text = text.replace(a, b)
    if text != orig:
        if "using Laplace.Engine.Core;" not in text:
            text = text.replace("namespace ", "using Laplace.Engine.Core;\n\nnamespace ", 1)
        path.write_text(text, encoding="utf-8")
        print("updated", path.relative_to(ROOT))
