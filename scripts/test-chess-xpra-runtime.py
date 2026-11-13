#!/usr/bin/env python3
"""Boundary tests for private Xpra acquisition; no network or host mutation."""
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch

SOURCE = Path(__file__).resolve().parent / "lib/chess_xpra_runtime.py"
SPEC = importlib.util.spec_from_file_location("test_chess_xpra_runtime_owner", SOURCE)
OWNER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(OWNER)


def release():
    return {"schema": "laplace.cutechess-user-runtime-release/v1", "version": "6.5.3",
            "debian_version": OWNER.VERSION, "distribution": "jammy", "architecture": "amd64",
            "repository": "https://xpra.org", "signing_fingerprint": OWNER.FINGERPRINT,
            "packages": [{"name": name, "size": size, "sha256": sha}
                         for name, size, sha in OWNER.PINS]}


class AcquisitionBoundaries(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name)
        self.root = self.base / "root"
        self.root.mkdir()
        self.counter = 0

    def payload(self, entries):
        self.counter += 1
        path = self.base / ("payload-" + str(self.counter) + ".tar")
        with tarfile.open(path, "w") as archive:
            for name, kind, value, mode in entries:
                item = tarfile.TarInfo(name)
                item.mode = mode
                if kind == "file":
                    data = value.encode()
                    item.size = len(data)
                    archive.addfile(item, io.BytesIO(data))
                else:
                    item.type = {"symlink": tarfile.SYMTYPE, "hardlink": tarfile.LNKTYPE,
                                 "fifo": tarfile.FIFOTYPE, "directory": tarfile.DIRTYPE}[kind]
                    item.linkname = value
                    archive.addfile(item)
        return path

    def extract(self, entries, budget=None):
        OWNER.extract_payload(self.payload(entries), self.root,
                              budget if budget is not None else {"count": 0, "bytes": 0})

    def test_release_rejects_changed_origin_key_and_bytes(self):
        OWNER.check_release(release())
        for field, value in (("repository", "https://example.invalid"),
                             ("signing_fingerprint", "0" * 40), ("debian_version", "6.5.4")):
            changed = release()
            changed[field] = value
            with self.subTest(field=field), self.assertRaises(RuntimeError):
                OWNER.check_release(changed)
        changed = release()
        changed["packages"][0]["sha256"] = "0" * 64
        with self.assertRaises(RuntimeError):
            OWNER.check_release(changed)

    def test_explicit_five_package_profile_rejects_old_lock_and_missing_client(self):
        selected = release()
        self.assertEqual([item["name"] for item in selected["packages"]],
                         ["xpra-common", "xpra-server", "xpra-x11", "xpra-client", "xpra-client-gtk3"])
        for change in ("schema", "missing", "extra"):
            value = copy.deepcopy(selected)
            if change == "schema":
                value["schema"] = "laplace.cutechess-session-release/v1"
            elif change == "missing":
                value["packages"] = value["packages"][:3]
            else:
                value["packages"].append({"name": "xpra-codecs", "size": 1, "sha256": "0" * 64})
            with self.subTest(change=change), self.assertRaises(RuntimeError):
                OWNER.check_release(value)

    def test_key_accepts_matching_primary_and_rejects_extra_primary(self):
        primary = "pub:::::::::\nfpr:::::::::" + OWNER.FINGERPRINT + ":\n"
        subkey = "sub:::::::::\nfpr:::::::::" + "A" * 40 + ":\n"
        OWNER.key_fingerprint(primary + subkey)
        with self.assertRaises(RuntimeError):
            OWNER.key_fingerprint(primary + "pub:::::::::\nfpr:::::::::" + "B" * 40 + ":\n")

    def test_dependency_selection_rejects_removal_and_core_replacement(self):
        self.assertEqual(OWNER.simulation_packages(""), [])
        self.assertEqual(OWNER.simulation_packages("Inst python3-pil (9.0.1 Ubuntu:22.04/jammy [amd64])"),
                         [("python3-pil", "9.0.1")])
        for text in ("Remv libc6 [2.35]", "Inst libc6 (2.36 Ubuntu [amd64])",
                     "Inst python3.10 (3.10.14 Ubuntu [amd64])", "Inst invalid"):
            with self.subTest(text=text), self.assertRaises(RuntimeError):
                OWNER.simulation_packages(text)

    def test_authenticated_package_rejects_wrong_filename_or_hash(self):
        name, size, sha = OWNER.PINS[0]
        text = ("Package: " + name + "\nVersion: " + OWNER.VERSION + "\nArchitecture: amd64\n"
                + "Size: " + str(size) + "\nSHA256: " + sha + "\nFilename: "
                + "dists/jammy/main/binary-amd64/" + name + "_" + OWNER.VERSION + "_amd64.deb\n")
        self.assertEqual(OWNER.package_record(text, name, OWNER.VERSION)["sha256"], sha)
        for changed in (text.replace(sha, "0" * 64), text.replace("dists/jammy", "dists/noble")):
            with self.assertRaises(RuntimeError):
                OWNER.package_record(changed, name, OWNER.VERSION)

    def test_paths_cannot_escape(self):
        for name in ("../outside", "/outside", "usr/../../outside"):
            with self.subTest(name=name), self.assertRaises(RuntimeError):
                self.extract([(name, "file", "owned", 0o644)])
        self.assertFalse((self.base / "outside").exists())

    def test_symlink_cannot_escape(self):
        with self.assertRaises(RuntimeError):
            self.extract([("usr/link", "symlink", "../../outside", 0o777)])
        self.assertFalse((self.base / "outside").exists())

    def test_absolute_package_link_is_rebased_inside_generation(self):
        self.extract([("usr/lib/example.so.1", "file", "library", 0o644),
                      ("usr/lib/example.so", "symlink", "/usr/lib/example.so.1", 0o777)])
        path = self.root / "usr/lib/example.so"
        self.assertEqual(path.read_text(), "library")
        self.assertEqual(os.readlink(path), "example.so.1")
        self.assertTrue(path.resolve().is_relative_to(self.root))

    def test_package_link_parent_cannot_redirect_file_write(self):
        self.extract([("usr/real/example", "file", "old", 0o644),
                      ("usr/link", "symlink", "real", 0o777)])
        with self.assertRaises(RuntimeError):
            self.extract([("usr/link/example", "file", "changed", 0o644)])
        self.assertEqual((self.root / "usr/real/example").read_text(), "old")

    def test_special_files_and_setuid_are_rejected(self):
        for item in (("fifo", "fifo", "", 0o644), ("tool", "file", "x", 0o4755)):
            with self.subTest(item=item), self.assertRaises(RuntimeError):
                self.extract([item])

    def test_hard_link_is_internal_and_external_target_rejected(self):
        self.extract([("usr/a", "file", "data", 0o644), ("usr/b", "hardlink", "usr/a", 0o644)])
        self.assertEqual((self.root / "usr/a").stat().st_ino, (self.root / "usr/b").stat().st_ino)
        with self.assertRaises(RuntimeError):
            self.extract([("usr/c", "hardlink", "../outside", 0o644)])

    def test_byte_and_member_budgets_are_enforced(self):
        with self.assertRaises(RuntimeError):
            self.extract([("big", "file", "12", 0o644)],
                         {"count": 0, "bytes": OWNER.MAX_EXPANDED - 1})
        with self.assertRaises(RuntimeError):
            self.extract([("extra", "file", "x", 0o644)],
                         {"count": OWNER.MAX_FILES, "bytes": 0})

    def test_shared_files_must_agree_and_inventory_detects_change(self):
        self.extract([("usr/example", "file", "same", 0o644)])
        self.extract([("usr/example", "file", "same", 0o644)])
        before = OWNER.file_inventory(self.root)
        with self.assertRaises(RuntimeError):
            self.extract([("usr/example", "file", "other", 0o644)])
        (self.root / "usr/example").write_text("changed")
        self.assertNotEqual(before, OWNER.file_inventory(self.root))

    def test_selected_environment_uses_owned_prefixes_and_clears_injection(self):
        for path in ("usr/bin", "usr/lib/python3/dist-packages",
                     "usr/lib/x86_64-linux-gnu/girepository-1.0", "usr/share"):
            (self.root / path).mkdir(parents=True, exist_ok=True)
        selected = {"root": str(self.root)}
        with patch.object(OWNER, "selected_x11", return_value=selected):
            env = OWNER.selected_environment({"root": str(self.root)}, {
                "PATH": "/untrusted", "PYTHONPATH": "/untrusted", "LD_PRELOAD": "/untrusted",
                "LD_AUDIT": "/untrusted", "XPRA_BIND_TCP": "0.0.0.0:14500",
                "XPRA_SESSION_DIR": "/run/user/994/laplace-cutechess", "XPRA_PRIVATE_XAUTH": "1"})
        self.assertNotIn("LD_PRELOAD", env)
        self.assertNotIn("LD_AUDIT", env)
        self.assertNotIn("XPRA_BIND_TCP", env)
        self.assertEqual(env["PYTHONPATH"], str(self.root / "usr/lib/python3/dist-packages"))
        self.assertEqual(env["XPRA_INSTALL_PREFIX"], str(self.root / "usr"))
        self.assertEqual(env["XPRA_DEFAULT_CONF_DIRS"], "")
        self.assertNotIn("/untrusted", env["PATH"])
        self.assertEqual(env["XPRA_SESSION_DIR"], "/run/user/994/laplace-cutechess")
        self.assertEqual(env["XPRA_PRIVATE_XAUTH"], "1")

    def test_load_rejects_changed_manifest_before_using_paths(self):
        document = {"schema": OWNER.SCHEMA, "manifest_sha256": "0" * 64}
        (self.root / "current.json").write_text(json.dumps(document))
        with self.assertRaisesRegex(RuntimeError, "manifest identity"):
            OWNER.load(self.root)

    def test_new_owned_directories_ignore_permissive_umask_without_changing_existing(self):
        previous = os.umask(0o002)
        try:
            path = OWNER.owned_directory(self.base / "new/child", create=True)
        finally:
            os.umask(previous)
        self.assertEqual(path.stat().st_mode & 0o777, 0o700)
        self.assertEqual(path.parent.stat().st_mode & 0o777, 0o700)
        existing = self.base / "existing"
        existing.mkdir(mode=0o775)
        existing.chmod(0o775)
        with self.assertRaises(RuntimeError):
            OWNER.owned_directory(existing, create=True)
        self.assertEqual(existing.stat().st_mode & 0o777, 0o775)

    def test_generation_boundary_under_permissive_umask_and_failed_publication(self):
        selection = self.base / "selection"
        work = self.base / "work"
        work.mkdir(mode=0o700)
        runtime_id = "a" * 64
        previous_umask = os.umask(0o002)
        try:
            OWNER.owned_directory(selection, create=True)
            staging = OWNER.create_staging_root(work)
            payload = staging / "payload"
            payload.write_text("authenticated package data")
            payload.chmod(0o644)
            inventory = OWNER.file_inventory(staging)
            generation = selection / "generations" / runtime_id
            selected_root = OWNER.promote_generation(staging, generation, inventory)
            document = {"runtime_id": runtime_id, "root": str(selected_root), "files": inventory}
            OWNER.publish_selection(selection, document)
        finally:
            os.umask(previous_umask)
        self.assertFalse(staging.exists())
        self.assertEqual(selected_root.stat().st_mode & 0o777, 0o700)
        self.assertEqual(generation.stat().st_mode & 0o777, 0o700)
        previous = (selection / "current.json").read_bytes()
        self.assertEqual(json.loads(previous)["root"], str(selected_root))
        # Simulate a permission change at the actual boundary after qualification:
        # publication must fail without replacing the prior current selection.
        selected_root.chmod(0o775)
        with self.assertRaises(RuntimeError):
            OWNER.publish_selection(selection, {**document, "attempt": "must-not-publish"})
        self.assertEqual((selection / "current.json").read_bytes(), previous)
        self.assertEqual(selected_root.stat().st_mode & 0o777, 0o775)

    def test_missing_selection_is_explicit(self):
        self.assertIsNone(OWNER.load(self.root))


if __name__ == "__main__":
    unittest.main()
