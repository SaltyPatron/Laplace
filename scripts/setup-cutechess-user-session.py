#!/usr/bin/env python3
"""Install the owned persistent CuteChess user session; activation requires --start."""
import argparse
import fcntl
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import pwd
import re
import secrets
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


def current_identity():
    uid = os.getuid()
    require(uid != 0 and uid == os.geteuid(), "run as the existing nonroot session account")
    account = pwd.getpwuid(uid)
    home = Path(account.pw_dir)
    require(home.is_absolute() and str(home) not in ("/", "/root"), "invalid account home")
    directory_chain(home, uid)
    return uid, account.pw_name, home


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


def check_user_manager(home, uid, *, run=subprocess.run):
    runtime = Path("/run/user") / str(uid)
    directory_chain(runtime, uid, private=True)
    info = (runtime / "bus").lstat()
    require(stat.S_ISSOCK(info.st_mode) and info.st_uid == uid,
            "the current account needs its existing user-manager bus")
    environment = manager_environment(home, uid)
    output = command(["/usr/bin/loginctl", "show-user", str(uid),
                      "--property=Linger", "--property=RuntimePath", "--no-pager"],
                     environment, run=run)
    properties = {}
    for line in output.splitlines():
        key, separator, value = line.partition("=")
        require(separator and key not in properties, "invalid loginctl response")
        properties[key] = value
    require(properties == {"Linger": "yes", "RuntimePath": str(runtime)},
            "existing linger and matching user runtime are required")
    state = command(["/usr/bin/systemctl", "--user", "is-system-running"],
                    environment, run=run, accepted=(0, 1))
    require(state in ("running", "degraded"), "existing user manager is not running")
    return environment


def validate_password_file(path, uid):
    require(path is not None and path.is_absolute(), "password file must be absolute")
    require(".." not in path.parts and re.fullmatch(r"[A-Za-z0-9_./-]+", str(path)),
            "password file path is not safe for the fixed Xpra auth syntax")
    directory_chain(path.parent, uid)
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    try:
        info = os.fstat(descriptor)
        require(stat.S_ISREG(info.st_mode) and info.st_uid == uid
                and stat.S_IMODE(info.st_mode) == 0o600 and info.st_nlink == 1,
                "password file must be an owned private regular file")
        require(info.st_size == 64, "password file must contain exactly 64 lowercase hex bytes without a newline")
        value = os.read(descriptor, 65)
        require(re.fullmatch(rb"[0-9a-f]{64}", value) is not None,
                "password file must contain exactly 64 lowercase hex bytes without a newline")
    finally:
        os.close(descriptor)
    # Values are checked in memory only; never copied, hashed, printed, or passed as arguments.


def ensure_password_file(path, uid):
    """Create once under the install lock; retain valid credentials on retries."""
    try:
        descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    except FileExistsError:
        validate_password_file(path, uid)
        return False
    identity = None
    try:
        info = os.fstat(descriptor)
        identity = (info.st_dev, info.st_ino)
        require(stat.S_ISREG(info.st_mode) and info.st_uid == uid and info.st_nlink == 1,
                "new password file has an unexpected owner or type")
        os.fchmod(descriptor, 0o600)
        value = secrets.token_hex(32).encode("ascii")
        require(re.fullmatch(rb"[0-9a-f]{64}", value) is not None,
                "credential generator returned an invalid value")
        with os.fdopen(descriptor, "wb", closefd=False) as stream:
            stream.write(value)
            stream.flush()
            os.fsync(descriptor)
    except BaseException:
        if identity is not None:
            current = os.lstat(path)
            if (current.st_dev, current.st_ino) == identity:
                os.unlink(path)
        raise
    finally:
        os.close(descriptor)
    parent_descriptor = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        os.fsync(parent_descriptor)
    finally:
        os.close(parent_descriptor)
    return True


