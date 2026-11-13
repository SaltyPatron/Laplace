#!/usr/bin/env python3
"""Structural gate for foundry-synthesized GGUFs: a tensor that is entirely zero, or a
readout whose rows are all the same, is not a model.

WHY THIS EXISTS. The synthesize job's only check was "the file exists and is larger than
50 MB". That is the failure mode scripts/verify-model-behavioral.py names in its own
header -- SIMULATED success -- encoded as a CI gate: a 50 MB file of zeros passes.

The behavioral harness is the real gate, but it needs a llama runtime, which is not
vendored in external/ and is not on the runner. This one needs nothing but the artifact,
so it can be mandatory today, and it catches the exact class that shipped:

  - compile=continuation silently applied OpAttnScale = OpResidScale = 0 to every declared
    non-continuation operator, so recipes that named relation:IS_A emitted tensors
    containing none of it while the plane loader still printed the edge counts;
  - a constant-gate FFN writes the same row everywhere;
  - hub collapse leaves an output projection whose rows barely differ.

None of those change the file size. All of them are visible in the tensors.

Exit codes: 0 pass, 1 degenerate artifact, 2 harness/setup error.
"""

import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

try:
    import numpy as np
    from importlib.machinery import SourceFileLoader
    _oracle = SourceFileLoader(
        "model_forward_oracle",
        os.path.join(os.path.dirname(os.path.abspath(__file__)), "model-forward-oracle.py"),
    ).load_module()
except Exception as exc:                                  # pragma: no cover - setup only
    print(f"::error::verify-gguf-nondegenerate: setup failed: {exc}")
    sys.exit(2)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True)
    ap.add_argument("--report")
    # A tensor is allowed to be zero only if it is named here, with a reason.
    ap.add_argument("--allow-zero", default="",
                    help="comma-separated tensor-name substrings permitted to be all-zero")
    args = ap.parse_args()

    if not os.path.isfile(args.model):
        print(f"::error::model not found: {args.model}")
        return 2
    try:
        _, tensors = _oracle._read_gguf(args.model)
    except SystemExit:
        raise
    except Exception as exc:
        print(f"::error::could not read GGUF: {exc}")
        return 2

    if not tensors:
        print("::error::GGUF contains no tensors")
        return 1

    allow = [s.strip() for s in args.allow_zero.split(",") if s.strip()]
    zero, constant, findings = [], [], []

    for name in sorted(tensors):
        a = tensors[name]
        finite = np.isfinite(a)
        if not finite.all():
            findings.append({"tensor": name, "defect": "non-finite values"})
        amax = float(np.abs(a[finite]).max()) if finite.any() else 0.0
        if amax == 0.0:
            if not any(s in name for s in allow):
                zero.append(name)
            continue
        # A readout or projection whose rows are identical carries no per-row information.
        if a.ndim == 2 and a.shape[0] > 1:
            row_span = float(np.abs(a - a[0]).max())
            if row_span == 0.0:
                constant.append(name)

    for n in zero:
        findings.append({"tensor": n, "defect": "entirely zero"})
    for n in constant:
        findings.append({"tensor": n, "defect": "every row identical"})

    report = {
        "model": args.model,
        "tensors": len(tensors),
        "zero_tensors": zero,
        "constant_row_tensors": constant,
        "findings": findings,
        "pass": not findings,
    }
    if args.report:
        with open(args.report, "w", encoding="utf-8") as fh:
            json.dump(report, fh, indent=2)

    if findings:
        for f in findings:
            print(f"::error::{f['tensor']}: {f['defect']}")
        print(f"::error::GGUF is structurally degenerate: {len(findings)} "
              f"of {len(tensors)} tensors carry no information")
        return 1

    print(f"gguf-nondegenerate OK — {len(tensors)} tensors, none zero, none row-constant")
    return 0


if __name__ == "__main__":
    sys.exit(main())
