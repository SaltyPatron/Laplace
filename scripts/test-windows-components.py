#!/usr/bin/env python3
"""Exercise actual project-reference boundaries and immutable component storage."""
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("windows_components", Path(__file__).with_name("windows-components.py"))
COMPONENTS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(COMPONENTS)


class Components(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="laplace-components-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for name in (*COMPONENTS.PROJECTS.values(), "Laplace.Shared"):
            directory = self.root / "app" / name
            directory.mkdir(parents=True)
            refs = '<ProjectReference Include="../Laplace.Shared/Laplace.Shared.csproj" />' if name != "Laplace.Shared" else ""
            (directory / (name + ".csproj")).write_text(f"<Project><ItemGroup>{refs}</ItemGroup></Project>")
            (directory / "Code.cs").write_text("class Example {}\n")
        subprocess.run(["git", "init", "-q"], cwd=self.root, check=True)
        self.native = self.root / "native"

    def inputs(self, name):
        return COMPONENTS.managed_inputs(self.root, name, self.native, "sdk")

    def test_private_component_change_does_not_invalidate_other_consumers(self):
        before = {name: self.inputs(name) for name in COMPONENTS.PROJECTS}
        (self.root / "app/Laplace.Chess.Uci/Code.cs").write_text("class ChangedChessAdapter {}\n")
        self.assertNotEqual(before["uci"], self.inputs("uci"))
        for name in ("api", "cli", "migrations"):
            self.assertEqual(before[name], self.inputs(name))

    def test_shared_project_change_invalidates_all_actual_consumers(self):
        before = {name: self.inputs(name) for name in COMPONENTS.PROJECTS}
        (self.root / "app/Laplace.Shared/Code.cs").write_text("class ChangedSharedContract {}\n")
        for name in before:
            self.assertNotEqual(before[name], self.inputs(name))

    def test_timestamp_and_frontend_staging_do_not_change_api_inputs(self):
        before = self.inputs("api")
        source = self.root / "app/Laplace.Endpoints.OpenAICompat/Code.cs"
        os.utime(source, (1000000000, 1000000000))
        web = source.parent / "wwwroot"
        web.mkdir()
        (web / "index.html").write_text("separate frontend artifact")
        self.assertEqual(before, self.inputs("api"))

    def test_linked_migration_content_invalidates_only_its_component(self):
        project = self.root / "app/Laplace.Migrations/Laplace.Migrations.csproj"
        project.write_text('<Project><ItemGroup><Content Include="../../db/**/*.sql" /></ItemGroup></Project>')
        sql = self.root / "db/migrations/one.sql"
        sql.parent.mkdir(parents=True)
        sql.write_text("first migration")
        before = {name: self.inputs(name) for name in COMPONENTS.PROJECTS}
        sql.write_text("changed migration")
        self.assertNotEqual(before["migrations"], self.inputs("migrations"))
        for name in ("api", "cli", "uci"):
            self.assertEqual(before[name], self.inputs(name))

    def test_native_abi_change_invalidates_managed_consumers(self):
        library = self.native / "core/laplace_core.dll"
        library.parent.mkdir(parents=True)
        library.write_bytes(b"native ABI one")
        before = self.inputs("api")
        library.write_bytes(b"native ABI two")
        self.assertNotEqual(before, self.inputs("api"))

    def test_semicolon_lists_and_recipe_runtime_resources_are_tracked(self):
        project = self.root / "app/Laplace.Cli/Laplace.Cli.csproj"
        project.write_text('<Project><ItemGroup><Content Include="../../first.txt;../../second.txt" />'
                           '<Content Include="$(LaplaceRecipeRuntime)/**/*" /></ItemGroup></Project>')
        (self.root / "first.txt").write_text("first")
        second = self.root / "second.txt"
        second.write_text("second")
        recipe = self.native / "core/recipe_engine/runtime/share/recipes/source.recipe"
        recipe.parent.mkdir(parents=True)
        recipe.write_text("old recipe")
        before = self.inputs("cli")
        second.write_text("changed linked resource")
        self.assertNotEqual(before, self.inputs("cli"))
        before = self.inputs("cli")
        recipe.write_text("changed runtime recipe")
        self.assertNotEqual(before, self.inputs("cli"))

    def test_unknown_resource_properties_cannot_silently_reuse_an_artifact(self):
        project = self.root / "app/Laplace.Cli/Laplace.Cli.csproj"
        project.write_text('<Project><ItemGroup><Content Include="$(UnresolvedProducer)/resource.bin" /></ItemGroup></Project>')
        with self.assertRaisesRegex(ValueError, "Unresolved publication resource"):
            self.inputs("cli")

    def test_recipe_executable_and_grammar_changes_invalidate_every_publishing_consumer(self):
        project = self.root / "app/Laplace.Shared/Laplace.Shared.csproj"
        project.write_text('<Project><ItemGroup><Content Include="$(LaplaceRecipeRuntime)/**/*" /></ItemGroup></Project>')
        runtime = self.native / "core/recipe_engine/runtime/bin"
        runtime.mkdir(parents=True)
        executable = runtime / "laplace.exe"
        executable.write_bytes(b"Native reader one")
        grammar = runtime / "grammars/xml.dll"
        grammar.parent.mkdir()
        grammar.write_bytes(b"grammar one")
        for path in (executable, grammar):
            before = {name: self.inputs(name) for name in COMPONENTS.PROJECTS}
            path.write_bytes(path.read_bytes() + b" changed")
            for name in before:
                self.assertNotEqual(before[name], self.inputs(name), (name, path))

    def test_identical_runtime_files_share_storage_and_corruption_is_rejected(self):
        source = self.root / "source"
        source.mkdir()
        (source / "runtime.dll").write_bytes(b"same runtime")
        store = self.root / "store"
        inputs = {"component": "cli"}
        for name in ("one", "two"):
            artifact = store / name
            inventory = COMPONENTS.materialize(source, artifact / "payload", store)
            COMPONENTS.atomic(artifact / "artifact.json", {"schema": COMPONENTS.SCHEMA, "inputs": inputs, "files": inventory})
            self.assertIsNotNone(COMPONENTS.read_artifact(artifact, inputs))
        one = store / "one/payload/runtime.dll"
        two = store / "two/payload/runtime.dll"
        self.assertTrue(os.path.samefile(one, two))
        one.write_bytes(b"corrupt runtime")
        self.assertIsNone(COMPONENTS.read_artifact(store / "one", inputs))
        self.assertIsNone(COMPONENTS.read_artifact(store / "two", inputs))

    def test_complete_input_receipt_survives_json_roundtrip_and_reuses_output(self):
        source = self.root / "source"
        source.mkdir()
        (source / "app.dll").write_bytes(b"qualified component")
        store = self.root / "store"
        artifact = store / "cli"
        inputs = self.inputs("cli")
        inventory = COMPONENTS.materialize(source, artifact / "payload", store)
        COMPONENTS.atomic(artifact / "artifact.json", {"schema": COMPONENTS.SCHEMA, "inputs": inputs, "files": inventory})
        self.assertIsNotNone(COMPONENTS.read_artifact(artifact, inputs))


if __name__ == "__main__":
    unittest.main(verbosity=2)