def sources(source_root):
    result = {}
    for name, relative in SOURCE_FILES.items():
        path = source_root / relative
        require(path.resolve() == path.absolute(), "source symlinks are not accepted")
        data = regular_bytes(path)
        result[name] = {"data": data, "source": relative,
                        "sha256": sha256(data), "git_blob": git_blob(data)}
    require(result["x11"]["git_blob"] == X11_BLOB, "selected X11 helper identity differs")
    installer = regular_bytes(Path(__file__).absolute())
    result["installer"] = {"data": installer, "source": "scripts/setup-cutechess-user-session.py",
                           "sha256": sha256(installer), "git_blob": git_blob(installer)}
    return result


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


def make_plan(home, uid, selected, runtime, password_file=None, *, generate_password=False):
    prefix, config_directory, unit_directory = layout(home)
    configuration = {"schema": CONFIG_SCHEMA, "runtime_root": str(RUNTIME_ROOT),
                     "transport": "unix"}
    if password_file is not None:
        if generate_password:
            require(re.fullmatch(r"[A-Za-z0-9_./-]+", str(password_file)) is not None,
                    "password file path is not safe for the fixed Xpra auth syntax")
            require(password_file == config_directory / "cutechess-session-password",
                    "generated credential must use the fixed owned path")
        if not generate_password or os.path.lexists(password_file):
            validate_password_file(password_file, uid)
        configuration["transport"] = "loopback-password"
        configuration["password_file"] = str(password_file)
    files = {
        str(prefix / "laplace-cutechess-user-session"): (selected["launcher"]["data"], 0o700),
        str(prefix / "lib/chess_xpra_runtime.py"): (selected["xpra"]["data"], 0o600),
        str(prefix / "lib/chess_x11_runtime.py"): (selected["x11"]["data"], 0o600),
        str(unit_directory / UNIT_NAME): (selected["unit"]["data"], 0o600),
        str(config_directory / "cutechess-session.json"): (canonical(configuration), 0o600),
    }
    receipt = {
        "schema": SCHEMA,
        "uid": uid,
        "home": str(home),
        "sources": {item["source"]: {"sha256": item["sha256"], "git_blob": item["git_blob"]}
                    for item in selected.values()},
        "files": {path: {"sha256": sha256(data), "mode": format(mode, "04o")}
                  for path, (data, mode) in files.items()},
        "runtime": {"root": str(RUNTIME_ROOT), "runtime_id": runtime["runtime_id"]},
        "transport": configuration["transport"],
    }
    if password_file is not None:
        # The credential belongs to this install by path only, never by content hash.
        receipt["credential_file"] = str(password_file)
    files[str(prefix / "install-receipt.json")] = (canonical(receipt), 0o600)
    return files, receipt


def managed_paths(home):
    prefix, config_directory, unit_directory = layout(home)
    return {
        str(prefix / "laplace-cutechess-user-session"): ("launcher", 0o700),
        str(prefix / "lib/chess_xpra_runtime.py"): ("xpra", 0o600),
        str(prefix / "lib/chess_x11_runtime.py"): ("x11", 0o600),
        str(unit_directory / UNIT_NAME): ("unit", 0o600),
        str(config_directory / "cutechess-session.json"): (None, 0o600),
    }


