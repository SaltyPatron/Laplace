#!/usr/bin/env python3
"""Check proof evidence boundaries without starting Xpra or changing services."""
import importlib.util
import json
from pathlib import Path
import socket
import unittest

SOURCE = Path(__file__).resolve().parent / "check-cutechess-user-session.py"
SPEC = importlib.util.spec_from_file_location("cutechess_user_session_proof_tests", SOURCE)
PROOF = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PROOF)


class ProofBoundaries(unittest.TestCase):
    def test_foreign_runtime_error_text_never_becomes_evidence(self):
        sensitive = "a1" * 32
        result = PROOF.failure_fields(RuntimeError(sensitive))
        self.assertEqual(result, {"error_type": "RuntimeError"})
        self.assertNotIn(sensitive, json.dumps(result))
        self.assertEqual(PROOF.failure_fields(PROOF.ProofError("proof-deadline-expired")),
                         {"error_type": "ProofError", "error_code": "proof-deadline-expired"})

    def test_authentication_rejection_cannot_be_connection_failure(self):
        self.assertTrue(PROOF.authentication_refused(3))
        self.assertTrue(PROOF.authentication_refused(28))
        for status in (0, 1, 2, 7, 18, 20, 31):
            with self.subTest(status=status):
                self.assertFalse(PROOF.authentication_refused(status))

    def test_listener_scope_requires_one_exact_loopback_listener(self):
        PROOF.require_listener_scope([{"address": "127.0.0.1", "port": 14501, "inode": 42}])
        for listeners in ([],
                          [{"address": "0.0.0.0", "port": 14501, "inode": 42}],
                          [{"address": "::", "port": 14501, "inode": 42}],
                          [{"address": "127.0.0.1", "port": 14500, "inode": 42}],
                          [{"address": "127.0.0.1", "port": 14501, "inode": 42},
                           {"address": "127.0.0.1", "port": 14502, "inode": 43}]):
            with self.subTest(listeners=listeners), self.assertRaises(PROOF.ProofError):
                PROOF.require_listener_scope(listeners)

    @unittest.skipUnless(Path("/proc/self/fd").is_dir(), "Linux process socket evidence")
    def test_socket_inventory_tracks_actual_owned_listener_lifecycle(self):
        before = PROOF.owned_tcp_listeners(Path("/proc/self"))
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
            listener.bind(("127.0.0.1", 0))
            listener.listen(1)
            port = listener.getsockname()[1]
            during = PROOF.owned_tcp_listeners(Path("/proc/self"))
            added = [item for item in during if item not in before]
            self.assertEqual(len(added), 1)
            self.assertEqual((added[0]["address"], added[0]["port"]), ("127.0.0.1", port))
            self.assertGreater(added[0]["inode"], 0)
        after = PROOF.owned_tcp_listeners(Path("/proc/self"))
        self.assertNotIn(added[0], after)


if __name__ == "__main__":
    unittest.main()
