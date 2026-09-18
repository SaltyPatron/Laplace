#!/usr/bin/env python3
"""Execute the real isolated SPA publication transaction in a temporary repository."""
from __future__ import annotations

import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class WebOnlyPublicationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="web-publication-")
        self.addCleanup(self.temp.cleanup)
        self.repo = Path(self.temp.name) / "repo"
        self.app = Path(self.temp.name) / "app"
        self.backups = Path(self.temp.name) / "backups"
        self.transaction = Path(self.temp.name) / "managed-transaction.json"
        for path in (
            self.repo / "scripts",
            self.repo / "deploy/linux",
            self.repo / "web/dist/assets",
            self.repo / "web/openapi",
            self.repo / "build",
            self.app / "wwwroot/assets",
            self.backups,
        ):
            path.mkdir(parents=True, exist_ok=True)

        for name in (
            "publish-applications.sh",
            "web-artifact.py",
            "atomic-directory-exchange.py",
        ):
            shutil.copyfile(ROOT / "scripts" / name, self.repo / "scripts" / name)

        (self.repo / "scripts/check-deployed-revision.sh").write_text(
            """#!/usr/bin/env bash
set -euo pipefail
[[ "${FAIL_REVISION_CHECK:-0}" != 1 ]] || exit 43
expected="$1"
actual="$(cat "$LAPLACE_APP_DIR/.laplace-source-revision" 2>/dev/null || true)"
[[ "$actual" == "$expected" ]] || {
  echo "revision mismatch expected=$expected actual=$actual" >&2
  exit 1
}
""",
            encoding="utf-8",
        )
        (self.repo / "scripts/check-deployed-revision.sh").chmod(0o755)

        (self.repo / "deploy/linux/app-dir-contract.sh").write_text(
            """laplace_reconcile_app_dir_contract() {
  [[ -d "$1" && ! -L "$1" ]]
}
laplace_require_app_dir_contract() {
  [[ -d "$1" && ! -L "$1" ]]
}
""",
            encoding="utf-8",
        )

        (self.repo / "web/package-lock.json").write_text(
            '{"lockfileVersion":3}\n', encoding="utf-8"
        )
        (self.repo / "web/openapi/openapi.json").write_text(
            '{"openapi":"3.0.0"}\n', encoding="utf-8"
        )
        (self.repo / "web/dist/index.html").write_text(
            "<html>new web</html>\n", encoding="utf-8"
        )
        (self.repo / "web/dist/assets/app.js").write_text(
            "console.log('new');\n", encoding="utf-8"
        )

        subprocess.run(["git", "init", "-q"], cwd=self.repo, check=True)
        subprocess.run(
            ["git", "config", "user.email", "ci@example.invalid"],
            cwd=self.repo, check=True,
        )
        subprocess.run(
            ["git", "config", "user.name", "CI"], cwd=self.repo, check=True
        )
        subprocess.run(["git", "add", "."], cwd=self.repo, check=True)
        subprocess.run(["git", "commit", "-qm", "candidate"], cwd=self.repo, check=True)
        self.revision = subprocess.run(
            ["git", "rev-parse", "HEAD"], cwd=self.repo, check=True,
            text=True, capture_output=True,
        ).stdout.strip()
        (self.repo / "build/.laplace-source-revision").write_text(
            self.revision + "\n", encoding="utf-8"
        )
        sealed = subprocess.run(
            [
                sys.executable,
                str(self.repo / "scripts/web-artifact.py"),
                "seal",
                "--root",
                str(self.repo),
                "--manifest",
                str(self.repo / "build/.laplace-web-artifact.json"),
            ],
            cwd=self.repo, text=True, capture_output=True,
        )
        self.assertEqual(0, sealed.returncode, sealed.stdout + sealed.stderr)

        self.old_revision = "1" * 40
        (self.app / ".laplace-source-revision").write_text(
            self.old_revision + "\n", encoding="utf-8"
        )
        (self.app / "wwwroot/index.html").write_text(
            "<html>old web</html>\n", encoding="utf-8"
        )
        (self.app / "wwwroot/assets/app.js").write_text(
            "console.log('old');\n", encoding="utf-8"
        )
        (self.app / "wwwroot/.laplace-web-source-revision").write_text(
            self.old_revision + "\n", encoding="utf-8"
        )
        (self.app / "Laplace.Endpoints.OpenAICompat.dll").write_bytes(
            b"unchanged-api"
        )
        self.api_inode = (self.app / "Laplace.Endpoints.OpenAICompat.dll").stat().st_ino
        self.env = dict(
            os.environ,
            LAPLACE_APP_DIR=str(self.app),
            LAPLACE_APP_BACKUP_ROOT=str(self.backups),
            LAPLACE_MANAGED_TRANSACTION_PATH=str(self.transaction),
        )

    def deploy(self, **updates):
        return subprocess.run(
            ["bash", str(self.repo / "scripts/publish-applications.sh"), "web-deploy"],
            cwd=self.repo,
            env=dict(self.env, **updates),
            text=True,
            capture_output=True,
            timeout=30,
        )

    def assert_no_pending_state(self):
        self.assertFalse((self.repo / "build/.web-publish-pending").exists())
        self.assertEqual([], list(self.backups.iterdir()))

    def test_web_only_deploy_updates_only_sealed_spa_and_logical_revision(self):
        result = self.deploy()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual("<html>new web</html>\n", (self.app / "wwwroot/index.html").read_text())
        self.assertEqual(
            self.revision,
            (self.app / "wwwroot/.laplace-web-source-revision").read_text().strip(),
        )
        self.assertEqual(
            self.revision, (self.app / ".laplace-source-revision").read_text().strip()
        )
        api = self.app / "Laplace.Endpoints.OpenAICompat.dll"
        self.assertEqual(b"unchanged-api", api.read_bytes())
        self.assertEqual(self.api_inode, api.stat().st_ino)
        self.assert_no_pending_state()

    def test_failure_after_atomic_swap_restores_old_web_and_revision(self):
        old_inode = (self.app / "wwwroot").stat().st_ino
        result = self.deploy(FAIL_REVISION_CHECK="1")
        self.assertEqual(43, result.returncode, result.stdout + result.stderr)
        self.assertEqual("<html>old web</html>\n", (self.app / "wwwroot/index.html").read_text())
        self.assertEqual(old_inode, (self.app / "wwwroot").stat().st_ino)
        self.assertEqual(
            self.old_revision, (self.app / ".laplace-source-revision").read_text().strip()
        )
        self.assertEqual(
            self.old_revision,
            (self.app / "wwwroot/.laplace-web-source-revision").read_text().strip(),
        )
        self.assert_no_pending_state()


if __name__ == "__main__":
    unittest.main(verbosity=2)
