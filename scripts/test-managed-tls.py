#!/usr/bin/env python3
"""Real loopback TLS tests for the LAN-pinned managed deployment verifier.

Uses disposable certificates and a test-only token; never contacts live services.
"""
import http.server
import importlib.machinery
import importlib.util
import json
import os
from pathlib import Path
import shutil
import socket
import ssl
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]


class TlsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="laplace-tls-test-")
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        loader = importlib.machinery.SourceFileLoader("managed_tls", str(ROOT / "deploy/linux/laplace-managed-deploy"))
        spec = importlib.util.spec_from_loader(loader.name, loader)
        self.host = importlib.util.module_from_spec(spec)
        loader.exec_module(self.host)
        self.cert, self.key = self.base / "ca.crt", self.base / "server.key"
        subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                        "-subj", "/CN=hart-server", "-addext", "subjectAltName=DNS:hart-server",
                        "-keyout", str(self.key), "-out", str(self.cert)],
                       check=True, capture_output=True, timeout=15)
        self.context = ssl.create_default_context(cafile=str(self.cert))
        self.settings = {"hostname": "hart-server", "address": "192.168.1.2"}
        self.requests, self.sni = [], []
        self.status, self.tools = 200, ["health", "api", "query", "mcp_runtime"]
        owner = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *_args):
                pass

            def handle_request(self):
                body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
                message = json.loads(body) if body else {}
                owner.requests.append((self.command, self.path, dict(self.headers), message))
                status, headers = owner.status, {}
                payload = {"ready": True}
                if message.get("method") == "initialize":
                    payload = {"result": {"protocolVersion": "2025-06-18"}}
                    headers["Mcp-Session-Id"] = "test-session"
                elif message.get("method") == "notifications/initialized":
                    status, payload = 202, None
                elif message.get("method") == "tools/list":
                    payload = {"result": {"tools": [{"name": name} for name in owner.tools]}}
                if status == 302:
                    headers["Location"] = "https://must-not-contact.invalid/secret"
                data = json.dumps(payload).encode() if payload is not None else b""
                self.send_response(status)
                self.send_header("Content-Length", str(len(data)))
                for name, value in headers.items():
                    self.send_header(name, value)
                self.end_headers()
                self.wfile.write(data)

            do_GET = do_POST = do_DELETE = handle_request

        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        server_context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        server_context.load_cert_chain(self.cert, self.key)
        server_context.set_servername_callback(lambda _sock, name, _context: self.sni.append(name))
        self.server.socket = server_context.wrap_socket(self.server.socket, server_side=True)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.addCleanup(self.stop)
        self.connect = socket.create_connection
        self.destinations = []

    def stop(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)

    def routed_socket(self, address, **kwargs):
        self.destinations.append(address)
        self.assertEqual(("192.168.1.2", 8443), address)
        return self.connect(self.server.server_address, **kwargs)

    def request(self, settings=None, context=None):
        with patch.object(self.host.socket, "create_connection", side_effect=self.routed_socket):
            return self.host.lan_https_request(settings or self.settings, context or self.context,
                "GET", "/health/ready", {"Authorization": "Bearer test-only-not-a-real-secret"})

    def prepare_mcp(self):
        self.host.ROOT = self.base
        (self.base / "share/laplace").mkdir(parents=True)
        shutil.copyfile(self.cert, self.base / "share/laplace/managed-services-ca.crt")
        (self.base / "secrets").mkdir()
        (self.base / "secrets/mcp.env").write_text("LAPLACE_MCP_TOKEN=test-only-not-a-real-secret\n")
        self.host.host_settings = lambda: self.settings

    def verify_mcp(self):
        with patch.object(self.host.socket, "create_connection", side_effect=self.routed_socket):
            self.host.verify_mcp()

    def test_lan_ip_is_pinned_but_hostname_sni_and_host_are_preserved(self):
        with patch.dict(os.environ, {"https_proxy": "http://must-not-contact.invalid:9999"}):
            status, _, body = self.request()
        self.assertEqual(200, status)
        self.assertEqual({"ready": True}, json.loads(body))
        self.assertEqual([("192.168.1.2", 8443)], self.destinations)
        self.assertEqual(["hart-server"], self.sni)
        self.assertEqual("hart-server:8443", self.requests[0][2]["Host"])

    def test_wrong_hostname_is_rejected_before_http_or_token_transmission(self):
        with self.assertRaises(ssl.SSLCertVerificationError):
            self.request(settings={"hostname": "wrong-host", "address": "192.168.1.2"})
        self.assertEqual([], self.requests)

    def test_untrusted_certificate_is_rejected(self):
        with self.assertRaises(ssl.SSLCertVerificationError):
            self.request(context=ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT))
        self.assertEqual([], self.requests)

    def test_redirect_is_not_followed_with_bearer(self):
        self.status = 302
        with self.assertRaisesRegex(ValueError, "HTTP 302"):
            self.request()
        self.assertEqual(1, len(self.destinations))
        self.assertEqual(1, len(self.requests))

    def test_mcp_initializes_discovers_and_deletes_session_over_tls(self):
        self.prepare_mcp()
        self.verify_mcp()
        self.assertEqual(["initialize", "notifications/initialized", "tools/list", None],
                         [request[3].get("method") for request in self.requests])
        self.assertEqual("DELETE", self.requests[-1][0])
        self.assertEqual("test-session", self.requests[-1][2]["Mcp-Session-Id"])
        self.assertTrue(all(request[1] == "/mcp" for request in self.requests))

    def test_failed_tool_surface_still_deletes_session(self):
        self.prepare_mcp()
        self.tools = []
        with self.assertRaisesRegex(ValueError, "incomplete"):
            self.verify_mcp()
        self.assertEqual("DELETE", self.requests[-1][0])

    def test_deliberate_hostname_resolution_regression_is_detected(self):
        source = (ROOT / "deploy/linux/laplace-managed-deploy").read_text()
        original = 'socket.create_connection((settings["address"], 8443), timeout=15)'
        broken = source.replace(original, 'socket.create_connection((settings["hostname"], 8443), timeout=15)')
        self.assertNotEqual(source, broken)
        namespace = {"__name__": "deliberately_broken_verifier"}
        exec(compile(broken, "deliberately-broken-verifier", "exec"), namespace)
        with patch.object(socket, "create_connection", side_effect=self.routed_socket):
            with self.assertRaises(AssertionError):
                namespace["lan_https_request"](self.settings, self.context, "GET", "/health/ready", {})


if __name__ == "__main__":
    unittest.main(verbosity=2)
