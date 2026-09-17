#!/usr/bin/env python3
"""Regression controls for application publication with an installed root policy."""
import ast
import importlib.util
import os
from pathlib import Path
import re
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("managed_policy", ROOT / "scripts/managed-policy.py")
policy = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(policy)
FIXTURE = ROOT / "scripts/fixtures/laplace-managed-deploy-legacy-scratch.py"

class Compatibility(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="managed-policy-")
        self.addCleanup(self.temporary.cleanup)
        self.installed = Path(self.temporary.name)
        self.installed.chmod(0o700)
        predecessor = FIXTURE.read_bytes()
        self.assertEqual(policy.blob(predecessor), "dfade6c9b72277ed4d95e64376d283a4aa8821d2")
        self.source = {name: (ROOT / "deploy/linux" / name).read_bytes() for name in policy.NAMES}
        self.previous = {policy.NAMES[0]: predecessor, policy.NAMES[1]: self.source[policy.NAMES[1]]}
        for name, raw in self.previous.items():
            path = self.installed / name
            path.write_bytes(raw)
            path.chmod(0o755)
        # Exercise the exact installed policy's directive validator without
        # importing or executing its privileged lifecycle and setup entrypoints.
        validator = next(node for node in ast.parse(predecessor).body
                         if isinstance(node, ast.FunctionDef) and node.name == "validate_unit")
        namespace = {"re": re, "UNITS": ("mcp", "lichess")}
        exec(compile(ast.Module(body=[validator], type_ignores=[]),
                     str(FIXTURE), "exec"), namespace)
        self.validate_installed_unit = namespace["validate_unit"]

    def unit(self, name):
        return (ROOT / "deploy/linux/managed-services" / ("laplace-" + name + ".service")).read_text()

    def test_installed_exact_predecessor(self):
        raw, units, receipt = policy.select(ROOT, self.installed, os.getuid())
        self.assertEqual(receipt["profile"], "retained-legacy-scratch")
        self.assertEqual(raw, self.previous)
        self.assertFalse(receipt["privilegedPolicyReplaced"])
        self.assertEqual(set(units), {"mcp", "lichess"})

    def test_equal_current_policy(self):
        self.assertEqual(policy.select_profile(self.source, self.source), "same-policy")
        for name in ("mcp", "lichess"):
            self.assertEqual(policy.unit_text(self.unit(name), name, "same-policy"), self.unit(name))

    def test_unknown_managed_policy_rejected(self):
        changed = dict(self.previous)
        changed[policy.NAMES[0]] += b"\n# unreviewed drift\n"
        with self.assertRaises(ValueError):
            policy.select_profile(self.source, changed)

    def test_service_control_drift_rejected(self):
        changed = dict(self.previous)
        changed[policy.NAMES[1]] += b"\n"
        with self.assertRaises(ValueError):
            policy.select_profile(self.source, changed)

    def test_only_four_directives_and_real_installed_validator(self):
        for name in ("mcp", "lichess"):
            before = self.unit(name)
            after = policy.unit_text(before, name, "retained-legacy-scratch")
            changes = [(a, b) for a, b in zip(before.splitlines(), after.splitlines()) if a != b]
            self.assertEqual(len(changes), 4)
            self.assertEqual(len(before.splitlines()), len(after.splitlines()))
            self.assertTrue(all(b == a.replace("/work/" + name, "/work/legacy-" + name) for a, b in changes))
            self.validate_installed_unit(after, name)
            with self.assertRaises(ValueError):
                self.validate_installed_unit(before, name)

    def test_ambiguous_or_missing_directive_rejected(self):
        before = self.unit("mcp")
        for text in (before.replace("ReadWritePaths=/build/laplace/work/mcp\n", ""),
                     before + "ReadWritePaths=/build/laplace/work/mcp\n"):
            with self.assertRaises(ValueError):
                policy.unit_text(text, "mcp", "retained-legacy-scratch")

    def test_untrusted_permissions_or_symlink_rejected(self):
        path = self.installed / policy.NAMES[0]
        path.chmod(0o775)
        with self.assertRaises(ValueError):
            policy.trusted_bytes(path, os.getuid())
        path.chmod(0o755)
        link = self.installed / "linked"
        link.symlink_to(path)
        with self.assertRaises(ValueError):
            policy.trusted_bytes(link, os.getuid())

if __name__ == "__main__":
    unittest.main(verbosity=2)
