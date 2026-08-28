#!/usr/bin/env python3
"""Launch a published UCI runtime and require a handshake and legal searched move.

Pure-search packaging check: no database access, game recording or live service
changes. Used before publish switches launch links, and in the CI unit gate.
"""
import argparse
import os
from pathlib import Path
import queue
import subprocess
import tempfile
import threading
import time


def check_runtime(executable, timeout=15):
    executable = str(Path(executable).absolute())
    with tempfile.TemporaryDirectory(prefix="laplace-uci-check-") as logs:
        env = dict(os.environ, LAPLACE_UCI_SUBSTRATE="off", LAPLACE_OPS_LOG_DIR=logs)
        process = subprocess.Popen([executable], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                   stderr=subprocess.DEVNULL, text=True, bufsize=1, env=env)
        lines = queue.Queue(maxsize=1024)

        def receive():
            for line in process.stdout:
                try:
                    lines.put_nowait(line.strip())
                except queue.Full:
                    break
            try:
                lines.put_nowait(None)
            except queue.Full:
                pass

        reader = threading.Thread(target=receive, daemon=True)
        reader.start()

        def send(command):
            process.stdin.write(command + "\n")
            process.stdin.flush()

        def wait_for(prefix):
            deadline = time.monotonic() + timeout
            observed = []
            while time.monotonic() < deadline:
                try:
                    line = lines.get(timeout=max(0.001, deadline - time.monotonic()))
                except queue.Empty:
                    break
                if line is None:
                    raise RuntimeError("UCI process exited before " + prefix)
                observed.append(line)
                if line == prefix or line.startswith(prefix + " "):
                    return line, observed
            raise RuntimeError("UCI did not answer " + prefix + " within its check deadline")

        try:
            send("uci")
            wait_for("uciok")
            send("isready")
            wait_for("readyok")
            send("position startpos")
            send("go depth 1")
            best, observed = wait_for("bestmove")
            legal = {f + "2" + f + rank for f in "abcdefgh" for rank in "34"}
            legal.update({"b1a3", "b1c3", "g1f3", "g1h3"})
            if best.split()[1] not in legal:
                raise RuntimeError("UCI returned no legal starting-position move")
            if not any(line.startswith("info depth 1 ") for line in observed):
                raise RuntimeError("UCI did not complete the requested search depth")
            send("quit")
            if process.wait(timeout=timeout) != 0:
                raise RuntimeError("UCI exited unsuccessfully after quit")
            return best.split()[1]
        finally:
            if process.poll() is None:
                process.kill()
            process.wait(timeout=5)
            reader.join(timeout=5)
            process.stdin.close()
            process.stdout.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable")
    args = parser.parse_args()
    move = check_runtime(args.executable)
    print("UCI runtime passed: uciok, readyok, completed depth 1, legal bestmove " + move)
