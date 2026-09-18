#!/usr/bin/env python3
from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "atomic-directory-exchange.py"
SPEC = importlib.util.spec_from_file_location("atomic_directory_exchange", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader
SPEC.loader.exec_module(MODULE)


class AtomicDirectoryExchangeTests(unittest.TestCase):
    def test_exchange_swaps_complete_directories(self):
        with tempfile.TemporaryDirectory(prefix="directory-exchange-") as tmp:
            root = Path(tmp)
            left, right = root / "left", root / "right"
            left.mkdir()
            right.mkdir()
            (left / "identity").write_text("old\n", encoding="utf-8")
            (right / "identity").write_text("new\n", encoding="utf-8")
            MODULE.exchange(left, right)
            self.assertEqual("new\n", (left / "identity").read_text())
            self.assertEqual("old\n", (right / "identity").read_text())

    def test_symlink_operand_is_rejected(self):
        with tempfile.TemporaryDirectory(prefix="directory-exchange-") as tmp:
            root = Path(tmp)
            target, right = root / "target", root / "right"
            target.mkdir()
            right.mkdir()
            link = root / "left"
            link.symlink_to(target, target_is_directory=True)
            with self.assertRaisesRegex(ValueError, "real directory"):
                MODULE.exchange(link, right)


if __name__ == "__main__":
    unittest.main(verbosity=2)
