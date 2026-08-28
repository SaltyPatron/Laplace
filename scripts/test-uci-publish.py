#!/usr/bin/env python3
"""Exercise the real published UCI apphost and deliberately incomplete copies."""
import argparse
import importlib.util
from pathlib import Path
import shutil
import tempfile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("uci_check", ROOT / "scripts/check-uci-runtime.py")
check = importlib.util.module_from_spec(spec)
spec.loader.exec_module(check)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("runtime", type=Path)
    args = parser.parse_args()
    print("Published runtime legal move:", check.check_runtime(args.runtime / "laplace-uci"))
    with tempfile.TemporaryDirectory(prefix="laplace-uci-publish-test-") as temporary:
        root = Path(temporary)
        apphost_only = root / "apphost-only"
        apphost_only.mkdir()
        shutil.copy2(args.runtime / "laplace-uci", apphost_only / "laplace-uci")
        try:
            check.check_runtime(apphost_only / "laplace-uci")
        except (RuntimeError, BrokenPipeError):
            print("PASS: original apphost-only deployment defect detected")
        else:
            raise AssertionError("apphost without its assembly was accepted")
        linked = root / "laplace-uci"
        linked.symlink_to((args.runtime / "laplace-uci").resolve())
        print("Stable launch-link legal move:", check.check_runtime(linked))


if __name__ == "__main__":
    main()
