#!/usr/bin/env python3
"""Check published UCI assembly names against the actual managed projects."""
import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("application_payload", ROOT / "scripts/verify-api-payload.py")
payload = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(payload)
GUI_SPEC = importlib.util.spec_from_file_location("gui_game_payload", ROOT / "scripts/check-cutechess-gui-game.py")
gui = importlib.util.module_from_spec(GUI_SPEC)
GUI_SPEC.loader.exec_module(gui)

def assembly(project):
    path = ROOT / "app" / project / (project + ".csproj")
    return ET.parse(path).getroot().findtext(".//AssemblyName") or project

class UciPayloadTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="uci-payload-")
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name)
        self.uci = assembly("Laplace.Chess.Uci")
        self.core = assembly("Laplace.Core")
        self.chess = assembly("Laplace.Chess")
        self.names = (self.uci, self.uci + ".dll", self.uci + ".deps.json",
                      self.uci + ".runtimeconfig.json", self.core + ".dll", self.chess + ".dll",
                      "liblaplace_core.so", "liblaplace_dynamics.so",
                      "liblaplace_synthesis.so", "liblaplace_syzygy.so")
        for name in self.names:
            (self.path / name).write_bytes(("isolated publication fixture: " + name).encode())

    def test_published_uci_uses_project_assembly_names(self):
        manifest = payload.seal_uci(self.path)
        self.assertEqual(set(manifest["files"]), set(self.names))
        self.assertEqual(manifest["schema"], "laplace.uci-payload/v1")
        self.assertFalse(manifest["wrapped"])
        namespace = ET.parse(ROOT / "app/Laplace.Core/Laplace.Core.csproj").getroot().findtext(".//RootNamespace")
        self.assertNotEqual(namespace, self.core)
        self.assertNotIn(namespace + ".dll", manifest["files"])

    def test_missing_actual_core_assembly_is_rejected(self):
        (self.path / (self.core + ".dll")).unlink()
        with self.assertRaisesRegex(ValueError, "API payload omitted"):
            payload.seal_uci(self.path)

    def test_missing_native_closure_is_rejected(self):
        (self.path / "liblaplace_syzygy.so").unlink()
        with self.assertRaisesRegex(ValueError, "API payload omitted"):
            payload.seal_uci(self.path)

    def test_symbolic_artifact_is_rejected(self):
        target = self.path / (self.core + ".dll")
        target.unlink()
        target.symlink_to(self.path / (self.chess + ".dll"))
        with self.assertRaisesRegex(ValueError, "symbolic link"):
            payload.seal_uci(self.path)

    def catalog(self):
        directory = self.path / "catalog"
        directory.mkdir()
        catalog = directory / "ChessCatalogSurfaces"
        catalog.write_bytes(b"isolated catalog apphost")
        catalog.chmod(0o755)
        for name in (self.core + ".dll", self.chess + ".dll",
                     "liblaplace_core.so", "liblaplace_dynamics.so",
                     "liblaplace_synthesis.so", "liblaplace_syzygy.so"):
            (directory / name).write_bytes((self.path / name).read_bytes())
        return catalog

    def test_catalog_closure_uses_actual_core_assembly(self):
        catalog = self.catalog()
        result = gui.catalog_closure(catalog, self.path / (self.uci + ".native"))
        self.assertIn(str(catalog.parent / (self.core + ".dll")), result)
        self.assertNotIn(str(catalog.parent / "Laplace.Engine.Core.dll"), result)

    def test_catalog_still_rejects_different_native_bytes(self):
        catalog = self.catalog()
        (catalog.parent / "liblaplace_core.so").write_bytes(b"different native generation")
        with self.assertRaisesRegex(ValueError, "closure differs"):
            gui.catalog_closure(catalog, self.path / (self.uci + ".native"))

if __name__ == "__main__":
    unittest.main(verbosity=2)
