#!/usr/bin/env python3
"""Artifact-owner controls. Synthetic fixtures prove filesystem/format refusal, not chess corpus or throughput.
The deterministic fixture checksum is explicitly not BLAKE3. A separate test uses the current built
native library when available and pins the actual hash128_blake3 ABI against known vectors.
"""
import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
import unittest
from unittest import mock

SCRIPT = Path(__file__).with_name("chess-floor-artifacts.py")
SPEC = importlib.util.spec_from_file_location("chess_floor_artifacts_under_test", SCRIPT)
owner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(owner)


def fixture_hash(data, size):
    """Synthetic checksum only; never used as native BLAKE3 evidence."""
    return hashlib.sha256(data[:size]).digest()[:16]


def key(number):
    return number.to_bytes(16, "big")


def floor_bytes(kind, rows, checksum=fixture_hash):
    header, width, magic = (128, 80, b"LCHP") if kind == "position" else (64, 32, b"LCHT")
    body = bytearray(header + width * len(rows))
    body[:4] = magic
    struct.pack_into("<IQ", body, 4, 1, len(rows))
    if kind == "position":
        struct.pack_into("<QQ", body, 16, 80, 128)
    for index, row in enumerate(rows):
        offset = header + index * width
        if kind == "position":
            body[offset:offset + 16] = row
        else:
            body[offset:offset + 16], body[offset + 16:offset + 32] = row
    return bytes(body) + checksum(body, len(body))


