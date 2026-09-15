#!/usr/bin/env python3
"""Expose canonical CI phases as separate steps under one run-scoped host lock.

Private named pipes connect the steps to a supervisor which retains the runner's
tracking environment. A lock-owning guardian
uses a Linux pidfd to clean up the active process group if the supervisor dies.
Phase children never inherit the host lock (a daemonized database must not own it).
Only the selected canonical script's ordered phase names are accepted.
"""
from __future__ import annotations

import argparse
import base64
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import select
import signal
import socket
import stat
import subprocess
import sys
import time

SCHEMA = "laplace.ci-session.v1"
LIMIT = 65536
IDENTITY_KEYS = ("GITHUB_REPOSITORY", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT", "GITHUB_JOB", "RUNNER_TRACKING_ID")


def identity():
    # Hash job identity; never persist the environment or credentials.
    return hashlib.sha256(json.dumps({k: os.environ.get(k, "") for k in IDENTITY_KEYS}, sort_keys=True).encode()).hexdigest()


def source(root):
    def git(*args):
        return subprocess.check_output(["git", "-C", str(root), *args], stderr=subprocess.DEVNULL, text=True).strip()
    if subprocess.call(["git", "-C", str(root), "diff", "--quiet", "HEAD", "--"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL):
        raise ValueError("CI checkout has tracked changes")
    return {"commit": git("rev-parse", "HEAD"), "tree": git("rev-parse", "HEAD^{tree}")}


def read_state(directory):
    if directory.is_symlink() or directory.stat().st_uid != os.getuid() or directory.stat().st_mode & 0o077:
        raise ValueError("CI session directory must be private and owned by this user")
    path = directory / "session.json"
    if path.is_symlink() or path.stat().st_size > LIMIT:
        raise ValueError("invalid CI session receipt")
    state = json.loads(path.read_bytes())
    if state.get("schema") != SCHEMA or state.get("identity") != identity():
        raise ValueError("CI session belongs to another run or job")
    return state


def save(directory, state):
    data = json.dumps(state, sort_keys=True).encode()
    if len(data) > LIMIT:
        raise ValueError("CI session receipt exceeds size limit")
    temporary = directory / "session.next"
    temporary.write_bytes(data)
    temporary.replace(directory / "session.json")


def command(state, phase=None):
    script = "product-ci.sh" if state["kind"] == "product" else "pr-proof.sh"
    args = ["bash", str(Path(state["checkout"]) / "scripts" / script)]
    args += [state["stage"]] if state["kind"] == "product" else ["--stage", state["stage"]]
    return args + (["--list-phases"] if phase is None else ["--phase", phase])


def process_identity(pid):
    try:
        descriptor = os.pidfd_open(pid)
        try:
            # fdinfo reports the PID in the mounted /proc namespace, which may
            # differ from os.getpid() inside a container. Birth ticks also work
            # on older kernels whose pidfds share an anonymous inode.
            info = Path(f"/proc/self/fdinfo/{descriptor}")
            line = next(line for line in info.read_text().splitlines() if line.startswith("Pid:"))
            proc_pid = int(line.split()[1])
            if proc_pid < 1:
                return None
            fields = Path(f"/proc/{proc_pid}/stat").read_text().rsplit(")", 1)[1].split()
            if line not in info.read_text().splitlines():
                return None
            boot = Path("/proc/sys/kernel/random/boot_id").read_text().strip()
            return f"{boot}:{proc_pid}:{fields[19]}"
        finally:
            os.close(descriptor)
    except (FileNotFoundError, ProcessLookupError, ValueError):
        return None


def group_alive(group):
    namespace = os.readlink("/proc/self/ns/pid")
    for entry in Path("/proc").iterdir():
        if not entry.name.isdecimal():
            continue
        try:
            if os.readlink(entry / "ns/pid") != namespace:
                continue
            fields = (entry / "stat").read_text().rsplit(")", 1)[1].split()
            if fields[0] == "Z":
                continue
            status = (entry / "status").read_text().splitlines()
            groups = next(line for line in status if line.startswith("NSpgid:")).split()
            if int(groups[-1]) == group:
                return True
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            pass
    return False


def terminate_group(active):
    if not active or process_identity(active["pid"]) != active["start"]:
        return
    pid = active["pid"]
    try:
        if not group_alive(pid):
            return
        os.killpg(pid, signal.SIGTERM)
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            if not group_alive(pid):
                return
            time.sleep(.05)
        os.killpg(pid, signal.SIGKILL)
    except ProcessLookupError:
        pass


def emit(connection, value, deadline=None):
    data = json.dumps(value).encode() + b"\n"
    if isinstance(connection, socket.socket):
        connection.sendall(data)
        return
    deadline = deadline or (time.monotonic() + 30)
    while data:
        try:
            count = os.write(connection, data)
            data = data[count:]
        except BlockingIOError:
            if time.monotonic() >= deadline:
                raise TimeoutError("CI output client stopped reading")
            select.select([], [connection], [], .1)


def cleanup(state, environment):
    if state["kind"] != "pr":
        return 0
    child = subprocess.Popen(["bash", str(Path(state["checkout"]) / "scripts/pr-db-proof.sh"), "--phase", "cleanup"],
                             cwd=state["checkout"], env=environment, start_new_session=True)
    active = {"pid": child.pid, "start": process_identity(child.pid)}
    try:
        return child.wait(timeout=120)
    except subprocess.TimeoutExpired:
        terminate_group(active)
        child.wait()
        return 124


def guardian(directory, lock_fd, control_fd, parent, environment):
    """Keep the lock until supervisor loss has terminated its active phase."""
    try:
        readable, _, _ = select.select([parent, control_fd], [], [])
        if control_fd in readable and os.read(control_fd, 1) == b"S":
            return
        state = read_state(directory)
        terminate_group(state.get("active"))
        cleanup_rc = cleanup(state, environment)
        state.update(status="failed", failure="supervisor exited unexpectedly", active=None, cleanup_exit_code=cleanup_rc)
        save(directory, state)
    finally:
        os.close(parent)
        os.close(control_fd)
        os.close(lock_fd)


def receive(descriptor):
    data = bytearray()
    while len(data) < 4096:
        byte = os.read(descriptor, 1)
        if byte == b"\n":
            return json.loads(data)
        if not byte:
            raise ConnectionError("CI client disconnected")
        data.extend(byte)
    raise ValueError("CI request exceeds atomic pipe size")


def validate(request, state):
    if request.get("identity") != state["identity"] or request.get("token") != state["token"]:
        raise ValueError("foreign CI session request")
    pid = request.get("pid", 0)
    if process_identity(pid) != request.get("start"):
        raise ValueError("CI client process identity is stale")
    if not re.fullmatch(r"reply-[a-f0-9]{32}", request.get("reply", "")):
        raise ValueError("invalid CI response pipe")


def response(directory, request):
    path = directory / request["reply"]
    mode = path.lstat()
    if not stat.S_ISFIFO(mode.st_mode) or mode.st_uid != os.getuid() or mode.st_mode & 0o077:
        raise ValueError("invalid CI response pipe ownership")
    return os.open(path, os.O_WRONLY | os.O_NONBLOCK | os.O_NOFOLLOW)


def run_phase(directory, state, phase, connection, client_fd, environment, timeout):
    # A gate prevents execution before the guardian can identify the group. If
    # the supervisor dies before opening it, EOF makes the wrapper exit unused.
    gate_read, gate_write = os.pipe()
    phase_environment = dict(environment, LAPLACE_CI_COMPLETED_PHASES="|" + "|".join(item["phase"] for item in state["results"] if item["exit_code"] == 0) + "|")
    child = subprocess.Popen([sys.executable, str(Path(__file__).resolve()), "_exec", str(gate_read), *command(state, phase)],
                             cwd=state["checkout"], env=phase_environment, stdout=subprocess.PIPE,
                             stderr=subprocess.STDOUT, pass_fds=(gate_read,), start_new_session=True)
    os.close(gate_read)
    state["active"] = {"pid": child.pid, "start": process_identity(child.pid), "phase": phase}
    save(directory, state)
    os.write(gate_write, b"G")
    os.close(gate_write)
    deadline = time.monotonic() + timeout
    child_fd = os.pidfd_open(child.pid)
    reason = None
    output_open = True
    try:
        while True:
            if time.monotonic() >= deadline:
                reason = "phase timed out"
                break
            watched = [client_fd, child_fd] + ([child.stdout] if output_open else [])
            readable, _, _ = select.select(watched, [], [], .2)
            if client_fd in readable:
                # The requesting process has exited; cancel its real work.
                reason = "phase client disconnected"
                break
            if child.stdout in readable:
                output = os.read(child.stdout.fileno(), 16384)
                if output:
                    emit(connection, {"output": base64.b64encode(output).decode()}, deadline)
                else:
                    output_open = False
                    if child_fd in readable:
                        break
            elif child_fd in readable:
                break
        if reason:
            terminate_group(state["active"])
            return 124 if reason == "phase timed out" else 130, reason
        # Keep the group leader unreaped until descendants are terminated;
        # its pidfd identity prevents signaling a reused PID.
        terminate_group(state["active"])
        code = child.wait()
        return (code if code >= 0 else 128 - code), None
    except TimeoutError:
        terminate_group(state["active"])
        return 124, "phase output timed out"
    except (BrokenPipeError, ConnectionError, KeyboardInterrupt):
        terminate_group(state["active"])
        return 130, "phase client disconnected"
    finally:
        if child.poll() is None:
            terminate_group(state["active"])
        child.wait()
        os.close(child_fd)
        child.stdout.close()
        state["active"] = None


def serve(directory, startup_fd, lock_path, idle_timeout, phase_timeout):
    startup = socket.socket(fileno=startup_fd)
    state = read_state(directory)
    environment = dict(os.environ, LAPLACE_CI_SESSION_DIRECTORY=str(directory))
    if state["kind"] == "pr":
        environment.setdefault("LAPLACE_REGRESS_DB", f"laplace_pr_{environment.get('GITHUB_RUN_ID') or os.getpid()}_{environment.get('GITHUB_RUN_ATTEMPT') or '1'}")
    server = None
    lock_fd = os.open(lock_path, os.O_CREAT | os.O_RDWR, 0o660)
    guardian_pid = None
    control_write = None
    stopped = False
    locked = False
    state["supervisor"] = {"pid": os.getpid(), "start": process_identity(os.getpid())}
    def interrupted(signum, frame):
        raise KeyboardInterrupt
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    try:
        save(directory, state)
        while True:
            try:
                fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
                locked = True
                break
            except BlockingIOError:
                if select.select([startup], [], [], .2)[0]:
                    raise ConnectionError("lock acquisition client disconnected")
        os.mkfifo(directory / "request.fifo", 0o600)
        server = os.open(directory / "request.fifo", os.O_RDWR)
        state.update(status="ready", supervisor={"pid": os.getpid(), "start": process_identity(os.getpid())})
        save(directory, state)
        control_read, control_write = os.pipe()
        parent_fd = os.pidfd_open(os.getpid())
        guardian_pid = os.fork()
        if guardian_pid == 0:
            os.close(control_write)
            os.close(server)
            startup.close()
            signal.signal(signal.SIGTERM, signal.SIG_IGN)
            signal.signal(signal.SIGINT, signal.SIG_IGN)
            try:
                guardian(directory, lock_fd, control_read, parent_fd, environment)
            finally:
                os._exit(0)
        os.close(control_read)
        os.close(parent_fd)
        emit(startup, {"exit_code": 0})
        startup.close()
        while True:
            if not select.select([server], [], [], idle_timeout)[0]:
                state.update(status="failed", failure="CI session idle timeout")
                break
            connection = client_fd = None
            try:
                request = receive(server)
                validate(request, state)
                connection = response(directory, request)
                client_fd = os.pidfd_open(request["pid"])
                if process_identity(request["pid"]) != request["start"]:
                    raise ValueError("CI client identity changed")
                if request.get("operation") == "stop":
                    cleanup_rc = cleanup(state, environment)
                    state.update(status="stopped", cleanup_exit_code=cleanup_rc)
                    save(directory, state)
                    os.write(control_write, b"S")
                    os.close(control_write)
                    control_write = None
                    os.waitpid(guardian_pid, 0)
                    guardian_pid = None
                    os.close(lock_fd)
                    lock_fd = -1
                    stopped = True
                    emit(connection, {"exit_code": cleanup_rc})
                    break
                if request.get("operation") != "run":
                    raise ValueError("unknown CI session operation")
                phase = request.get("phase")
                expected = state["phases"][state["next"]] if state["next"] < len(state["phases"]) else None
                if phase != expected:
                    raise ValueError(f"expected phase {expected!r}, received {phase!r}")
                if source(state["checkout"]) != state["source"]:
                    raise ValueError("CI checkout identity changed")
                code, reason = run_phase(directory, state, phase, connection, client_fd, environment, phase_timeout)
                state["results"].append({"phase": phase, "exit_code": code})
                state["next"] += 1
                if code:
                    state.update(status="failed", failure=reason or f"phase {phase} failed")
                    state["cleanup_exit_code"] = cleanup(state, environment)
                save(directory, state)
                try:
                    emit(connection, {"exit_code": code})
                except BrokenPipeError:
                    pass
                if code:
                    break
            except (ValueError, ConnectionError, OSError) as error:
                if connection is not None:
                    try:
                        emit(connection, {"exit_code": 2, "error": str(error)})
                    except BrokenPipeError:
                        pass
            finally:
                if connection is not None:
                    os.close(connection)
                if client_fd is not None:
                    os.close(client_fd)
    except (KeyboardInterrupt, ConnectionError, OSError, ValueError, subprocess.SubprocessError) as error:
        state.update(status="failed", failure=str(error) or "CI session cancelled")
    finally:
        if not stopped:
            terminate_group(state.get("active"))
            if "cleanup_exit_code" not in state:
                state["cleanup_exit_code"] = cleanup(state, environment) if locked else 0
            state.update(active=None)
            save(directory, state)
        if control_write is not None:
            os.write(control_write, b"S")
            os.close(control_write)
        if guardian_pid:
            os.waitpid(guardian_pid, 0)
        if lock_fd >= 0:
            os.close(lock_fd)
        if server is not None:
            os.close(server)
        startup.close()
        (directory / "request.fifo").unlink(missing_ok=True)


def consume(connection, supervisor_fd=None):
    descriptor = connection.fileno() if isinstance(connection, socket.socket) else connection
    data = bytearray()
    while True:
        watched = [descriptor] + ([supervisor_fd] if supervisor_fd is not None else [])
        readable, _, _ = select.select(watched, [], [])
        if descriptor not in readable:
            raise ConnectionError("CI supervisor disconnected")
        chunk = os.read(descriptor, 16384)
        if not chunk:
            raise ConnectionError("CI supervisor disconnected")
        data.extend(chunk)
        while b"\n" in data:
            line, _, remaining = data.partition(b"\n")
            data = bytearray(remaining)
            if len(line) > LIMIT:
                raise ValueError("CI response exceeds size limit")
            value = json.loads(line)
            if "output" in value:
                sys.stdout.buffer.write(base64.b64decode(value["output"]))
                sys.stdout.buffer.flush()
            if "error" in value:
                print(value["error"], file=sys.stderr)
            if "exit_code" in value:
                return value["exit_code"]
        if len(data) > LIMIT:
            raise ValueError("CI response exceeds size limit")


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "_exec":
        fd = int(sys.argv[2])
        go = os.read(fd, 1)
        os.close(fd)
        if go != b"G":
            return 130
        os.execvp(sys.argv[3], sys.argv[3:])
    if len(sys.argv) > 1 and sys.argv[1] == "_serve":
        serve(Path(sys.argv[2]), int(sys.argv[3]), sys.argv[4], float(sys.argv[5]), float(sys.argv[6]))
        return 0
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="operation", required=True)
    for operation in ("start", "run", "stop"):
        command_parser = sub.add_parser(operation)
        command_parser.add_argument("--directory", type=Path, required=True)
        if operation == "start":
            command_parser.add_argument("--lock", type=Path, required=True)
            command_parser.add_argument("--checkout", type=Path, required=True)
            command_parser.add_argument("--kind", choices=("product", "pr"), required=True)
            command_parser.add_argument("--stage", default="all")
            command_parser.add_argument("--idle-timeout-seconds", type=float, default=600)
            command_parser.add_argument("--phase-timeout-seconds", type=float, default=25200)
        if operation == "run":
            command_parser.add_argument("--phase", required=True)
    args = parser.parse_args()
    directory = args.directory.absolute()
    def interrupted(signum, frame):
        raise KeyboardInterrupt
    signal.signal(signal.SIGTERM, interrupted)
    if args.operation == "start":
        if args.idle_timeout_seconds <= 0 or args.phase_timeout_seconds <= 0:
            raise ValueError("CI session timeouts must be positive")
        state = {"schema": SCHEMA, "identity": identity(), "token": os.urandom(24).hex(),
                 "kind": args.kind, "stage": args.stage, "checkout": str(args.checkout.resolve()),
                 "source": source(args.checkout), "next": 0, "results": [], "active": None, "status": "waiting"}
        plan = subprocess.check_output(command(state), cwd=state["checkout"], text=True, timeout=30).splitlines()
        if not plan or len(plan) > 64 or len(set(plan)) != len(plan) or any(not re.fullmatch(r"[a-z][a-z0-9-]{0,63}", phase) for phase in plan):
            raise ValueError("canonical CI phase plan is invalid")
        state["phases"] = plan
        directory.mkdir(mode=0o700, parents=False, exist_ok=False)
        save(directory, state)
        client, startup = socket.socketpair()
        with open(directory / "supervisor.log", "wb") as log:
            child = subprocess.Popen([sys.executable, str(Path(__file__).resolve()), "_serve", str(directory),
                                      str(startup.fileno()), str(args.lock.absolute()), str(args.idle_timeout_seconds),
                                      str(args.phase_timeout_seconds)], pass_fds=(startup.fileno(),),
                                     stdin=subprocess.DEVNULL, stdout=log, stderr=log, start_new_session=True)
        startup.close()
        try:
            with client:
                code = consume(client)
        except BaseException:
            # Closing the startup lease is sufficient while waiting for flock;
            # avoid racing a second signal against that clean cancellation.
            client.close()
            try:
                child.wait(timeout=3)
            except subprocess.TimeoutExpired:
                child.terminate()
                child.wait(timeout=130)
            raise
        if code == 0:
            if os.environ.get("GITHUB_ENV"):
                with open(os.environ["GITHUB_ENV"], "a", encoding="utf-8") as github_env:
                    github_env.write("LAPLACE_CI_PHASES=|" + "|".join(plan) + "|\n")
            print(f"CI session ready: {args.kind}/{args.stage}; {len(plan)} phases")
        return code
    state = read_state(directory)
    if state["status"] in ("stopped", "failed"):
        if args.operation == "stop":
            return state.get("cleanup_exit_code", 0)
        raise ValueError(f"CI session is {state['status']}")
    supervisor = state.get("supervisor", {})
    if not supervisor.get("start") or process_identity(supervisor.get("pid", 0)) != supervisor.get("start"):
        raise ValueError("CI session supervisor identity is stale")
    if args.operation == "stop" and state.get("active"):
        descriptor = os.pidfd_open(supervisor["pid"])
        try:
            if process_identity(supervisor["pid"]) != supervisor["start"]:
                raise ValueError("CI session supervisor identity changed")
            signal.pidfd_send_signal(descriptor, signal.SIGTERM)
            if not select.select([descriptor], [], [], 150)[0]:
                raise ValueError("CI session cleanup has not finished")
        finally:
            os.close(descriptor)
        return read_state(directory).get("cleanup_exit_code", 0)
    reply = directory / ("reply-" + os.urandom(16).hex())
    os.mkfifo(reply, 0o600)
    connection = os.open(reply, os.O_RDWR | os.O_NONBLOCK)
    supervisor_fd = os.pidfd_open(supervisor["pid"])
    try:
        request = {"operation": args.operation, "phase": getattr(args, "phase", None),
                   "identity": identity(), "token": state["token"], "pid": os.getpid(),
                   "start": process_identity(os.getpid()), "reply": reply.name}
        data = json.dumps(request).encode() + b"\n"
        if len(data) > 4096:
            raise ValueError("CI request exceeds atomic pipe size")
        outgoing = os.open(directory / "request.fifo", os.O_WRONLY | os.O_NONBLOCK | os.O_NOFOLLOW)
        try:
            if os.write(outgoing, data) != len(data):
                raise ValueError("incomplete CI request")
        finally:
            os.close(outgoing)
        return consume(connection, supervisor_fd)
    finally:
        os.close(supervisor_fd)
        os.close(connection)
        reply.unlink(missing_ok=True)



if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        print(f"CI session: {error}", file=sys.stderr)
        sys.exit(2)
