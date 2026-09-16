#!/usr/bin/env python3
"""Install missing engine entries in the invoking user's official CuteChess config.

CuteChess 1.5.1, commit 45e923949e43570886c0ad3392f514e743839c6b:
projects/gui/src/cutechessapp.cpp uses QSettings IniFormat and loads
configPath()/engines.json; projects/lib/src/enginemanager.cpp loads the shared
array of EngineConfiguration objects. No application settings are rewritten.
"""
import fcntl
import hashlib
import json
import os
from pathlib import Path
import stat
import tempfile

MAXIMUM_CONFIG_BYTES = 16 * 1024 * 1024


def digest(value):
    return hashlib.sha256(value).hexdigest()


def object_pairs(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("CuteChess configuration contains duplicate JSON keys")
        value[key] = item
    return value


def invalid_number(value):
    raise ValueError("CuteChess configuration contains a non-finite JSON number")


def engines(value):
    result = json.loads(value.decode("utf-8"), object_pairs_hook=object_pairs,
                        parse_constant=invalid_number)
    if not isinstance(result, list) or any(
        not isinstance(item, dict)
        or any(not isinstance(item.get(key), str) or not item[key] for key in ("name", "command", "protocol"))
        for item in result
    ):
        raise ValueError("CuteChess engines.json must be an array of named engine configurations")
    return result


def owned_directory(path, private=False):
    if private and path.is_symlink():
        raise ValueError("private CuteChess work directory must not be a symlink")
    path.mkdir(mode=0o700, parents=True, exist_ok=True)
    found = path.stat()
    if not stat.S_ISDIR(found.st_mode) or found.st_uid != os.geteuid():
        raise ValueError("CuteChess session directory must belong to the invoking user")
    if private:
        path.chmod(0o700)
        if stat.S_IMODE(path.stat().st_mode) != 0o700:
            raise ValueError("private CuteChess work directory mode is not 0700")
    return path.resolve(strict=True)


def read_owned(path, maximum=MAXIMUM_CONFIG_BYTES):
    try:
        descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    except FileNotFoundError:
        return None
    with os.fdopen(descriptor, "rb") as stream:
        found = os.fstat(stream.fileno())
        if not stat.S_ISREG(found.st_mode) or found.st_uid != os.geteuid():
            raise ValueError("CuteChess user configuration must be a regular file owned by this user")
        value = stream.read(maximum + 1)
        if len(value) > maximum:
            raise ValueError("CuteChess configuration exceeds the admitted configuration byte envelope")
        return value, (found.st_dev, found.st_ino, found.st_size, found.st_mtime_ns, found.st_mode)


def atomic_text(path, value, mode=0o600):
    descriptor, pending = tempfile.mkstemp(prefix=".laplace-engines-", dir=path.parent)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            os.fchmod(stream.fileno(), mode)
            stream.write(value)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(pending, path)
        parent = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(parent)
        finally:
            os.close(parent)
    finally:
        Path(pending).unlink(missing_ok=True)


def backup(path, original):
    target = path.with_name(path.name + ".laplace-backup-" + digest(original))
    try:
        descriptor = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    except FileExistsError:
        saved = read_owned(target)
        if saved is None or saved[0] != original:
            raise ValueError("existing CuteChess backup differs; it was preserved")
    else:
        try:
            with os.fdopen(descriptor, "wb") as stream:
                stream.write(original)
                stream.flush()
                os.fsync(stream.fileno())
        except BaseException:
            target.unlink(missing_ok=True)
            raise
    return target


def running_gui(binary):
    """The session lock covers these launchers; do not edit a direct GUI's config."""
    selected = binary.stat()
    for process in Path("/proc").iterdir():
        if not process.name.isdecimal() or process.name == str(os.getpid()):
            continue
        try:
            if process.stat().st_uid != os.geteuid():
                continue
            link = process / "exe"
            target = os.readlink(link).removesuffix(" (deleted)")
            executable = link.stat()
            if (target == str(binary.resolve(strict=True))
                    or Path(target).name == "cutechess"
                    or (executable.st_dev, executable.st_ino) == (selected.st_dev, selected.st_ino)):
                return True
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            continue
    return False


def prepare(catalog, gui_binary, work_root):
    """Return a lock descriptor retained across GUI exec and public session paths."""
    catalog = Path(catalog)
    with catalog.open("rb") as stream:
        raw_catalog = stream.read(MAXIMUM_CONFIG_BYTES + 1)
    if len(raw_catalog) > MAXIMUM_CONFIG_BYTES:
        raise ValueError("installed engine catalog exceeds its byte envelope")
    selected = engines(raw_catalog)
    if (len(selected) != 2 or len({item["command"] for item in selected}) != 2
            or len({item["name"] for item in selected}) != 2 or any(item["protocol"] != "uci" or not Path(item["command"]).is_absolute()
                                 for item in selected)):
        raise ValueError("installed catalog must select the two installed UCI commands")
    for item in selected:
        executable = Path(item["command"])
        if not executable.is_file() or not os.access(executable, os.X_OK):
            raise ValueError("installed engine is unavailable: " + item["name"])
    configured = os.environ.get("XDG_CONFIG_HOME")
    config_root = Path(configured) if configured else Path.home() / ".config"
    if not config_root.is_absolute():
        raise ValueError("XDG_CONFIG_HOME must be absolute for CuteChess configuration")
    directory = owned_directory(config_root / "cutechess")
    lock_path = directory / ".laplace-engine-session.lock"
    lock = os.open(lock_path, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    try:
        found = os.fstat(lock)
        if not stat.S_ISREG(found.st_mode) or found.st_uid != os.geteuid():
            raise ValueError("CuteChess session lock does not belong to this user")
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise ValueError("CuteChess is already running through this launcher; close it before opening another session") from None
        work_root = Path(work_root)
        if not work_root.is_absolute() or not work_root.is_dir():
            raise ValueError("installed permanent CuteChess work root is unavailable")
        work = owned_directory(work_root / ("cutechess-user-" + str(os.geteuid())), private=True)
        logs = owned_directory(work / "logs", private=True)
        path = directory / "engines.json"
        original = read_owned(path)
        existing = engines(original[0]) if original is not None else []
        result = list(existing)
        names = {item["name"] for item in existing}
        commands = {(item["command"], item["protocol"]) for item in existing}
        added = []
        for template in selected:
            if (template["command"], template["protocol"]) in commands:
                continue
            name = template["name"]
            suffix = 2
            while name in names:
                name = template["name"] + " (" + str(suffix) + ")"
                suffix += 1
            entry = {**template, "name": name, "workingDirectory": str(work)}
            result.append(entry)
            names.add(name)
            commands.add((entry["command"], entry["protocol"]))
            added.append(name)
        saved_backup = None
        if added:
            if running_gui(Path(gui_binary)):
                raise ValueError("close the running CuteChess GUI before installing engine entries")
            output = (json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False) + "\n").encode("utf-8")
            if len(output) > MAXIMUM_CONFIG_BYTES:
                raise ValueError("merged CuteChess configuration exceeds its byte envelope")
            if original is not None:
                saved_backup = backup(path, original[0])
            if read_owned(path) != original:
                raise ValueError("CuteChess configuration changed concurrently; no replacement was made")
            atomic_text(path, output, stat.S_IMODE(original[1][4]) if original else 0o600)
        current = read_owned(path)
        if current is None:
            raise ValueError("CuteChess engine configuration was not published")
        receipt = {"schema": "laplace.cutechess-user-engines/v1", "uid": os.geteuid(),
                   "config": str(path), "config_sha256": digest(current[0]),
                   "catalog_sha256": digest(raw_catalog), "added_names": added,
                   "engine_count": len(result), "backup": str(saved_backup) if saved_backup else None,
                   "engine_execution_verified": False, "substrate_access_verified": False}
        atomic_text(directory / "laplace-engines.json",
                    (json.dumps(receipt, indent=2) + "\n").encode("utf-8"))
        os.set_inheritable(lock, True)
        return lock, {"TMPDIR": str(work), "TMP": str(work), "TEMP": str(work),
                      "LAPLACE_OPS_LOG_DIR": str(logs)}, receipt
    except BaseException:
        os.close(lock)
        raise