def validate_owned_receipt(home, uid, files):
    """Accept only the fixed owned installation and its exact recorded identities."""
    allowed = managed_paths(home)
    receipt_path = str(layout(home)[0] / "install-receipt.json")
    require(set(files) == set(allowed) | {receipt_path},
            "managed paths differ from the fixed installation")
    receipt_data, receipt_mode = files[receipt_path]
    require(receipt_mode == 0o600, "installation receipt mode differs")
    receipt = json.loads(receipt_data)
    require(isinstance(receipt, dict), "invalid installation receipt")
    fields = {"schema", "uid", "home", "sources", "files", "runtime", "transport"}
    transport = receipt.get("transport")
    require(transport in ("unix", "loopback-password"), "invalid receipt transport")
    if transport == "loopback-password":
        fields.add("credential_file")
    require(set(receipt) == fields and receipt.get("schema") == SCHEMA
            and type(receipt.get("uid")) is int and receipt["uid"] == uid
            and receipt.get("home") == str(home), "installation receipt ownership differs")
    require(canonical(receipt) == receipt_data, "installation receipt is not canonical")
    source_keys = set(SOURCE_FILES.values()) | {"scripts/setup-cutechess-user-session.py"}
    recorded_sources = receipt["sources"]
    require(isinstance(recorded_sources, dict) and set(recorded_sources) == source_keys,
            "installation receipt source keys differ")
    for identity in recorded_sources.values():
        require(isinstance(identity, dict) and set(identity) == {"sha256", "git_blob"}
                and isinstance(identity["sha256"], str)
                and re.fullmatch(r"[0-9a-f]{64}", identity["sha256"]) is not None
                and isinstance(identity["git_blob"], str)
                and re.fullmatch(r"[0-9a-f]{40}", identity["git_blob"]) is not None,
                "installation receipt source identity is malformed")
    runtime = receipt["runtime"]
    require(isinstance(runtime, dict) and set(runtime) == {"root", "runtime_id"}
            and runtime["root"] == str(RUNTIME_ROOT)
            and isinstance(runtime["runtime_id"], str)
            and re.fullmatch(r"[0-9a-f]{64}", runtime["runtime_id"]) is not None,
            "installation receipt runtime identity differs")
    records = receipt["files"]
    require(isinstance(records, dict) and set(records) == set(allowed),
            "installation receipt file keys differ")
    config_path = str(layout(home)[1] / "cutechess-session.json")
    configuration = json.loads(files[config_path][0])
    config_keys = {"schema", "runtime_root", "transport"}
    if transport == "loopback-password":
        config_keys.add("password_file")
    require(isinstance(configuration, dict) and set(configuration) == config_keys
            and configuration.get("schema") == CONFIG_SCHEMA
            and configuration.get("runtime_root") == str(RUNTIME_ROOT)
            and configuration.get("transport") == transport,
            "recorded session configuration differs")
    require(canonical(configuration) == files[config_path][0],
            "recorded session configuration is not canonical")
    if transport == "loopback-password":
        password = configuration["password_file"]
        require(isinstance(password, str) and Path(password).is_absolute()
                and ".." not in Path(password).parts
                and re.fullmatch(r"[A-Za-z0-9_./-]+", password) is not None
                and receipt["credential_file"] == password,
                "recorded credential path differs")
        require(password not in files, "credential contents must not be managed as source")
    for path, (source, expected_mode) in allowed.items():
        data, mode = files[path]
        record = records[path]
        require(mode == expected_mode and isinstance(record, dict)
                and set(record) == {"sha256", "mode"}
                and record["mode"] == format(expected_mode, "04o")
                and record["sha256"] == sha256(data),
                "managed file identity or mode differs from its receipt")
        if source is not None:
            identity = recorded_sources[SOURCE_FILES[source]]
            require(identity["sha256"] == record["sha256"]
                    and identity["git_blob"] == git_blob(data),
                    "installed source identity differs from its receipt")
    return receipt


def existing_plan(home, files, uid):
    """Never adopt an unknown or locally edited managed file."""
    receipt_path = str(layout(home)[0] / "install-receipt.json")
    existing = {}
    for path, (data, mode) in files.items():
        if os.path.lexists(path):
            existing[path] = (regular_bytes(path, uid, mode), mode)
    if not existing:
        return None
    require(receipt_path in existing, "existing files have no installation receipt")
    require(len(existing) == len(files), "existing installation is incomplete")
    validate_owned_receipt(home, uid, existing)
    return existing


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


