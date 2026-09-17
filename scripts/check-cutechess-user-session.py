#!/usr/bin/env python3
"""Prove the installed private CuteChess session through two real GTK attaches."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import pwd
import re
import secrets
import selectors
import signal
import socket
import stat
import subprocess
import tempfile
import time

SCHEMA = "laplace.cutechess-user-session-proof/v1"
ENDPOINT = "tcp://127.0.0.1:14501"
PROOF_DISPLAY = ":121"
TITLE = "Laplace CuteChess qualification"
MAX_OUTPUT = 2 * 1024 * 1024


class ProofError(RuntimeError):
    def __init__(self, code):
        super().__init__(code)
        self.code = code


def require(condition, code):
    if not condition:
        raise ProofError(code)


def failure_fields(error):
    result = {"error_type": type(error).__name__}
    if type(error) is ProofError:
        result["error_code"] = error.code
    return result


def authentication_refused(status):
    return status in (3, 28)


def remaining(deadline, maximum=8):
    value = min(maximum, deadline - time.monotonic())
    require(value > 0, "proof-deadline-expired")
    return value


def stop_owned(child, first=signal.SIGTERM):
    if child is None:
        return
    for signum in (first, signal.SIGKILL):
        try:
            os.killpg(child.pid, signum)
        except ProcessLookupError:
            break
        try:
            child.wait(timeout=2)
        except subprocess.TimeoutExpired:
            continue
        break
    child.wait(timeout=1)


def capture(arguments, environment, deadline, *, maximum=8, input_data=None):
    require(input_data is None or len(input_data) <= 1024, "command-input-exceeds-bound")
    end = time.monotonic() + remaining(deadline, maximum)
    child = subprocess.Popen(arguments, env=environment,
                             stdin=subprocess.PIPE if input_data is not None else subprocess.DEVNULL,
                             stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                             start_new_session=True)
    output = bytearray()
    try:
        if input_data is not None:
            child.stdin.write(input_data)
            child.stdin.close()
        with selectors.DefaultSelector() as selector:
            selector.register(child.stdout, selectors.EVENT_READ)
            while True:
                require(time.monotonic() < end, "command-deadline-expired")
                if not selector.select(max(.001, min(1, end - time.monotonic()))):
                    continue
                value = os.read(child.stdout.fileno(), 65536)
                if not value:
                    break
                output.extend(value)
                require(len(output) <= MAX_OUTPUT, "command-output-exceeds-bound")
        status = child.wait(timeout=max(.01, end - time.monotonic()))
        return status, output.decode("utf-8", errors="strict")
    finally:
        stop_owned(child)
        child.stdout.close()
        if child.stdin is not None:
            child.stdin.close()


def checked_capture(arguments, environment, deadline, *, maximum=8, input_data=None):
    status, output = capture(arguments, environment, deadline, maximum=maximum, input_data=input_data)
    require(status == 0, "command-failed")
    return output


def process_identity(pid, uid):
    require(pid > 1, "invalid-process-id")
    directory = Path("/proc") / str(pid)
    require(directory.stat().st_uid == uid, "process-owner-differs")
    value = (directory / "stat").read_text()
    fields = value[value.rfind(")") + 2:].split()
    require(len(fields) > 19, "process-identity-incomplete")
    return {"pid": pid, "start_ticks": int(fields[19])}


def service_identity(environment, deadline, uid):
    output = checked_capture(["/usr/bin/systemctl", "--user", "show", "laplace-cutechess.service",
                              "-p", "ActiveState", "-p", "SubState", "-p", "MainPID"],
                             environment, deadline)
    fields = dict(line.split("=", 1) for line in output.splitlines() if "=" in line)
    require(fields.get("ActiveState") == "active" and fields.get("SubState") == "running",
            "user-session-is-not-active")
    return process_identity(int(fields["MainPID"]), uid)


def owned_tcp_listeners(directory):
    """Intersect process-owned socket inodes with namespace TCP listener tables."""
    directory = Path(directory)
    inodes = set()
    for path in (directory / "fd").iterdir():
        try:
            match = re.fullmatch(r"socket:\[(\d+)\]", os.readlink(path))
        except FileNotFoundError:
            continue
        if match:
            inodes.add(int(match[1]))
    require(len(inodes) <= 4096, "server-socket-count-exceeds-bound")
    listeners = []
    for filename, family in (("tcp", socket.AF_INET), ("tcp6", socket.AF_INET6)):
        with (directory / "net" / filename).open() as stream:
            text = stream.read(4 * 1024 * 1024 + 1)
        require(len(text) <= 4 * 1024 * 1024, "network-table-exceeds-bound")
        for line in text.splitlines()[1:]:
            fields = line.split()
            require(len(fields) >= 10, "network-table-row-is-incomplete")
            if fields[3] != "0A" or int(fields[9]) not in inodes:
                continue
            encoded, port = fields[1].split(":")
            value = bytes.fromhex(encoded)
            if family == socket.AF_INET:
                value = value[::-1]
            else:
                value = b"".join(value[index:index + 4][::-1] for index in range(0, 16, 4))
            listeners.append({"address": socket.inet_ntop(family, value),
                              "port": int(port, 16), "inode": int(fields[9])})
    return listeners


def require_listener_scope(listeners):
    require(len(listeners) == 1 and listeners[0]["address"] == "127.0.0.1"
            and listeners[0]["port"] == 14501, "server-listener-scope-differs")


def listener_audit(pid):
    listeners = owned_tcp_listeners(Path("/proc") / str(pid))
    require_listener_scope(listeners)
    return listeners


def xpra_command(selected, mode, password=None):
    command = [selected["tools"]["python"], "-s", "-B", selected["tools"]["xpra"],
               mode, ENDPOINT, "--challenge-handlers=file", "--splash=no",
               "--systemd-run=no"]
    if password is not None:
        command.append("--password-file=" + str(password))
    return command


def window_info(selected, password, environment, deadline):
    # The CLI output is held only in bounded memory. No full server info or
    # environment, credential, filename, or window title is retained.
    output = checked_capture([*xpra_command(selected, "info", password), "window"],
                             environment, deadline)
    windows = {}
    for line in output.splitlines():
        match = re.fullmatch(r"windows\.(\d+)\.(pid|title|class-instance|xid)=(.*)", line)
        if match:
            windows.setdefault(int(match[1]), {})[match[2]] = match[3]
    matches = []
    for window_id, values in windows.items():
        if re.search(r"cute\s*chess", values.get("title", "") + " " +
                     values.get("class-instance", ""), re.I):
            pid = values.get("pid", "")
            if pid.isdecimal() and int(pid) > 1:
                matches.append({"window_id": window_id, "pid": int(pid),
                                "title_sha256": hashlib.sha256(values.get("title", "").encode()).hexdigest()})
    require(bool(matches), "native-cutechess-window-is-absent")
    return sorted(matches, key=lambda item: item["window_id"])


def wait_for_server_ready(selected, password, environment, deadline, uid):
    """Wait for this active service to publish its authenticated native window."""
    identity = service_identity(environment, deadline, uid)
    ready_deadline = min(deadline, time.monotonic() + 25)
    transient = {"native-cutechess-window-is-absent", "command-failed", "command-deadline-expired"}
    while time.monotonic() < ready_deadline:
        require(service_identity(environment, ready_deadline, uid) == identity,
                "service-changed-during-readiness")
        listeners = owned_tcp_listeners(Path("/proc") / str(identity["pid"]))
        if listeners:
            # Any unexpected address, port or additional listener fails immediately.
            require_listener_scope(listeners)
            try:
                windows = window_info(selected, password, environment, ready_deadline)
            except ProofError as error:
                if error.code not in transient:
                    raise
            else:
                require(service_identity(environment, ready_deadline, uid) == identity,
                        "service-changed-during-readiness")
                current = owned_tcp_listeners(Path("/proc") / str(identity["pid"]))
                require_listener_scope(current)
                require(current == listeners, "server-listeners-changed-during-readiness")
                return identity, listeners, windows
        time.sleep(min(.2, max(0, ready_deadline - time.monotonic())))
    raise ProofError("server-readiness-deadline-expired")


def wait_for_client_window(child, tools, environment, deadline, native_window_id):
    end = time.monotonic() + remaining(deadline, 25)
    while time.monotonic() < end:
        require(child.poll() is None, "gtk-client-exited-before-window")
        status, text = capture([tools["xdotool"], "search", "--onlyvisible", "--name", "^" + TITLE + " " + str(native_window_id) + "$"],
                               environment, deadline, maximum=3)
        if status == 0:
            identifiers = [int(value) for value in text.split() if value.isdecimal()]
            require(len(identifiers) <= 16, "client-window-count-exceeds-bound")
            for identifier in identifiers:
                geometry = checked_capture([tools["xdotool"], "getwindowgeometry", "--shell",
                                            str(identifier)], environment, deadline, maximum=3)
                fields = dict(line.split("=", 1) for line in geometry.splitlines() if "=" in line)
                width, height = int(fields.get("WIDTH", "0")), int(fields.get("HEIGHT", "0"))
                if width >= 320 and height >= 240:
                    return {"xid": identifier, "width": width, "height": height,
                            "native_window_id": native_window_id}
        time.sleep(min(.2, max(0, end - time.monotonic())))
    raise ProofError("gtk-client-window-did-not-map")


def attach_once(selected, password, tools, environment, deadline, native_window_id):
    command = xpra_command(selected, "attach", password) + [
        "--title=" + TITLE + " @windowid@", "--readonly=yes", "--clipboard=no", "--notifications=no",
        "--tray=no", "--system-tray=no", "--opengl=no", "--speaker=no", "--microphone=no",
        "--webcam=no", "--printing=no", "--file-transfer=no", "--open-files=no",
        "--open-url=no", "--mmap=no", "--sharing=yes", "--encodings=rgb,png",
    ]
    child = subprocess.Popen(command, env=environment, stdin=subprocess.DEVNULL,
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                             start_new_session=True)
    try:
        mapped = wait_for_client_window(child, tools, environment, deadline, native_window_id)
        # Xpra's own SIGINT handler sends disconnect, drains cleanup and exits
        # 128+SIGINT. Signal only this newly created client process.
        child.send_signal(signal.SIGINT)
        status = child.wait(timeout=remaining(deadline, 8))
        require(status in (0, 130), "gtk-client-did-not-detach-cleanly")
        return {"client_pid": child.pid, "mapped_window": mapped, "detach_exit_code": status}
    finally:
        stop_owned(child)


def run_proof(deadline, checks):
    uid = os.getuid()
    require(uid == os.geteuid() and uid != 0, "proof-requires-current-unprivileged-user")
    home = Path(pwd.getpwuid(uid).pw_dir)
    install = home / ".local/lib/laplace-cutechess"
    launcher_path = install / "laplace-cutechess-user-session"
    info = launcher_path.lstat()
    require(stat.S_ISREG(info.st_mode) and info.st_uid == uid and not info.st_mode & 0o022,
            "installed-launcher-is-not-owned")
    spec = importlib.util.spec_from_file_location("laplace_user_session_proof_owner", launcher_path)
    # Installed launcher has no .py extension, so provide the source loader.
    if spec is None:
        from importlib.machinery import SourceFileLoader
        spec = importlib.util.spec_from_loader("laplace_user_session_proof_owner",
                                               SourceFileLoader("laplace_user_session_proof_owner",
                                                                str(launcher_path)))
    require(spec is not None and spec.loader is not None, "installed-launcher-cannot-load")
    launcher = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(launcher)
    config = launcher.configuration(home, uid)
    require(config["transport"] == "loopback-password", "proof-requires-selected-loopback-auth-transport")
    password = Path(config["password_file"])
    owner = launcher.load_owner(install, uid)
    selected = owner.load(launcher.RUNTIME_ROOT)
    require(selected is not None, "selected-xpra-runtime-is-absent")
    x11 = owner.selected_x11(selected)
    environment = owner.selected_environment(selected, {
        "HOME": str(home), "LANG": "C", "LC_ALL": "C",
        "XDG_RUNTIME_DIR": "/run/user/" + str(uid),
        "DBUS_SESSION_BUS_ADDRESS": "unix:path=/run/user/" + str(uid) + "/bus",
    })
    checks["runtime_id"] = selected["runtime_id"]
    checks["runtime_manifest_sha256"] = selected["manifest_sha256"]
    ready_start = time.monotonic()
    before_service, listeners, ready_windows = wait_for_server_ready(
        selected, password, environment, deadline, uid)
    checks["readiness_elapsed_seconds"] = round(time.monotonic() - ready_start, 3)
    checks["service_before"] = before_service
    checks["tcp_listeners_before"] = listeners
    # Never take over, remove a lock from, or kill an existing display.
    require(not Path("/tmp/.X121-lock").exists() and not Path("/tmp/.X11-unix/X121").exists(),
            "proof-display-is-already-owned")
    xvfb = None
    with tempfile.TemporaryDirectory(prefix="cutechess-proof-", dir="/run/user/" + str(uid)) as temporary:
        work = Path(temporary)
        wrong = work / "wrong-password"
        value = secrets.token_hex(32).encode()
        require(value != launcher.read_private(password, uid, 64), "negative-credential-collision")
        wrong.write_bytes(value)
        wrong.chmod(0o600)
        for name, path in (("anonymous", None), ("wrong_password", wrong)):
            status, _ = capture([*xpra_command(selected, "info", path), "window"],
                                environment, deadline)
            checks[name] = {"exit_code": status, "accepted": status == 0}
            require(authentication_refused(status), "authentication-negative-control-differs")
        before = window_info(selected, password, environment, deadline)
        require(before == ready_windows, "native-window-changed-after-readiness")
        checks["native_windows_before"] = before
        native = process_identity(before[0]["pid"], uid)
        checks["native_process_before"] = native
        authority = work / "Xauthority"
        authority.touch(mode=0o600)
        cookie = secrets.token_hex(16)
        # Credential is sent through this owned child's stdin, never argv or logs.
        checked_capture([x11["tools"]["xauth"], "-f", str(authority), "source", "-"],
                        environment, deadline,
                        input_data=("add " + PROOF_DISPLAY + " MIT-MAGIC-COOKIE-1 " + cookie + "\n").encode())
        client_env = dict(environment, DISPLAY=PROOF_DISPLAY, XAUTHORITY=str(authority))
        try:
            xvfb = subprocess.Popen([selected["tools"]["Xvfb"], PROOF_DISPLAY,
                                     "-screen", "0", "1920x1080x24", "-nolisten", "tcp",
                                     "-noreset", "-auth", str(authority)],
                                    env=client_env, stdin=subprocess.DEVNULL,
                                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                    start_new_session=True)
            end = time.monotonic() + remaining(deadline, 5)
            while True:
                require(xvfb.poll() is None, "proof-xvfb-exited")
                status, _ = capture([x11["tools"]["xwininfo"], "-root"], client_env, deadline, maximum=1)
                if status == 0:
                    break
                require(time.monotonic() < end, "proof-xvfb-not-ready")
                time.sleep(.1)
            checks["first_attach"] = attach_once(selected, password, x11["tools"], client_env, deadline,
                                                  before[0]["window_id"])
            between = window_info(selected, password, environment, deadline)
            require(between == before and service_identity(environment, deadline, uid) == before_service
                    and process_identity(native["pid"], uid) == native,
                    "session-changed-after-first-detach")
            checks["persisted_after_first_detach"] = True
            checks["second_attach"] = attach_once(selected, password, x11["tools"], client_env, deadline,
                                                  before[0]["window_id"])
            after = window_info(selected, password, environment, deadline)
            require(after == before and service_identity(environment, deadline, uid) == before_service
                    and process_identity(native["pid"], uid) == native,
                    "session-changed-after-reconnect")
            checks["persisted_after_second_detach"] = True
        finally:
            stop_owned(xvfb)
        require(xvfb.poll() is not None, "proof-xvfb-was-not-reaped")
        checks["proof_xvfb_reaped"] = True
    checks["tcp_listeners_after"] = listener_audit(before_service["pid"])
    require(checks["tcp_listeners_after"] == checks["tcp_listeners_before"],
            "server-listeners-changed-during-proof")
    checks["service_after"] = service_identity(environment, deadline, uid)
    require(checks["service_after"] == before_service, "service-changed-after-proof-cleanup")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("--deadline-seconds", type=int, default=120)
    args = parser.parse_args()
    require(60 <= args.deadline_seconds <= 180, "proof-deadline-range")
    # Exclusive receipt path makes reruns preserve earlier evidence.
    descriptor = os.open(args.output, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    checks = {}
    receipt = {"schema": SCHEMA, "status": "failed", "checks": checks}
    previous = {signum: signal.getsignal(signum) for signum in (signal.SIGTERM, signal.SIGINT)}
    def interrupted(_signum, _frame):
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
        signal.signal(signal.SIGINT, signal.SIG_IGN)
        raise InterruptedError("proof-interrupted")
    for signum in previous:
        signal.signal(signum, interrupted)
    try:
        run_proof(time.monotonic() + args.deadline_seconds, checks)
        receipt["status"] = "passed"
    except (Exception, KeyboardInterrupt) as error:
        # Only this module's explicit ProofError carries a retained local code.
        # Foreign RuntimeError text is never interpreted or emitted.
        receipt.update(failure_fields(error))
    finally:
        try:
            with os.fdopen(descriptor, "w") as stream:
                json.dump(receipt, stream, sort_keys=True, indent=2)
                stream.write("\n")
        finally:
            for signum, handler in previous.items():
                signal.signal(signum, handler)
    return 0 if receipt["status"] == "passed" else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print("CuteChess session proof refused: " + type(error).__name__)
        raise SystemExit(1)
