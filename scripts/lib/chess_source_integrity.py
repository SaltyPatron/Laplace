"""Read actual checkout bytes against original Git objects, independently of index hints."""
import os
from pathlib import Path
import stat
import subprocess


def git_command(source, *arguments):
    return ["git", "--no-replace-objects", "-c", "safe.directory=" + str(source),
            "-c", "core.fsmonitor=false", "-C", str(source), *arguments]


def git(source, *arguments):
    return subprocess.run(git_command(source, *arguments), text=True,
                          capture_output=True, check=True).stdout.strip()


def verify_checkout(source, expected_commit, label="Git dependency"):
    """Compare actual tracked bytes and modes with unfiltered committed objects.

    Status remains an additional index/untracked check. Its cached stat data,
    assume-unchanged/skip-worktree flags, filters and core.fileMode setting are
    not evidence that the compiler will read the committed source.
    """
    if git(source, "rev-parse", "HEAD") != expected_commit:
        raise ValueError(f"{label} source HEAD changed during verification; preserved")
    tree = subprocess.run(git_command(source, "ls-tree", "-rz", "--full-tree", expected_commit),
                          capture_output=True, check=True).stdout
    if not tree:
        raise ValueError(f"{label} source commit contains no tracked files")
    process = subprocess.Popen(git_command(source, "cat-file", "--batch"),
                               stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        entries = tree.rstrip(b"\0").split(b"\0")
        total_bytes = 0
        for entry in entries:
            descriptor, name = entry.split(b"\t", 1)
            mode, kind, object_id = descriptor.split()
            relative = Path(os.fsdecode(name))
            if relative.is_absolute() or ".." in relative.parts or kind != b"blob" or mode not in (
                    b"100644", b"100755", b"120000"):
                raise ValueError(f"{label} source contains an unsupported tracked entry")
            path = source / relative
            for parent in relative.parents:
                if parent != Path(".") and (source / parent).is_symlink():
                    raise ValueError(f"{label} source has a changed directory type; preserved: {relative}")
            before = path.lstat()
            regular = mode != b"120000"
            if ((regular and not stat.S_ISREG(before.st_mode))
                    or (not regular and not stat.S_ISLNK(before.st_mode))
                    or (regular and os.name != "nt"
                        and bool(before.st_mode & stat.S_IXUSR) != (mode == b"100755"))):
                raise ValueError(f"{label} source has local file type/mode changes; preserved: {relative}")
            process.stdin.write(object_id + b"\n")
            process.stdin.flush()
            header = process.stdout.readline().rstrip(b"\n").split()
            if len(header) != 3 or header[:2] != [object_id, b"blob"]:
                raise ValueError(f"{label} committed source object could not be read")
            size = int(header[2])
            total_bytes += size
            actual = path.open("rb") if regular else None
            try:
                if regular:
                    opened = os.fstat(actual.fileno())
                    if (opened.st_dev, opened.st_ino) != (before.st_dev, before.st_ino):
                        raise ValueError(f"{label} source changed while opening; preserved: {relative}")
                elif os.fsencode(os.readlink(path)) != process.stdout.read(size):
                    raise ValueError(f"{label} source has local symlink changes; preserved: {relative}")
                remaining = size if regular else 0
                while remaining:
                    expected = process.stdout.read(min(remaining, 1024 * 1024))
                    if not expected or actual.read(len(expected)) != expected:
                        raise ValueError(f"{label} source has local byte changes; preserved: {relative}")
                    remaining -= len(expected)
                if regular and actual.read(1):
                    raise ValueError(f"{label} source has local byte changes; preserved: {relative}")
                if process.stdout.read(1) != b"\n":
                    raise ValueError(f"{label} committed source object was truncated")
                after = path.lstat()
                if ((before.st_dev, before.st_ino, before.st_mode, before.st_size,
                     before.st_mtime_ns, before.st_ctime_ns)
                        != (after.st_dev, after.st_ino, after.st_mode, after.st_size,
                            after.st_mtime_ns, after.st_ctime_ns)):
                    raise ValueError(f"{label} source changed while reading; preserved: {relative}")
            finally:
                if actual is not None:
                    actual.close()
        process.stdin.close()
        if process.wait(timeout=10):
            raise ValueError(f"{label} committed source reader failed")
    finally:
        if process.poll() is None:
            process.kill()
        process.wait(timeout=10)
        for stream in (process.stdin, process.stdout, process.stderr):
            stream.close()
    if git(source, "rev-parse", "HEAD") != expected_commit:
        raise ValueError(f"{label} source HEAD changed during verification; preserved")
    return {"commit": expected_commit, "tracked_files": len(entries),
            "tracked_blob_bytes": total_bytes, "verification": "raw-committed-blob-bytes",
            "replacement_objects": "disabled",
            "executable_mode_check": "posix-owner-execute-bit" if os.name != "nt" else "not-applicable-on-windows"}
