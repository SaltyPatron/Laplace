#!/usr/bin/env python3
"""Update explicitly supplied OAuth pairs; retain unselected installed providers."""
import os
from pathlib import Path
import stat
import sys
import tempfile

PROVIDERS = (
    ("LAPLACE_AUTH_MICROSOFT_CLIENT_ID", "LAPLACE_AUTH_MICROSOFT_CLIENT_SECRET"),
    ("LAPLACE_AUTH_GOOGLE_CLIENT_ID", "LAPLACE_AUTH_GOOGLE_CLIENT_SECRET"),
)

def update(path, environment):
    path = Path(path)
    selected = {}
    for keys in PROVIDERS:
        values = tuple(environment.get(key, "") for key in keys)
        if bool(values[0]) != bool(values[1]):
            raise ValueError("OAuth client id and secret must be supplied together")
        if values[0]:
            if any("\n" in value or "\r" in value or "\0" in value for value in values):
                raise ValueError("OAuth values must be single-line environment values")
            selected.update(zip(keys, values))
    previous = path.lstat() if path.exists() or path.is_symlink() else None
    if previous is not None and not stat.S_ISREG(previous.st_mode):
        raise ValueError("identity environment must be a regular file")
    if previous is not None and not selected:
        return False
    original = path.read_bytes() if previous is not None else b""
    lines = original.decode("utf-8").splitlines(keepends=True)
    retained = [line for line in lines if line.split("=", 1)[0].strip() not in selected]
    if retained and not retained[-1].endswith(("\n", "\r")):
        retained[-1] += "\n"
    raw = "".join(retained).encode("utf-8")
    raw += "".join(key + "=" + value + "\n" for key, value in selected.items()).encode("utf-8")
    if previous is not None and raw == original:
        return False
    fd, temporary = tempfile.mkstemp(prefix=".identity.", dir=path.parent)
    try:
        with os.fdopen(fd, "wb") as stream:
            if previous is not None:
                actual = os.fstat(stream.fileno())
                if (actual.st_uid, actual.st_gid) != (previous.st_uid, previous.st_gid):
                    os.fchown(stream.fileno(), previous.st_uid, previous.st_gid)
            os.fchmod(stream.fileno(), stat.S_IMODE(previous.st_mode) if previous is not None else 0o640)
            stream.write(raw)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    return True

if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("supply the installed identity environment path")
    try:
        update(Path(sys.argv[1]), os.environ)
    except (OSError, UnicodeError, ValueError) as error:
        # Neither credential values nor the environment belong in deployment logs.
        raise SystemExit("identity configuration update failed: " + type(error).__name__) from None