def update_owned_files(files, previous, uid):
    changed = sorted((path for path in files if files[path] != previous[path]),
                     key=lambda path: path.endswith("/install-receipt.json"))
    attempted = []
    try:
        for path in changed:
            old_data, old_mode = previous[path]
            require(regular_bytes(path, uid, old_mode) == old_data,
                    "managed file changed during installation")
            attempted.append(path)
            data, mode = files[path]
            atomic_replace(path, data, mode, uid)
    except BaseException:
        rollback_failed = False
        for path in reversed(attempted):
            old_data, old_mode = previous[path]
            data, mode = files[path]
            try:
                actual = regular_bytes(path, uid, mode)
                if actual == old_data and old_mode == mode:
                    continue
                require(actual == data, "managed file changed during rollback")
                atomic_replace(path, old_data, old_mode, uid)
            except BaseException:
                rollback_failed = True
        if rollback_failed:
            raise SetupError("managed update rollback could not restore every owned file") from None
        raise
    return bool(changed)


def pending_path(home):
    return layout(home)[0] / "pending-activation.json"


def pending_bytes(home, uid, receipt_data):
    return canonical({"schema": "laplace.cutechess-user-activation/v1", "uid": uid,
                      "home": str(home), "receipt_sha256": sha256(receipt_data)})


def read_pending(home, uid, receipt_data):
    path = pending_path(home)
    if not os.path.lexists(path):
        return None
    require(receipt_data is not None, "pending activation has no owned installation")
    data = regular_bytes(path, uid, 0o600)
    require(data == pending_bytes(home, uid, receipt_data),
            "pending activation differs from the owned receipt")
    return data


def sync_directory(path):
    descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def publish_pending(home, uid, before, wanted):
    if before == wanted:
        return False
    path = pending_path(home)
    if before is None:
        create_owned_file(path, wanted, 0o600, uid)
        sync_directory(path.parent)
    else:
        require(regular_bytes(path, uid, 0o600) == before,
                "pending activation changed during installation")
        atomic_replace(path, wanted, 0o600, uid)
    return True


def restore_pending(home, uid, before, wanted):
    path = pending_path(home)
    if not os.path.lexists(path):
        require(before is None, "pending activation disappeared during rollback")
        return
    actual = regular_bytes(path, uid, 0o600)
    if actual == before:
        return
    require(actual == wanted, "pending activation changed during rollback")
    if before is None:
        path.unlink()
        sync_directory(path.parent)
    else:
        atomic_replace(path, before, 0o600, uid)


def clear_pending(home, uid, receipt_data):
    if read_pending(home, uid, receipt_data) is not None:
        pending_path(home).unlink()
        sync_directory(pending_path(home).parent)


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


