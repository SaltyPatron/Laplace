#!/usr/bin/env python3
"""Update the official Stockfish checkout, build it, and use its executable directly.

Stockfish participates in Laplace's existing external source tree and PINS.tsv.
Local edits and existing branch tips are preserved. Upstream make downloads and
validates its selected NNUE network; no binary release packages are installed.
"""
import argparse
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import platform
import queue
import shutil
import subprocess
import tempfile
import threading
import time
from urllib.request import Request, urlopen

from lib.chess_source_integrity import git, verify_checkout

ROOT = Path(__file__).resolve().parents[1]
LATEST_RELEASE = "https://api.github.com/repos/official-stockfish/Stockfish/releases/latest"


def load_lock():
    return json.loads((ROOT / "deploy/linux/stockfish-release.json").read_text())


def external_root():
    default = ROOT / "external" if platform.system() == "Windows" else Path("/build/external")
    return Path(os.environ.get("LAPLACE_EXTERNAL", str(default))).absolute()


def source_root():
    return Path(os.environ.get("LAPLACE_STOCKFISH_SOURCE", str(external_root() / "stockfish"))).absolute()


def binary_path(source=None):
    return (source or source_root()) / "src" / ("stockfish.exe" if platform.system() == "Windows" else "stockfish")


def configured_binary(prefix=None):
    explicit = os.environ.get("LAPLACE_STOCKFISH")
    if explicit:
        return Path(explicit)
    if prefix is not None:
        config = Path(prefix) / "app/laplace-api.env"
        if config.is_file():
            selected = None
            for line in config.read_text().splitlines():
                if line.startswith("LAPLACE_STOCKFISH="):
                    selected = line.split("=", 1)[1].strip().strip("\"'")
            if selected:
                return Path(selected)
    return binary_path()


def github_json(url):
    request = Request(url, headers={"Accept": "application/vnd.github+json", "User-Agent": "Laplace-dependency-check"})
    with urlopen(request, timeout=30) as response:
        return json.load(response)


def check_latest():
    lock = load_lock()
    upstream = github_json(LATEST_RELEASE)
    tag = upstream.get("tag_name")
    if upstream.get("draft") or upstream.get("prerelease") or tag != lock["tag"]:
        raise ValueError(f"Stockfish source pin is stale: pinned {lock['tag']}, upstream {tag}. "
                         "Update deploy/linux/stockfish-release.json to the official stable tag and commit, "
                         "then run scripts/install-stockfish.py to update and rebuild the external checkout.")
    commit = github_json("https://api.github.com/repos/official-stockfish/Stockfish/commits/" + tag)["sha"]
    if commit != lock["commit"]:
        raise ValueError("Stockfish official release commit differs from the source pin")
    print(f"Stockfish {lock['version']} is the latest stable official release; source commit {commit} matches")


def snapshot(prefix, state):
    """Record only the managed pointer and its API config, never credentials."""
    link = prefix / "bin/stockfish"
    config = prefix / "app/laplace-api.env"
    saved = {
        "link": os.readlink(link) if link.is_symlink() else None,
        "regular_file": link.exists() and not link.is_symlink(),
        "config": [line for line in config.read_text().splitlines(keepends=True)
                   if line.startswith("LAPLACE_STOCKFISH=")] if config.exists() else [],
    }
    with state.open("x") as output:
        json.dump(saved, output)


def restore(prefix, state):
    """Restore the prior launch contract, retaining downloaded immutable releases."""
    saved = json.loads(state.read_text())
    link = prefix / "bin/stockfish"
    if saved["link"] is not None:
        # Replace only our managed link, or the exact pre-existing symlink.
        if link.is_symlink() and os.readlink(link) == saved["link"]:
            pass
        else:
            if link.exists() or link.is_symlink():
                if not link.is_symlink() or not link.resolve().is_relative_to((prefix / "stockfish").resolve()):
                    raise ValueError("unmanaged Stockfish path changed during publish; preserved")
            with tempfile.TemporaryDirectory(prefix=".stockfish-restore-", dir=link.parent) as temporary:
                candidate = Path(temporary) / "stockfish"
                candidate.symlink_to(saved["link"])
                os.replace(candidate, link)
    elif not saved["regular_file"] and link.is_symlink():
        if not link.resolve().is_relative_to((prefix / "stockfish").resolve()):
            raise ValueError("unmanaged Stockfish link changed during publish; preserved")
        link.unlink()
    config = prefix / "app/laplace-api.env"
    if config.exists():
        lines = [line for line in config.read_text().splitlines(keepends=True)
                 if not line.startswith("LAPLACE_STOCKFISH=")]
        if lines and saved["config"] and not lines[-1].endswith("\n"):
            lines[-1] += "\n"
        lines.extend(saved["config"])
        with tempfile.TemporaryDirectory(prefix=".stockfish-env-", dir=config.parent) as temporary:
            candidate = Path(temporary) / config.name
            shutil.copy2(config, candidate)
            candidate.write_text("".join(lines))
            os.replace(candidate, config)
    print("Previous Stockfish launch contract restored; immutable releases retained")


