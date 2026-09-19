#!/usr/bin/env python3
"""Atomically merge explicitly supplied external-agent credentials."""
import os
from pathlib import Path
import stat
import sys
import tempfile

KEYS = (
    "OPENAI_API_KEY",
    "ANTHROPIC_API_KEY",
    "XAI_API_KEY",
    "GEMINI_API_KEY",
    "GOOGLE_API_KEY",
    "OPENROUTER_API_KEY",
    "GROQ_API_KEY",
    "DEEPSEEK_API_KEY",
    "MISTRAL_API_KEY",
    "OLLAMA_API_KEY",
    "LAPLACE_AGENT_API_KEY",
)


def update(path, environment):
    path = Path(path)
    selected = {key: environment[key] for key in KEYS if environment.get(key)}
    for value in selected.values():
        if any(char in value for char in ("\n", "\r", "\0")):
            raise ValueError("agent credentials must be single-line environment values")

    previous = path.lstat() if path.exists() or path.is_symlink() else None
    if previous is not None and not stat.S_ISREG(previous.st_mode):
        raise ValueError("agent environment must be a regular file")
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

    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=".agents.", dir=path.parent)
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
        raise SystemExit("supply the installed agent environment path")
    try:
        update(Path(sys.argv[1]), os.environ)
    except (OSError, UnicodeError, ValueError) as error:
        raise SystemExit("agent configuration update failed: " + type(error).__name__) from None
