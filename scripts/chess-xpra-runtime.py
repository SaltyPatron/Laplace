#!/usr/bin/env python3
"""Acquire the private CuteChess Xpra runtime without changing host packages."""
from pathlib import Path
import importlib.util

path = Path(__file__).resolve().parent / "lib" / "chess_xpra_runtime.py"
spec = importlib.util.spec_from_file_location("laplace_chess_xpra_runtime", path)
if spec is None or spec.loader is None:
    raise RuntimeError("Xpra acquisition owner is absent")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

if __name__ == "__main__":
    module.main()
