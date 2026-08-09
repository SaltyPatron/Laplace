#!/usr/bin/env python3
"""Regression tests for the documentation-governance gate."""

from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).with_name("check-doc-governance.py")
SPEC = importlib.util.spec_from_file_location("check_doc_governance", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class RelativeLinkBoundaryTests(unittest.TestCase):
    def test_normal_relative_link_remains_inside_repository(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "repo"
            source = root / "docs" / "INDEX.md"
            target = root / "docs" / "GOVERNANCE.md"
            target.parent.mkdir(parents=True)
            target.touch()

            resolved, inside = MODULE.resolve_repo_link(
                source, "GOVERNANCE.md", root
            )

            self.assertTrue(inside)
            self.assertEqual(target.resolve(), resolved)

    def test_parent_traversal_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "repo"
            source = root / "docs" / "INDEX.md"
            source.parent.mkdir(parents=True)

            _, inside = MODULE.resolve_repo_link(source, "../../outside.md", root)

            self.assertFalse(inside)

    def test_symlink_escape_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory)
            root = base / "repo"
            outside = base / "outside"
            docs = root / "docs"
            docs.mkdir(parents=True)
            outside.mkdir()
            (docs / "escape").symlink_to(outside, target_is_directory=True)

            _, inside = MODULE.resolve_repo_link(
                docs / "INDEX.md", "escape/host-file.md", root
            )

            self.assertFalse(inside)


if __name__ == "__main__":
    unittest.main()