class ArtifactTests(unittest.TestCase):
    def setUp(self):
        root = os.environ.get("TMPDIR")
        if not root or not Path(root).is_absolute() or not Path(root).is_dir():
            raise RuntimeError("TMPDIR must name the existing permanent test workspace")
        self.temporary = tempfile.TemporaryDirectory(prefix="chess-floor-artifacts-", dir=root)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.prefix = self.root / "installed"
        self.prefix.mkdir()

    def write_json(self, path, value):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value, sort_keys=True) + "\n", encoding="utf-8")

    def fixture_export(self, name="export"):
        root = self.root / name
        root.mkdir()
        (root / "inventory").mkdir()
        inputs = root / "inventory/hydrated-playings.jsonl"
        inputs.write_bytes(b'{"test_only":true,"PlayingId":"fixture"}\n')
        database = {"database": "fixture", "database_oid": "123", "system_identifier": "456"}
        summary_database = {"Database": "fixture", "DatabaseOid": "123", "SystemIdentifier": "456"}
        summary = {
            "Status": "completed", "Scope": "observed-read-interval", "Snapshot": False,
            "ProvesHistoricalCoverage": False, "DatabaseBefore": summary_database,
            "DatabaseAfter": summary_database, "SelectedBefore": 1, "SelectedAfter": 1,
            "Enumerated": 1, "Retained": 1, "White": 1, "Black": 0, "Unclassified": 0,
            "PendingPlayingIds": [], "ReachedEnd": True, "RetainedBytes": inputs.stat().st_size,
            "InputsSha256": owner.sha256(inputs), "Stage": "completed",
        }
        self.write_json(root / "inventory/summary.json", summary)
        (root / "positions.txt").write_text("test-only surface; producer is mocked\n", encoding="utf-8")
        (root / "transitions.bin").write_bytes(floor_bytes("transition", [(key(1), key(101))]))
        paths = {
            "position-surfaces": "positions.txt", "transition-floor": "transitions.bin",
            "inventory-summary": "inventory/summary.json", "witnessed-inputs": "inventory/hydrated-playings.jsonl",
        }
        value = {
            "schema": owner.EXPORT_SCHEMA, "status": "completed", "inventory_complete": True,
            "snapshot": False, "observation_scope": "observed-read-interval",
            "database_before": database, "database_after": database,
            "selected_playings": 1, "exported_playings": 1, "position_occurrences": 2,
            "transition_occurrences": 1, "unique_transitions": 1,
            "files": [{"role": role, "path": path, "bytes": (root / path).stat().st_size,
                       "sha256": owner.sha256(root / path)} for role, path in paths.items()],
        }
        receipt = root / "export-receipt.json"
        self.write_json(receipt, value)
        return receipt, value

    def rebind(self, receipt, value, role):
        row = next(row for row in value["files"] if row["role"] == role)
        path = receipt.parent / row["path"]
        row.update(bytes=path.stat().st_size, sha256=owner.sha256(path))
        self.write_json(receipt, value)

    def pair(self, name, numbers=(1, 2)):
        directory = self.root / name
        directory.mkdir()
        position, transition = (directory / name for name in owner.NAMES)
        position.write_bytes(floor_bytes("position", [key(number) for number in numbers]))
        transition.write_bytes(floor_bytes("transition", [(key(number), key(number + 100)) for number in numbers]))
        facts = [owner.validate_floor(path, kind, fixture_hash)
                 for path, kind in ((position, "position"), (transition, "transition"))]
        # Explicit synthetic seal for publication-state controls. seal_pair itself is
        # exercised separately with its actual call contract and mocked native producer.
        manifest = {
            "schema": owner.PAIR_SCHEMA, "files": dict(zip(owner.NAMES, facts)), "export": None,
            "producer": {"binary_sha256": "1" * 64, "seed_surfaces_sha256": "2" * 64},
            "coverage_note": "synthetic filesystem-control fixture",
        }
        receipt = directory / "pair.json"
        self.write_json(receipt, manifest)
        return position, transition, receipt, manifest

    def install(self, pair):
        return owner.install_pair(self.prefix, pair[0], pair[1], self.root / "unused-test-library",
                                  pair[2], hash_body=fixture_hash)

    def visible_pair(self):
        return tuple((self.prefix / "share/laplace" / name).read_bytes() for name in owner.NAMES)

    def legacy(self, pair):
        share = self.prefix / "share/laplace"
        share.mkdir(parents=True, exist_ok=True)
        for source, name in zip(pair[:2], owner.NAMES):
            shutil.copyfile(source, share / name)
        return share

    def test_only_complete_consistent_export_is_selectable(self):
        receipt, value = self.fixture_export()
        actual = owner.validate_export(receipt)
        self.assertEqual(actual["coverage"]["exported_playings"], 1)
        self.assertEqual(actual["receipt_sha256"], owner.sha256(receipt))
        cases = (
            ("partial", {"status": "partial"}),
            ("incomplete", {"inventory_complete": False}),
            ("snapshot", {"snapshot": True}),
            ("selected-prefix", {"selected_playings": 2}),
            ("positions", {"position_occurrences": 3}),
            ("novelty", {"unique_transitions": 2}),
            ("boolean-count", {"exported_playings": True}),
            ("missing-role", {"files": value["files"][:-1]}),
            ("different-database", {"database_after": {**value["database_after"], "database_oid": "999"}}),
        )
        for name, change in cases:
            with self.subTest(name=name):
                self.write_json(receipt, {**value, **change})
                with self.assertRaises(ValueError):
                    owner.validate_export(receipt)
        self.write_json(receipt, value)

    def test_each_selected_file_and_saved_receipt_identity_is_bound(self):
        for index, role in enumerate(sorted(owner.ROLES)):
            with self.subTest(role=role):
                receipt, value = self.fixture_export("changed-" + str(index))
                row = next(row for row in value["files"] if row["role"] == role)
                path = receipt.parent / row["path"]
                content = bytearray(path.read_bytes())
                content[0] ^= 1
                path.write_bytes(content)  # Same length: size alone is insufficient.
                with self.assertRaises(ValueError):
                    owner.validate_export(receipt)
        receipt, value = self.fixture_export("selected")
        validated = owner.validate_export(receipt)
        self.write_json(self.prefix / "etc/chess-corpus-export.json",
                        {name: validated[name] for name in ("receipt", "receipt_sha256")})
        with mock.patch.dict(os.environ):
            os.environ.pop("LAPLACE_CHESS_CORPUS_EXPORT", None)
            self.assertEqual(owner.selected_export(self.prefix), validated)
            os.environ["LAPLACE_CHESS_CORPUS_EXPORT"] = ""
            self.assertEqual(owner.selected_export(self.prefix), validated)
            os.environ.pop("LAPLACE_CHESS_CORPUS_EXPORT")
            self.write_json(receipt, {**value, "test_note": "changed but still individually valid"})
            with self.assertRaises(ValueError):
                owner.selected_export(self.prefix)

    def test_hashed_inventory_summary_cannot_disagree_with_complete_export(self):
        cases = (
            {"Status": "partial"}, {"Retained": 0}, {"SelectedAfter": 2}, {"ReachedEnd": False},
            {"White": 0}, {"Unclassified": 1, "PendingPlayingIds": ["fixture"]},
            {"InputsSha256": "0" * 64}, {"RetainedBytes": 0},
            {"DatabaseAfter": {"Database": "fixture", "DatabaseOid": "999", "SystemIdentifier": "456"}},
        )
        for index, change in enumerate(cases):
            with self.subTest(change=change):
                receipt, value = self.fixture_export("summary-" + str(index))
                summary_path = receipt.parent / "inventory/summary.json"
                summary = json.loads(summary_path.read_text(encoding="utf-8"))
                self.write_json(summary_path, {**summary, **change})
                self.rebind(receipt, value, "inventory-summary")
                with self.assertRaises(ValueError):
                    owner.validate_export(receipt)

    def test_export_rejects_symlink_ancestors_escape_and_duplicate_roles(self):
        receipt, value = self.fixture_export()
        source = receipt.parent / "positions.txt"
        original = source.read_bytes()
        source.unlink()
        real = self.root / "outside.txt"
        real.write_bytes(original)
        source.symlink_to(real)
        with self.assertRaises(ValueError):
            owner.validate_export(receipt)
        source.unlink()
        source.write_bytes(original)
        inventory = receipt.parent / "inventory"
        inventory.rename(receipt.parent / "real-inventory")
        inventory.symlink_to("real-inventory", target_is_directory=True)
        with self.assertRaises(ValueError):
            owner.validate_export(receipt)
        inventory.unlink()
        (receipt.parent / "real-inventory").rename(inventory)
        for replacement in ("../outside.txt", str(real)):
            invalid = copy.deepcopy(value)
            invalid["files"][0]["path"] = replacement
            self.write_json(receipt, invalid)
            with self.assertRaises(ValueError):
                owner.validate_export(receipt)
        duplicate = copy.deepcopy(value)
        duplicate["files"][-1] = duplicate["files"][0]
        self.write_json(receipt, duplicate)
        with self.assertRaises(ValueError):
            owner.validate_export(receipt)

    def test_fixed_v1_framing_order_and_complete_body_checksum(self):
        for kind in ("position", "transition"):
            with self.subTest(kind=kind):
                rows = [key(1), key(2)] if kind == "position" else [(key(1), key(11)), (key(2), key(12))]
                valid = floor_bytes(kind, rows)
                path = self.root / (kind + ".bin")
                path.write_bytes(valid)
                self.assertEqual(owner.validate_floor(path, kind, fixture_hash)["records"], 2)
                mutations = {}
                for label, offset in (("magic", 0), ("version", 4), ("count", 8)):
                    body = bytearray(valid)
                    body[offset] ^= 1
                    body[-16:] = fixture_hash(body, len(body) - 16)
                    mutations[label] = bytes(body)
                mutations["trailing"] = valid + b"x"
                mutations["truncated"] = valid[:-1]
                bad_checksum = bytearray(valid)
                bad_checksum[-1] ^= 1
                mutations["checksum"] = bytes(bad_checksum)
                duplicates = [rows[0], rows[0]]
                mutations["duplicate"] = floor_bytes(kind, duplicates)
                mutations["reversed"] = floor_bytes(kind, list(reversed(rows)))
                if kind == "position":
                    body = bytearray(valid)
                    struct.pack_into("<Q", body, 16, 79)
                    body[-16:] = fixture_hash(body, len(body) - 16)
                    mutations["record-width"] = bytes(body)
                for label, invalid in mutations.items():
                    with self.subTest(kind=kind, defect=label):
                        path.write_bytes(invalid)
                        with self.assertRaises(ValueError):
                            owner.validate_floor(path, kind, fixture_hash)
                path.write_bytes(valid)
                with self.assertRaises(ValueError):
                    owner.validate_floor(path, "unknown-kind", fixture_hash)

    def test_transition_coverage_requires_every_exact_corpus_result(self):
        corpus = self.root / "corpus.bin"
        merged = self.root / "merged.bin"
        corpus.write_bytes(floor_bytes("transition", [(key(2), key(12)), (key(4), key(14))]))
        merged.write_bytes(floor_bytes("transition", [(key(i), key(i + 10)) for i in range(1, 6)]))
        owner.require_transition_coverage(corpus, merged, fixture_hash)
        for rows in ([(key(2), key(12))],
                     [(key(2), key(12)), (key(4), key(99))],
                     [(key(1), key(11)), (key(4), key(14))]):
            merged.write_bytes(floor_bytes("transition", rows))
            with self.assertRaises(ValueError):
                owner.require_transition_coverage(corpus, merged, fixture_hash)
        corpus.write_bytes(floor_bytes("transition", []))
        owner.require_transition_coverage(corpus, merged, fixture_hash)

    def test_seal_invokes_exact_producer_inputs_and_binds_the_observed_pair(self):
        exported, _ = self.fixture_export()
        pair = self.pair("built", (1, 2))
        emitter, seed = self.root / "emitter", self.root / "seed.txt"
        emitter.write_bytes(b"test-only mocked producer")
        seed.write_bytes(b"test-only seed")
        with mock.patch.object(owner, "native_hasher", return_value=fixture_hash), \
             mock.patch.object(owner.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)) as run:
            manifest = owner.seal_pair(pair[0], pair[1], self.root / "fake-library",
                                       emitter, seed, pair[2], exported)
        args = run.call_args.args[0]
        self.assertEqual(args[0], str(emitter))
        self.assertIn("--verify-existing", args)
        self.assertEqual(args[args.index("--output") + 1], str(pair[0]))
        self.assertEqual(args[args.index("--surfaces") + 1], str(seed))
        self.assertEqual(args[args.index("--additional-surfaces") + 1],
                         str(exported.parent / "positions.txt"))
        self.assertEqual(manifest["producer"]["binary_sha256"], owner.sha256(emitter))
        self.assertEqual(manifest["producer"]["seed_surfaces_sha256"], owner.sha256(seed))
        self.assertEqual(manifest["export"], owner.validate_export(exported))
        self.assertEqual(owner.validate_pair_receipt(pair[2],
                         [owner.validate_floor(pair[0], "position", fixture_hash),
                          owner.validate_floor(pair[1], "transition", fixture_hash)]), manifest)

    def test_seed_only_seal_and_install_uses_the_same_full_pair_owner(self):
        pair = self.pair("seed-only", (1,))
        emitter, seed = self.root / "seed-producer", self.root / "seed-surfaces"
        emitter.write_bytes(b"test-only mocked producer")
        seed.write_bytes(b"test-only seed")
        with mock.patch.object(owner, "native_hasher", return_value=fixture_hash), \
             mock.patch.object(owner.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)) as run:
            manifest = owner.seal_pair(pair[0], pair[1], self.root / "fake-library",
                                       emitter, seed, pair[2], export=None)
        self.assertIsNone(manifest["export"])
        self.assertNotIn("--additional-surfaces", run.call_args.args[0])
        installed = self.install(pair)
        self.assertEqual(self.visible_pair(), tuple(path.read_bytes() for path in pair[:2]))
        self.assertEqual(owner.read_json(Path(installed["receipt"]))["export"], None)

    def test_changed_producer_or_seed_during_verification_cannot_be_sealed(self):
        for changed in ("emitter", "seed"):
            with self.subTest(changed=changed):
                pair = self.pair("built-" + changed)
                emitter = self.root / (changed + "-producer")
                seed = self.root / (changed + "-surfaces")
                emitter.write_bytes(b"producer-before")
                seed.write_bytes(b"surfaces-before")
                before = pair[2].read_bytes()
                def verification(*args, **kwargs):
                    (emitter if changed == "emitter" else seed).write_bytes(b"changed-during-verification")
                    return subprocess.CompletedProcess([], 0)
                with mock.patch.object(owner, "native_hasher", return_value=fixture_hash), \
                     mock.patch.object(owner.subprocess, "run", side_effect=verification):
                    with self.assertRaises(ValueError):
                        owner.seal_pair(pair[0], pair[1], self.root / "fake-library",
                                        emitter, seed, pair[2])
                self.assertEqual(pair[2].read_bytes(), before)

    def test_replaced_position_after_native_verification_cannot_acquire_a_seal(self):
        pair = self.pair("changed-position")
        emitter, seed = self.root / "producer", self.root / "seed"
        emitter.write_bytes(b"producer")
        seed.write_bytes(b"seed")
        before = pair[2].read_bytes()
        def verification(*args, **kwargs):
            # Simulates native verification of the old bytes followed by replacement
            # before return. The substitute has valid framing/order/checksum.
            pair[0].write_bytes(floor_bytes("position", [key(99)]))
            return subprocess.CompletedProcess([], 0)
        with mock.patch.object(owner, "native_hasher", return_value=fixture_hash), \
             mock.patch.object(owner.subprocess, "run", side_effect=verification):
            with self.assertRaises(ValueError):
                owner.seal_pair(pair[0], pair[1], self.root / "fake-library",
                                emitter, seed, pair[2])
        self.assertEqual(pair[2].read_bytes(), before)

    def test_legacy_adoption_exposes_only_complete_old_or_new_pair_and_is_idempotent(self):
        old, new = self.pair("old", (1,)), self.pair("new", (1, 2))
        share = self.legacy(old)
        before = self.visible_pair()
        after = tuple(path.read_bytes() for path in new[:2])
        observed = []
        replace = owner.replace_link
        def observe(path, target):
            replace(path, target)
            observed.append(self.visible_pair())
        with mock.patch.object(owner, "replace_link", side_effect=observe):
            first = self.install(new)
        self.assertTrue(observed)
        self.assertTrue(all(pair in (before, after) for pair in observed))
        self.assertEqual(self.visible_pair(), after)
        generations = set((share / "chess-floor/generations").iterdir())
        again = self.install(new)
        self.assertEqual(again["generation"], first["generation"])
        self.assertEqual(set((share / "chess-floor/generations").iterdir()), generations)
        for name in owner.NAMES:
            self.assertEqual(os.readlink(share / name), "chess-floor/current/" + name)
        self.assertTrue(all(Path(path).is_file() for path in first["manifest_files"]))
        self.assertNotIn(str(share / "chess-floor/current"), first["manifest_files"])

    def test_interruption_before_switch_retains_old_pair_and_retry_completes(self):
        old, new = self.pair("old", (1,)), self.pair("new", (1, 2))
        share = self.legacy(old)
        before = self.visible_pair()
        replace = owner.replace_link
        def interrupt(path, target):
            if path.name == "current" and path.is_symlink() and os.readlink(path) != target:
                raise OSError("controlled interruption at publication commit point")
            replace(path, target)
        with mock.patch.object(owner, "replace_link", side_effect=interrupt):
            with self.assertRaises(OSError):
                self.install(new)
        self.assertEqual(self.visible_pair(), before)
        self.assertTrue(all((share / name).is_symlink() for name in owner.NAMES))
        self.install(new)
        self.assertEqual(self.visible_pair(), tuple(path.read_bytes() for path in new[:2]))
        self.assertFalse(any(path.name.startswith(".staging-")
                             for path in (share / "chess-floor/generations").iterdir()))

    def test_partial_legacy_and_dangling_current_are_refused_without_switch(self):
        old, new = self.pair("old", (1,)), self.pair("new", (1, 2))
        share = self.legacy(old)
        (share / owner.NAMES[1]).unlink()
        before = (share / owner.NAMES[0]).read_bytes()
        with self.assertRaises(ValueError):
            self.install(new)
        self.assertEqual((share / owner.NAMES[0]).read_bytes(), before)
        current = share / "chess-floor/current"
        self.assertFalse(current.is_symlink())
        shutil.copyfile(old[1], share / owner.NAMES[1])
        current.symlink_to("generations/" + "0" * 64)
        with self.assertRaises((ValueError, OSError)):
            self.install(new)
        self.assertEqual(os.readlink(current), "generations/" + "0" * 64)

    def test_retained_generation_conflict_and_changed_copy_source_cannot_switch(self):
        old, new = self.pair("old", (1,)), self.pair("new", (1, 2))
        self.install(old)
        before = self.visible_pair()
        root = self.prefix / "share/laplace/chess-floor"
        retained = owner.retain_generation(root, new[:2], new[3])
        corrupt = retained / owner.NAMES[1]
        content = bytearray(corrupt.read_bytes())
        content[-1] ^= 1
        corrupt.write_bytes(content)
        with self.assertRaises(ValueError):
            self.install(new)
        self.assertEqual(self.visible_pair(), before)
        shutil.rmtree(retained)
        copy_verified = owner.copy_verified
        def changed(source, target, expected):
            if Path(source) == new[0]:
                Path(source).write_bytes(floor_bytes("position", [key(9)]))
            return copy_verified(source, target, expected)
        with mock.patch.object(owner, "copy_verified", side_effect=changed):
            with self.assertRaises(ValueError):
                self.install(new)
        self.assertEqual(self.visible_pair(), before)

    def test_native_hash_abi_and_floor_checksum_when_current_library_is_available(self):
        explicit = os.environ.get("LAPLACE_TEST_NATIVE_CORE")
        build = os.environ.get("LAPLACE_ENGINE_BUILD")
        library = Path(explicit) if explicit else (
            Path(build) / "core/liblaplace_core.so" if build else
            SCRIPT.resolve().parents[1] / "build/engine/core/liblaplace_core.so")
        if not library.is_file():
            if explicit:
                self.fail("LAPLACE_TEST_NATIVE_CORE names a missing current-build artifact")
            self.skipTest("current built liblaplace_core.so is unavailable; no installed-library fallback")
        hasher = owner.native_hasher(library.resolve())
        self.assertEqual(hasher(bytearray(b"\0"), 0).hex(), "af1349b9f5f9a1a6a0404dea36dcc949")
        self.assertEqual(hasher(bytearray(b"abc"), 3).hex(), "6437b3ac38465133ffb63b75273a8db5")
        path = self.root / "native-checksummed.bin"
        path.write_bytes(floor_bytes("transition", [(key(1), key(2))], hasher))
        self.assertEqual(owner.validate_floor(path, "transition", hasher)["records"], 1)
        corrupt = bytearray(path.read_bytes())
        corrupt[64] ^= 1
        path.write_bytes(corrupt)
        with self.assertRaisesRegex(ValueError, "checksum"):
            owner.validate_floor(path, "transition", hasher)


if __name__ == "__main__":
    unittest.main(verbosity=2)
