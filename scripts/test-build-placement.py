#!/usr/bin/env python3
"""Pure contracts for revision-keyed persistent build placement."""
from __future__ import annotations

import hashlib
import importlib.util
import os
from pathlib import Path
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "place_build_directory", ROOT / "scripts" / "place-build-directory.py"
)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class BuildPlacementIdentity(unittest.TestCase):
    def test_default_identity_preserves_legacy_checkout_cache(self):
        checkout = Path("/example/laplace")
        expected = hashlib.sha256(os.fsencode(str(checkout))).hexdigest()[:16]
        with mock.patch.dict(os.environ, {}, clear=False):
            os.environ.pop("LAPLACE_BUILD_KEY", None)
            self.assertEqual(expected, MODULE.placement_identity(checkout))

    def test_explicit_revision_key_isolates_candidates(self):
        checkout = Path("/example/laplace")
        with mock.patch.dict(os.environ, {"LAPLACE_BUILD_KEY": "a" * 40}, clear=False):
            first = MODULE.placement_identity(checkout)
        with mock.patch.dict(os.environ, {"LAPLACE_BUILD_KEY": "b" * 40}, clear=False):
            second = MODULE.placement_identity(checkout)
        self.assertNotEqual(first, second)

    def test_same_revision_key_is_stable(self):
        checkout = Path("/example/laplace")
        with mock.patch.dict(os.environ, {"LAPLACE_BUILD_KEY": "c" * 40}, clear=False):
            first = MODULE.placement_identity(checkout)
            second = MODULE.placement_identity(checkout)
        self.assertEqual(first, second)


if __name__ == "__main__":
    unittest.main(verbosity=2)