def install_files(home, uid, files, *, generated_password=None):
    prefix, config_directory, unit_directory = layout(home)
    # All existing-file conflicts are found before creating a lock or new files.
    for target, private in ((prefix, True), (prefix / "lib", True),
                            (config_directory, True), (unit_directory, False)):
        if os.path.lexists(target):
            directory_chain(target, uid, private=private, boundary=home)
    validate_owned_receipt(home, uid, files)
    previous = existing_plan(home, files, uid)
    validate_private_log(home, uid, previous, files)
    receipt_path = str(prefix / "install-receipt.json")
    read_pending(home, uid, previous[receipt_path][0] if previous else None)
    for target, private in ((prefix, True), (prefix / "lib", True),
                            (config_directory, True), (unit_directory, False)):
        directory_chain(target, uid, private=private, create=True, boundary=home)
    lock_path = prefix / "install.lock"
    lock_fd = os.open(lock_path, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW | os.O_NONBLOCK, 0o600)
    created = []
    password_created = False
    log_created = False
    changed = False
    pending_before = None
    pending_wanted = None
    pending_attempted = False
    try:
        lock_info = os.fstat(lock_fd)
        require(stat.S_ISREG(lock_info.st_mode) and lock_info.st_uid == uid
                and stat.S_IMODE(lock_info.st_mode) == 0o600 and lock_info.st_nlink == 1
                and lock_info.st_size == 0, "invalid installer lock")
        fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        previous = existing_plan(home, files, uid)
        logging = validate_private_log(home, uid, previous, files)
        pending_before = read_pending(home, uid, previous[receipt_path][0] if previous else None)
        if generated_password is not None:
            require(generated_password == config_directory / "cutechess-session-password",
                    "generated credential must use the fixed owned path")
            password_created = ensure_password_file(generated_password, uid)
        if logging and not os.path.lexists(private_log_path(home)):
            log_path = str(private_log_path(home))
            inode = create_owned_file(log_path, b"", 0o600, uid)
            created.append((log_path, inode))
            sync_directory(private_log_path(home).parent)
            log_created = True
        content_changed = previous is None or any(files[path] != previous[path] for path in files)
        if content_changed or password_created or log_created:
            pending_wanted = pending_bytes(home, uid, files[receipt_path][0])
            pending_attempted = pending_before != pending_wanted
            publish_pending(home, uid, pending_before, pending_wanted)
        if previous is None:
            for path, (data, mode) in files.items():
                inode = create_owned_file(path, data, mode, uid)
                created.append((path, inode))
            changed = True
            for parent in {str(Path(path).parent) for path in files}:
                descriptor = os.open(parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
                try:
                    os.fsync(descriptor)
                finally:
                    os.close(descriptor)
        else:
            changed = update_owned_files(files, previous, uid)
    except BaseException:
        for path, inode in reversed(created):
            try:
                info = os.lstat(path)
                if (info.st_dev, info.st_ino) == inode:
                    os.unlink(path)
            except FileNotFoundError:
                pass
        if pending_attempted:
            try:
                restore_pending(home, uid, pending_before, pending_wanted)
            except BaseException:
                raise SetupError("managed update rollback could not restore pending activation") from None
        raise
    finally:
        os.close(lock_fd)
    return changed or password_created or log_created


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


def setup(*, start=False, loopback=False, password_file=None, source_root=None,
          identity=current_identity, manager=check_user_manager, runtime_loader=load_runtime,
          run=subprocess.run):
    require(not (loopback and password_file is not None),
            "choose --loopback or an explicit --password-file")
    uid, account, home = identity()
    environment = manager(home, uid, run=run)
    root = Path(source_root) if source_root else Path(__file__).absolute().parents[1]
    selected = sources(root)
    runtime = runtime_loader(root)
    generated_password = home / ".config/laplace/cutechess-session-password" if loopback else None
    selected_password = generated_password if loopback else password_file
    files, receipt = make_plan(home, uid, selected, runtime, selected_password,
                               generate_password=loopback)
    was_active = service_is_active(environment, run=run) if start else False
    installed = install_files(home, uid, files, generated_password=generated_password)
    if start:
        receipt_data = files[str(layout(home)[0] / "install-receipt.json")][0]
        pending = read_pending(home, uid, receipt_data) is not None
        activate(environment, changed=installed or pending, was_active=was_active, run=run)
        clear_pending(home, uid, receipt_data)
    return {"schema": SCHEMA, "status": "active" if start else "installed",
            "changed": installed, "uid": uid, "source_count": len(receipt["sources"]),
            "managed_file_count": len(files), "transport": receipt["transport"],
            "runtime_id": receipt["runtime"]["runtime_id"]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--start", action="store_true",
                        help="reload the existing user manager and enable/start the session")
    transport = parser.add_mutually_exclusive_group()
    transport.add_argument("--loopback", action="store_true",
                           help="use the fixed authenticated loopback transport and create its private credential once")
    transport.add_argument("--password-file", type=Path,
                        help="owned 0600 file with exactly 64 lowercase hex bytes, no newline; fixed loopback transport")
    arguments = parser.parse_args()
    try:
        outcome = setup(start=arguments.start, loopback=arguments.loopback,
                        password_file=arguments.password_file)
    except Exception as error:
        # No command output, environment, password content, or arbitrary exception text.
        message = str(error) if isinstance(error, SetupError) else type(error).__name__
        print(json.dumps({"schema": SCHEMA, "status": "failed", "error": message}),
              file=sys.stderr)
        return 1
    print(json.dumps(outcome, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