def digest(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def probe(binary, version):
    """Require readiness and legal search, including usable embedded NNUE data."""
    process = subprocess.Popen([str(binary)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=subprocess.STDOUT, text=True, bufsize=1)
    received = queue.Queue()

    def read_output():
        try:
            for line in process.stdout:
                received.put(line.rstrip())
        finally:
            received.put(None)

    reader = threading.Thread(target=read_output, daemon=True)
    reader.start()

    def send(command):
        process.stdin.write(command + "\n")
        process.stdin.flush()

    def until(predicate):
        lines = []
        deadline = time.monotonic() + 30
        while len(lines) < 10000:
            try:
                line = received.get(timeout=max(0, deadline - time.monotonic()))
            except queue.Empty as error:
                raise ValueError("Stockfish UCI readiness/search timed out") from error
            if line is None:
                code = process.wait(timeout=5)
                if code:
                    raise subprocess.CalledProcessError(code, [str(binary)], "\n".join(lines))
                raise ValueError("Stockfish exited before completing the UCI readiness/search check")
            lines.append(line)
            if predicate(line):
                return lines
        raise ValueError("Stockfish UCI output exceeded the verification limit")

    try:
        send("uci")
        handshake = until(lambda line: line == "uciok")
        if "id name Stockfish " + version not in handshake:
            raise ValueError("Stockfish version/UCI handshake did not match the release lock")
        for option in ("Threads", "Hash", "UCI_LimitStrength", "UCI_Elo"):
            if not any(line.startswith("option name " + option + " type ") for line in handshake):
                raise ValueError(f"Stockfish required UCI option is absent: {option}")
        send("setoption name Threads value 1")
        send("setoption name Hash value 16")
        send("isready")
        until(lambda line: line == "readyok")
        send("ucinewgame")
        send("position startpos")
        send("go depth 1")
        search = until(lambda line: line.startswith("bestmove "))
        legal = {file + "2" + file + rank for file in "abcdefgh" for rank in "34"}
        legal.update(("b1a3", "b1c3", "g1f3", "g1h3"))
        if search[-1].split()[1] not in legal:
            raise ValueError("Stockfish did not return a legal move from the initial position")
        send("quit")
        if process.wait(timeout=10):
            raise subprocess.CalledProcessError(process.returncode, [str(binary)])
        return next(line for line in handshake if line.startswith("option name UCI_Elo "))
    finally:
        if process.poll() is None:
            process.kill()
        process.wait(timeout=10)
        reader.join(timeout=5)
        process.stdin.close()
        process.stdout.close()


def verify_source(source, expected_commit):
    return verify_checkout(source, expected_commit, "Stockfish")


def update_source(source, lock):
    """Fetch the stable source pin without resetting local edits or branch history."""
    if not source.exists():
        source.parent.mkdir(parents=True, exist_ok=True)
        subprocess.run(["git", "--no-replace-objects", "clone", "--branch", lock["tag"], "--depth", "1",
                        lock["repository"], str(source)], check=True)
    if Path(git(source, "rev-parse", "--show-toplevel")).resolve() != source.resolve():
        raise ValueError(f"Stockfish source is not a repository root: {source}")
    origin = git(source, "config", "--get", "remote.origin.url").rstrip("/")
    if origin.removesuffix(".git") not in (
            "https://github.com/official-stockfish/Stockfish",
            "git@github.com:official-stockfish/Stockfish"):
        raise ValueError(f"Stockfish source origin is not the official repository: {source}")
    if git(source, "status", "--porcelain", "--untracked-files=all"):
        raise ValueError(f"Stockfish source has local changes; preserved without checkout or rebuild: {source}")
    previous = git(source, "rev-parse", "HEAD")
    verify_source(source, previous)
    try:
        selected = git(source, "rev-parse", "--verify", "refs/tags/" + lock["tag"] + "^{commit}")
    except subprocess.CalledProcessError:
        git(source, "fetch", "origin", "refs/tags/" + lock["tag"] + ":refs/tags/" + lock["tag"])
        selected = git(source, "rev-parse", "refs/tags/" + lock["tag"] + "^{commit}")
    if selected != lock["commit"]:
        raise ValueError("Stockfish release tag does not match the pinned official source commit")
    if previous != selected:
        git(source, "update-ref", "refs/laplace/stockfish-before-update/" + previous, previous)
        git(source, "checkout", "--detach", selected)
    verify_source(source, selected)
    return selected


def record_external_pin(source, lock):
    # An explicit source override remains outside the default external roster.
    if source.resolve() != (external_root() / "stockfish").resolve():
        return
    pins = external_root() / "PINS.tsv"
    # Preserve the operator-owned inode: shared group write does not permit a
    # runner to chown a replacement back to another owner. Serialize with the
    # CuteChess source updater before reading and rewriting the shared roster.
    descriptor = os.open(pins, os.O_RDWR | os.O_CREAT, 0o664)
    with os.fdopen(descriptor, "r+", encoding="utf-8", newline="") as output:
        if os.name == "nt":
            import msvcrt
            msvcrt.locking(output.fileno(), msvcrt.LK_LOCK, 1)
        else:
            import fcntl
            fcntl.flock(output.fileno(), fcntl.LOCK_EX)
        try:
            original = output.read()
            lines = [line for line in original.splitlines()
                     if line.split("\t", 1)[0] != "external/stockfish"]
            lines.append("\t".join(("external/stockfish", lock["repository"], lock["commit"])))
            text = "\n".join(lines) + "\n"
            if text != original:
                output.seek(0)
                output.write(text)
                output.truncate()
                output.flush()
                os.fsync(output.fileno())
        finally:
            if os.name == "nt":
                output.seek(0)
                msvcrt.locking(output.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(output.fileno(), fcntl.LOCK_UN)


@contextmanager
def regenerate_dependencies(source, state_directory):
    """Let upstream make regenerate .depend without executing an old include.

    Preserve the operator's exact prior file or symlink. The backup stays in
    Git metadata if restoration fails, so a cleanup error cannot erase it.
    """
    dependency = source / "src/.depend"
    if not dependency.exists() and not dependency.is_symlink():
        yield
        return
    if not dependency.is_file() and not dependency.is_symlink():
        raise ValueError("Stockfish .depend has an unsupported file type; preserved")
    temporary = Path(tempfile.mkdtemp(prefix="laplace-stockfish-depend-", dir=state_directory))
    backup = temporary / ".depend"
    os.replace(dependency, backup)
    try:
        yield
    finally:
        try:
            os.replace(backup, dependency)
        except OSError as error:
            raise ValueError(f"Stockfish prior .depend remains preserved at {backup}; restoration failed") from error
        temporary.rmdir()


def build(source=None, jobs=None, make=None, compiler=None, rebuild=False):
    source = (source or source_root()).absolute()
    lock = load_lock()
    for tool in ("git", "sh"):
        if not shutil.which(tool):
            raise ValueError(f"Stockfish source build requires {tool} on PATH")
    make = make or os.environ.get("LAPLACE_STOCKFISH_MAKE", "make")
    compiler = compiler or os.environ.get("LAPLACE_STOCKFISH_COMP", "gcc")
    if compiler not in ("gcc", "clang", "mingw", "icx"):
        raise ValueError("Stockfish compiler must be gcc, clang, mingw or icx")
    if not shutil.which(make):
        raise ValueError(f"Stockfish source build requires GNU make ({make}); on Windows use the MSYS2 UCRT64 toolchain")
    if not (shutil.which("curl") or shutil.which("wget")):
        raise ValueError("Stockfish NNUE acquisition requires curl or wget")
    if not (shutil.which("sha256sum") or shutil.which("shasum")):
        raise ValueError("Stockfish NNUE validation requires sha256sum or shasum")
    commit = update_source(source, lock)
    build_environment = dict(os.environ, GIT_NO_REPLACE_OBJECTS="1")
    cpu = subprocess.run(["sh", str(source / "scripts/get_native_properties.sh")],
                         cwd=source / "src", env=build_environment,
                         capture_output=True, text=True, check=True).stdout.strip()
    compiler_executable = os.environ.get("CXX", {"gcc": "g++", "mingw": "g++", "clang": "clang++", "icx": "icpx"}[compiler])
    compiler_version = subprocess.run([compiler_executable, "--version"], capture_output=True,
                                      text=True, check=True).stdout.splitlines()[0]
    recipe = {"commit": commit, "arch": "native", "cpu": cpu,
              "compiler": compiler, "compiler_version": compiler_version,
              "source_integrity": "git-committed-bytes-and-modes-v1",
              "dependency_include": "regenerated-by-upstream-make-with-prior-file-preserved"}
    state = Path(git(source, "rev-parse", "--git-path", "laplace-stockfish-build.json"))
    if not state.is_absolute():
        state = source / state
    binary = binary_path(source)
    previous = json.loads(state.read_text()) if state.exists() else {}
    if rebuild or previous.get("recipe") != recipe or not binary.is_file() or previous.get("binary_sha256") != digest(binary):
        count = jobs if jobs is not None else int(
            os.environ.get("CMAKE_BUILD_PARALLEL_LEVEL") or os.environ.get("LAPLACE_BUILD_JOBS")
            or os.cpu_count() or 1)
        if count < 1:
            raise ValueError("Stockfish build jobs must be positive")
        # Upstream objclean removes stockfish even when EXE names a candidate.
        # Keep the prior executable for rollback throughout the build; admit
        # the candidate only after a real search and source verification.
        with tempfile.TemporaryDirectory(prefix="laplace-stockfish-previous-", dir=state.parent) as temporary:
            previous_binary = Path(temporary) / binary.name
            candidate = binary.with_name("stockfish.pending.exe" if platform.system() == "Windows" else "stockfish.pending")
            if candidate.exists() or candidate.is_symlink():
                raise ValueError(f"Stockfish build candidate already exists; preserved: {candidate}")
            if binary.is_file():
                shutil.copy2(binary, previous_binary)
            activated = False
            try:
                with regenerate_dependencies(source, state.parent):
                    subprocess.run([make, "-C", str(source / "src"), "-j", str(count),
                                    "profile-build", "ARCH=native", "COMP=" + compiler,
                                    "CXX=" + compiler_executable, "EXE=" + candidate.name],
                                   env=build_environment, check=True)
                    capabilities = probe(candidate, lock["version"])
                    verify_source(source, commit)
                os.replace(candidate, binary)
                activated = True
                state.write_text(json.dumps({"recipe": recipe, "binary_sha256": digest(binary)}) + "\n")
            except BaseException:
                if previous_binary.exists():
                    os.replace(previous_binary, binary)
                elif activated and binary.exists():
                    binary.unlink()
                raise
            finally:
                if candidate.exists():
                    candidate.unlink()
    else:
        capabilities = probe(binary, lock["version"])
        verify_source(source, commit)
    record_external_pin(source, lock)
    print(f"Stockfish {lock['version']} source {commit} verified at {binary}")
    print(capabilities)
    return binary


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-dir", type=Path, help="Official source checkout; defaults to the existing external/stockfish dependency")
    parser.add_argument("--jobs", type=int, help="Parallel compiler jobs; honors CMAKE_BUILD_PARALLEL_LEVEL/LAPLACE_BUILD_JOBS before available CPUs")
    parser.add_argument("--make", help="GNU make executable")
    parser.add_argument("--compiler", choices=("gcc", "clang", "mingw", "icx"))
    parser.add_argument("--rebuild", action="store_true", help="Rebuild even when source, compiler, CPU and executable match")
    parser.add_argument("--prefix", type=Path, default=Path("/opt/laplace"), help="Application prefix for snapshot/restore only")
    action = parser.add_mutually_exclusive_group()
    action.add_argument("--snapshot", type=Path, help="Save the previous application launch configuration for CI rollback")
    action.add_argument("--restore", type=Path, help="Restore the previous CI launch configuration")
    action.add_argument("--check-latest", action="store_true", help="Check the official latest stable tag and source commit")
    action.add_argument("--check-binary", type=Path, help="Verify the selected executable version, UCI readiness and legal search")
    action.add_argument("--print-path", "--print-binary", action="store_true", help="Print the direct source-build executable path without installing")
    args = parser.parse_args()
    if args.check_latest:
        check_latest()
    elif args.check_binary:
        print(probe(args.check_binary, load_lock()["version"]))
    elif args.print_path:
        print(binary_path(args.source_dir))
    elif args.snapshot:
        snapshot(args.prefix.absolute(), args.snapshot)
    elif args.restore:
        restore(args.prefix.absolute(), args.restore)
    else:
        build(args.source_dir, args.jobs, args.make, args.compiler, args.rebuild)
