#!/usr/bin/env python3
"""Regression tests for the deployed MCP launcher/protocol acceptance contract."""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
PROBE = REPO_ROOT / "scripts" / "probe-mcp-stdio.py"
DEPLOY = REPO_ROOT / "deploy" / "linux" / "deploy.sh"


FAKE_SERVER = r'''#!/usr/bin/env python3
import json
import sys

mode = sys.argv[1] if len(sys.argv) > 1 else "ok"
tools = [{"name": name} for name in ("health", "api", "facts")]
for line in sys.stdin:
    if not line.strip():
        continue
    req = json.loads(line)
    method = req.get("method")
    rid = req.get("id")
    if method == "initialize":
        result = {
            "protocolVersion": "2025-06-18",
            "capabilities": {"tools": {}},
            "serverInfo": {"name": "laplace-substrate", "version": "test-build"},
        }
    elif method == "tools/list":
        result = {"tools": tools if mode != "missing-tool" else tools[:-1]}
    elif method == "tools/call":
        name = req.get("params", {}).get("name")
        result = {
            "content": [{"type": "text", "text": "fixture"}],
            "isError": mode == "tool-error" and name == "health",
        }
    else:
        print(json.dumps({"jsonrpc": "2.0", "id": rid, "error": {"code": -32601, "message": "unknown"}}), flush=True)
        continue
    print(json.dumps({"jsonrpc": "2.0", "id": rid, "result": result}), flush=True)
'''


class McpDeployContractTests(unittest.TestCase):
    def _fake(self, root: Path, mode: str) -> Path:
        server = root / f"fake-mcp-{mode}"
        # Bake the mode into argv so the probe can execute the fixture exactly as
        # it executes a deployed apphost: one path, no fixture-only CLI flags.
        body = FAKE_SERVER.replace(
            'mode = sys.argv[1] if len(sys.argv) > 1 else "ok"',
            f'mode = "{mode}"',
        )
        server.write_text(body, encoding="utf-8")
        server.chmod(0o755)
        return server

    def _probe(self, executable: Path) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [sys.executable, str(PROBE), str(executable), "--timeout", "2"],
            cwd=REPO_ROOT,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=10,
            check=False,
        )

    def test_protocol_probe_accepts_full_contract(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            result = self._probe(self._fake(Path(td), "ok"))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("MCP stdio probe OK", result.stdout)
        self.assertIn("tools=3", result.stdout)

    def test_protocol_probe_rejects_tool_error(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            result = self._probe(self._fake(Path(td), "tool-error"))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("tool 'health' reported isError=True", result.stderr)

    def test_protocol_probe_rejects_stale_catalog(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            result = self._probe(self._fake(Path(td), "missing-tool"))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("missing required deployed tools: facts", result.stderr)

    def test_deploy_resolves_launcher_and_runs_protocol_probe(self) -> None:
        deploy = DEPLOY.read_text(encoding="utf-8")
        self.assertIn('readlink -f "$APP_DIR/laplace-mcp"', deploy)
        self.assertIn('probe-mcp-stdio.py" "$mcp_target"', deploy)
        self.assertNotIn('laplace-mcp" </dev/null', deploy)


if __name__ == "__main__":
    unittest.main()
