#!/usr/bin/env python3
"""Protocol and kernel-file identity controls; these do not simulate chess proof."""
import copy
import http.server
import importlib.util
import json
import mmap
import os
from pathlib import Path
import tempfile
import threading
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("verify_chess_floor_serving",
                                             ROOT / "scripts/verify-chess-floor-serving.py")
owner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(owner)


class ServingEvidenceTests(unittest.TestCase):
    def setUp(self):
        root = os.environ.get("TMPDIR")
        if not root or not Path(root).is_absolute():
            self.fail("TMPDIR must select the permanent test workspace")
        self.temp = tempfile.TemporaryDirectory(prefix="chess-floor-serving-", dir=root)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def test_real_readonly_mapping_binds_inode_and_refuses_replaced_path(self):
        path = self.root / "floor.bin"
        path.write_bytes(b"mapping-protocol-only" * 512)
        before = owner.fact(path)
        with path.open("rb") as source, mmap.mmap(source.fileno(), 0, access=mmap.ACCESS_READ):
            observation = owner.process(os.getpid(), {"position": before})
            self.assertEqual(os.getpid(), observation["pid"])
            self.assertGreater(observation["start_ticks"], 0)
            replacement = self.root / "replacement.bin"
            replacement.write_bytes(path.read_bytes())
            replacement.replace(path)
            self.assertEqual(before["sha256"], owner.fact(path)["sha256"])
            self.assertNotEqual(before["inode"], owner.fact(path)["inode"])
            with self.assertRaisesRegex(ValueError, "exact selected artifact"):
                owner.process(os.getpid(), {"position": owner.fact(path)})
            with self.assertRaisesRegex(ValueError, "changed"):
                owner.require_same_files({"position": before})

    def test_writable_floor_mapping_is_refused(self):
        path = self.root / "floor.bin"
        path.write_bytes(b"protocol" * 512)
        with path.open("r+b") as source, mmap.mmap(source.fileno(), 0, access=mmap.ACCESS_WRITE):
            with self.assertRaisesRegex(ValueError, "writable mapping"):
                owner.process(os.getpid(), {"transition": owner.fact(path)})

    def test_counter_proof_requires_persistent_hit_and_same_process(self):
        before = {"process_id": 7, "position": {"lookup_hits": 1, "lookup_misses": 2},
                  "transition": {"persistent_hits": 10, "novel_hits": 20, "lookup_misses": 30}}
        response = {"rated": True, "uci": "e2e4", "depth": 1, "nodes": 5, "fen": "protocol"}
        after = copy.deepcopy(before)
        after["transition"]["novel_hits"] += 100
        with self.assertRaisesRegex(ValueError, "no persistent"):
            owner.delta(before, after, response, ["e2e4"])
        after["transition"]["persistent_hits"] += 1
        change = owner.delta(before, after, response, ["e2e4"])
        self.assertEqual(1, change["transition"]["persistent_hits"])
        self.assertEqual(100, change["transition"]["novel_hits"])
        with self.assertRaisesRegex(ValueError, "legal substrate"):
            owner.delta(before, after, response | {"rated": False}, ["e2e4"])
        with self.assertRaisesRegex(ValueError, "legal substrate"):
            owner.delta(before, after, response | {"uci": "illegal"}, ["e2e4"])
        after["process_id"] += 1
        with self.assertRaisesRegex(ValueError, "process changed"):
            owner.delta(before, after, response, ["e2e4"])

    def test_readiness_binds_both_actual_floor_record_counts(self):
        floors = {name: {"records": count} for name, count in zip(owner.artifacts.NAMES, (100, 200))}
        chess = {"ready": True, "initialization_completed": True, "process_id": 7,
                 "position": {"is_loaded": True, "record_count": 100, "lookup_hits": 0, "lookup_misses": 0},
                 "transition": {"is_loaded": True, "record_count": 200, "persistent_hits": 0,
                                "novel_hits": 0, "lookup_misses": 0}}
        self.assertIs(chess, owner.ready({"ready": True, "chess_perfcache": chess}, floors))
        chess["transition"]["record_count"] = 201
        with self.assertRaisesRegex(ValueError, "count differs"):
            owner.ready({"ready": True, "chess_perfcache": chess}, floors)

    def test_actual_loopback_json_post_retains_body_and_refuses_redirect(self):
        seen = []
        class Handler(http.server.BaseHTTPRequestHandler):
            def do_POST(self):
                body = self.rfile.read(int(self.headers["Content-Length"]))
                seen.append((self.path, self.headers.get("Authorization"), json.loads(body)))
                response = b'{"protocol":"ok"}'
                self.send_response(200)
                self.send_header("Content-Length", str(len(response)))
                self.end_headers()
                self.wfile.write(response)
            def do_GET(self):
                seen.append((self.path, self.headers.get("Authorization"), None))
                self.send_response(302)
                self.send_header("Location", "/credential-must-not-follow")
                self.end_headers()
            def log_message(self, *args):
                pass
        with Server(Handler) as base:
            payload = {"fen": "protocol", "depth": 1, "substrate": True}
            self.assertEqual({"protocol": "ok"}, owner.request(base, "/chess/bestmove", "fixture-only", payload))
            with self.assertRaises(Exception):
                owner.request(base, "/health/ready", "fixture-only")
        self.assertEqual([("/chess/bestmove", "Bearer fixture-only", payload),
                          ("/health/ready", "Bearer fixture-only", None)], seen)
        for url in ("https://127.0.0.1", "http://example.com", "http://user@127.0.0.1",
                    "http://127.0.0.1/x", "http://127.0.0.1?key=value"):
            with self.assertRaises(ValueError):
                owner.local_base(url)

    def test_actual_trickle_body_obeys_absolute_deadline(self):
        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(200)
                self.send_header("Content-Length", "100")
                self.end_headers()
                try:
                    for _ in range(100):
                        self.wfile.write(b" ")
                        self.wfile.flush()
                        time.sleep(0.04)
                except (BrokenPipeError, ConnectionResetError):
                    pass
            def log_message(self, *args):
                pass
        with Server(Handler) as base:
            started = time.monotonic()
            with self.assertRaises(TimeoutError):
                owner.request(base, "/health/ready", None, seconds=0.2)
            self.assertLess(time.monotonic() - started, 1.5)


class Server:
    def __init__(self, handler):
        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
        self.server.daemon_threads = True
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
    def __enter__(self):
        self.thread.start()
        return "http://127.0.0.1:" + str(self.server.server_port)
    def __exit__(self, *args):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)


if __name__ == "__main__":
    unittest.main()
