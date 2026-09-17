#!/usr/bin/env python3
"""Check proof evidence boundaries without starting Xpra or changing services."""
import importlib.util
import json
from pathlib import Path
import socket
import unittest
from unittest import mock

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


class ServerReadiness(unittest.TestCase):
    def setUp(self):
        self.now = [0.0]
        self.identity = {"pid": 123, "start_ticks": 456}
        self.listener = [{"address": "127.0.0.1", "port": 14501, "inode": 42}]
        self.windows = [{"window_id": 7, "pid": 234, "title_sha256": "a" * 64}]
        clock = mock.patch.object(PROOF.time, "monotonic", side_effect=lambda: self.now[0])
        sleep = mock.patch.object(PROOF.time, "sleep", side_effect=self.advance)
        self.addCleanup(clock.stop)
        self.addCleanup(sleep.stop)
        clock.start()
        sleep.start()

    def advance(self, value):
        self.now[0] += value

    def test_delayed_listener_then_native_window_becomes_ready(self):
        with mock.patch.object(PROOF, "service_identity", return_value=self.identity), \
             mock.patch.object(PROOF, "owned_tcp_listeners",
                               side_effect=[[], self.listener, self.listener, self.listener]), \
             mock.patch.object(PROOF, "window_info", side_effect=[
                 PROOF.ProofError("native-cutechess-window-is-absent"), self.windows]) as info:
            result = PROOF.wait_for_server_ready({}, None, {}, 120, 994)
        self.assertEqual(result, (self.identity, self.listener, self.windows))
        self.assertEqual(info.call_count, 2)
        self.assertGreater(self.now[0], 0)

    def test_unexpected_listener_fails_without_authentication_or_wait(self):
        for bad in ([{"address": "0.0.0.0", "port": 14501, "inode": 42}],
                    [*self.listener, {"address": "127.0.0.1", "port": 14502, "inode": 43}]):
            with self.subTest(listener=bad), \
                 mock.patch.object(PROOF, "service_identity", return_value=self.identity), \
                 mock.patch.object(PROOF, "owned_tcp_listeners", return_value=bad), \
                 mock.patch.object(PROOF, "window_info") as info:
                with self.assertRaisesRegex(PROOF.ProofError, "server-listener-scope-differs"):
                    PROOF.wait_for_server_ready({}, None, {}, 120, 994)
                info.assert_not_called()
                self.assertEqual(self.now[0], 0)

    def test_service_replacement_during_readiness_fails_immediately(self):
        changed = dict(self.identity, start_ticks=457)
        with mock.patch.object(PROOF, "service_identity", side_effect=[self.identity, changed]), \
             mock.patch.object(PROOF, "owned_tcp_listeners") as listeners:
            with self.assertRaisesRegex(PROOF.ProofError, "service-changed-during-readiness"):
                PROOF.wait_for_server_ready({}, None, {}, 120, 994)
            listeners.assert_not_called()

    def test_readiness_respects_shorter_absolute_deadline(self):
        with mock.patch.object(PROOF, "service_identity", return_value=self.identity), \
             mock.patch.object(PROOF, "owned_tcp_listeners", return_value=[]), \
             mock.patch.object(PROOF, "window_info") as info:
            with self.assertRaisesRegex(PROOF.ProofError, "server-readiness-deadline-expired"):
                PROOF.wait_for_server_ready({}, None, {}, .5, 994)
            info.assert_not_called()
        self.assertEqual(self.now[0], .5)


if __name__ == "__main__":
    unittest.main()
