#!/usr/bin/env python3
"""Helper constants and functions for setup-cutechess-user-session.py."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile

SCHEMA = "laplace.cutechess-user-install/v1"
CONFIG_SCHEMA = "laplace.cutechess-user-session/v1"
RUNTIME_ROOT = Path("/opt/laplace/tools/chess/xpra-runtime")
UNIT_NAME = "laplace-cutechess.service"
LOG_OUTPUT = b"StandardOutput=append:%h/.config/laplace/cutechess-session.log"
MAX_SOURCE = 2 * 1024 * 1024
X11_BLOB = "c31f1f4cce7da187e0706e2b028598673437b1c0"
SOURCE_FILES = {
    "launcher": "deploy/linux/laplace-cutechess-user-session",
    "unit": "deploy/linux/laplace-cutechess-user.service",
    "xpra": "scripts/lib/chess_xpra_runtime.py",
    "x11": "scripts/lib/chess_x11_runtime.py",
}


class SetupError(RuntimeError):
    pass


def require(condition, message):
    if not condition:
        raise SetupError(message)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def git_blob(data):
    return hashlib.sha1(b"blob " + str(len(data)).encode("ascii") + b"\0" + data).hexdigest()


def canonical(document):
    return (json.dumps(document, sort_keys=True, indent=2) + "\n").encode("utf-8")


def regular_bytes(path, uid=None, mode=None):
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    try:
        info = os.fstat(descriptor)
        require(stat.S_ISREG(info.st_mode), "expected a regular file")
        require(info.st_nlink == 1, "hard-linked files are not managed")
        if uid is not None:
            require(info.st_uid == uid, "file belongs to another account")
        if mode is not None:
            require(stat.S_IMODE(info.st_mode) == mode, "unexpected private file mode")
        require(info.st_size <= MAX_SOURCE, "file exceeds bounded source size")
        with os.fdopen(descriptor, "rb", closefd=False) as stream:
            data = stream.read(MAX_SOURCE + 1)
        require(len(data) <= MAX_SOURCE, "file exceeds bounded source size")
        return data
    finally:
        os.close(descriptor)


def directory_chain(path, uid, *, private=False, create=False, boundary=None):
    """Check components without resolving symlinks or changing existing modes.

    A boundary is used only after current_identity checked the full home chain.
    """
    path = Path(path)
    require(path.is_absolute() and ".." not in path.parts, "directory must be absolute")
    if boundary is None:
        current = Path(path.anchor)
        components = path.parts[1:]
        selected = []
    else:
        current = Path(boundary)
        require(current.is_absolute() and path.is_relative_to(current),
                "managed path escaped the selected home")
        components = path.relative_to(current).parts
        selected = [current]
    for component in components:
        require(component not in ("", ".", ".."), "invalid directory component")
        current /= component
        selected.append(current)
    for current in selected:
        try:
            info = current.lstat()
        except FileNotFoundError:
            require(create, "required directory is absent")
            current.mkdir(mode=0o700)
            descriptor = os.open(current, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            try:
                require(os.fstat(descriptor).st_uid == uid, "new directory belongs to another account")
                os.fchmod(descriptor, 0o700)
            finally:
                os.close(descriptor)
            info = current.lstat()
        require(stat.S_ISDIR(info.st_mode), "directory symlinks are not accepted")
        require(info.st_uid in (0, uid), "directory belongs to another account")
        require(not info.st_mode & 0o002, "directory is writable by another account")
        require(not info.st_mode & 0o020 or (info.st_uid == uid and info.st_gid == os.getgid()),
                "group-writable directory is outside the current account trust boundary")
        if current == path:
            require(info.st_uid == uid, "selected directory must belong to this account")
            if private:
                require(stat.S_IMODE(info.st_mode) == 0o700, "selected directory must be private")


def command(arguments, environment, *, run=subprocess.run, accepted=(0,)):
    result = run(arguments, env=environment, stdin=subprocess.DEVNULL,
                 stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                 text=True, check=False, timeout=8)
    require(result.returncode in accepted, "user-manager command failed")
    require(len(result.stdout) <= 8192, "user-manager output exceeded bound")
    return result.stdout.strip()


def manager_environment(home, uid):
    return {
        "HOME": str(home),
        "PATH": "/usr/bin:/bin",
        "LANG": "C.UTF-8",
        "LC_ALL": "C.UTF-8",
        "XDG_RUNTIME_DIR": "/run/user/" + str(uid),
        "DBUS_SESSION_BUS_ADDRESS": "unix:path=/run/user/" + str(uid) + "/bus",
    }


def load_runtime(source_root):
    library = source_root / "scripts/lib"
    previous_path = list(sys.path)
    previous_bytecode = sys.dont_write_bytecode
    sys.dont_write_bytecode = True
    previous_x11 = sys.modules.pop("chess_x11_runtime", None)
    previous_xpra = sys.modules.pop("chess_xpra_runtime", None)
    try:
        sys.path.insert(0, str(library))
        spec = importlib.util.spec_from_file_location("chess_xpra_runtime",
                                                     library / "chess_xpra_runtime.py")
        require(spec is not None and spec.loader is not None, "cannot load Xpra helper")
        module = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = module
        spec.loader.exec_module(module)
        document = module.load(RUNTIME_ROOT)
        require(isinstance(document, dict), "acquire the selected Xpra runtime first")
        return document
    finally:
        sys.path[:] = previous_path
        sys.dont_write_bytecode = previous_bytecode
        sys.modules.pop("chess_xpra_runtime", None)
        sys.modules.pop("chess_x11_runtime", None)
        if previous_x11 is not None:
            sys.modules["chess_x11_runtime"] = previous_x11
        if previous_xpra is not None:
            sys.modules["chess_xpra_runtime"] = previous_xpra


def layout(home):
    prefix = home / ".local/lib/laplace-cutechess"
    config = home / ".config/laplace"
    unit_directory = home / ".config/systemd/user"
    return prefix, config, unit_directory


def managed_paths(home):
    prefix, config_directory, unit_directory = layout(home)
    return {
        str(prefix / "laplace-cutechess-user-session"): ("launcher", 0o700),
        str(prefix / "lib/chess_xpra_runtime.py"): ("xpra", 0o600),
        str(prefix / "lib/chess_x11_runtime.py"): ("x11", 0o600),
        str(unit_directory / UNIT_NAME): ("unit", 0o600),
        str(config_directory / "cutechess-session.json"): (None, 0o600),
    }


def create_owned_file(path, data, mode, uid):
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, mode)
    try:
        info = os.fstat(descriptor)
        require(info.st_uid == uid, "new file has an unexpected owner")
        with os.fdopen(descriptor, "wb", closefd=False) as stream:
            stream.write(data)
            stream.flush()
            os.fsync(descriptor)
        os.fchmod(descriptor, mode)
        return info.st_dev, info.st_ino
    except BaseException:
        os.unlink(path)
        raise
    finally:
        os.close(descriptor)


def atomic_replace(path, data, mode, uid):
    """Publish one owned file atomically, with staged bytes and durable metadata."""
    descriptor, temporary = tempfile.mkstemp(prefix=".laplace-cutechess-update-",
                                             dir=Path(path).parent)
    try:
        info = os.fstat(descriptor)
        require(stat.S_ISREG(info.st_mode) and info.st_uid == uid and info.st_nlink == 1,
                "staged update file has an unexpected owner or type")
        with os.fdopen(descriptor, "wb", closefd=False) as stream:
            stream.write(data)
            stream.flush()
            os.fchmod(descriptor, mode)
            os.fsync(descriptor)
        os.replace(temporary, path)
        directory = os.open(Path(path).parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        os.close(descriptor)
        if os.path.lexists(temporary):
            os.unlink(temporary)


def pending_path(home):
    return layout(home)[0] / "pending-activation.json"


def sync_directory(path):
    descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def private_log_path(home):
    return layout(home)[1] / "cutechess-session.log"


def wants_private_log(home, files):
    if files is None:
        return False
    unit_path = str(layout(home)[2] / UNIT_NAME)
    return LOG_OUTPUT in files[unit_path][0].splitlines()


def validate_private_log(home, uid, previous, files):
    """Reuse a log only when the prior receipt-backed unit owns this exact sink."""
    if not wants_private_log(home, files):
        return False
    path = private_log_path(home)
    if not os.path.lexists(path):
        return True
    require(wants_private_log(home, previous),
            "existing log has no prior owned logging unit")
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    try:
        info = os.fstat(descriptor)
        require(stat.S_ISREG(info.st_mode) and info.st_uid == uid
                and stat.S_IMODE(info.st_mode) == 0o600 and info.st_nlink == 1,
                "session log must be an owned private regular file")
    finally:
        os.close(descriptor)
    # Log contents change while the service runs. Never read or hash them here.
    return True


def service_is_active(environment, *, run=subprocess.run):
    state = command(["/usr/bin/systemctl", "--user", "is-active", UNIT_NAME],
                    environment, run=run, accepted=(0, 3, 4))
    require(state in ("active", "inactive", "failed", "unknown"),
            "user session is in a transitional state")
    return state == "active"


def activate(environment, *, changed=False, was_active=False, run=subprocess.run):
    command(["/usr/bin/systemctl", "--user", "daemon-reload"], environment, run=run)
    command(["/usr/bin/systemctl", "--user", "enable", "--now", UNIT_NAME],
            environment, run=run)
    if changed and was_active:
        command(["/usr/bin/systemctl", "--user", "restart", UNIT_NAME], environment, run=run)
    active = command(["/usr/bin/systemctl", "--user", "is-active", UNIT_NAME],
                     environment, run=run)
    require(active == "active", "user session did not become active")
