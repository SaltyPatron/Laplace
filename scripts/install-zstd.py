#!/usr/bin/env python3
"""Build the official locked Zstandard shared library in the configured source estate.

The system library is never replaced. Every new build receives its own persistent
output directory; a verified receipt selects the exact library used by Laplace.
"""
import argparse
import contextlib
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import tempfile
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parents[1]
LOCK = ROOT / "deploy/zstd-release.json"
SCHEMA = "laplace.zstd-source-build/v1"


def load_lock():
    lock = json.loads(LOCK.read_text())
    if lock["repository"] != "https://github.com/facebook/zstd.git" or not re.fullmatch(r"[0-9a-f]{40}", lock["commit"]):
        raise ValueError("Zstandard lock must identify the official repository and complete commit")
    return lock


def configuration():
    spec = importlib.util.spec_from_file_location("laplace_chess_configuration", ROOT / "scripts/check-chess-dependencies.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    prefix = Path(os.environ.get("LAPLACE_INSTALL_PREFIX", "/opt/laplace"))
    return module.configuration(prefix, keys={"LAPLACE_EXTERNAL", "LAPLACE_ZSTD_SOURCE", "LAPLACE_ZSTD_LIBRARY", "LAPLACE_ZSTD_BUILD"})


def external_root():
    default = ROOT / "external" if os.name == "nt" else Path("/build/external")
    return Path(configuration().get("LAPLACE_EXTERNAL", str(default))).absolute()


def source_root():
    return Path(configuration().get("LAPLACE_ZSTD_SOURCE", str(external_root() / "zstd"))).absolute()


def build_root():
    explicit = configuration().get("LAPLACE_ZSTD_BUILD")
    if explicit:
        return Path(explicit).absolute()
    base = Path(os.environ.get("LAPLACE_BUILD_ROOT", str(ROOT / "build" if os.name == "nt" else Path("/build/laplace/build"))))
    return (base / "zstd").absolute()


def digest(path):
    value = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def run(argv, timeout=300, **kwargs):
    result = subprocess.run([str(arg) for arg in argv], capture_output=True, text=True, timeout=timeout, **kwargs)
    if result.returncode:
        raise RuntimeError(f"{argv[0]} failed ({result.returncode}): {(result.stderr or result.stdout)[-4000:]}")
    return result.stdout.strip()


def git(source, *arguments):
    return run(["git", "-c", "safe.directory=" + str(source.resolve()), "-C", source, *arguments])


def git_metadata(source, name):
    path = Path(git(source, "rev-parse", "--git-path", name))
    return path if path.is_absolute() else source / path


@contextlib.contextmanager
def exclusive(path):
    """Preserve the existing lock inode/owner shared by operator and runner."""
    descriptor = os.open(path, os.O_RDWR | os.O_CREAT, 0o664)
    with os.fdopen(descriptor, "r+", encoding="utf-8") as handle:
        if os.name == "nt":
            import msvcrt
            msvcrt.locking(handle.fileno(), msvcrt.LK_LOCK, 1)
        else:
            import fcntl
            fcntl.flock(handle, fcntl.LOCK_EX)
        try:
            yield handle
        finally:
            if os.name == "nt":
                handle.seek(0)
                msvcrt.locking(handle.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(handle, fcntl.LOCK_UN)


def verify_source(source, lock, selected=True):
    if Path(git(source, "rev-parse", "--show-toplevel")).resolve() != source.resolve():
        raise ValueError(f"Zstandard source must be a repository root: {source}")
    origin = git(source, "remote", "get-url", "origin").rstrip("/").removesuffix(".git")
    origin = origin.replace("git@github.com:", "https://github.com/").replace("ssh://git@github.com/", "https://github.com/")
    if origin != lock["repository"].removesuffix(".git"):
        raise ValueError("Zstandard source origin is not the official facebook/zstd repository")
    if git(source, "status", "--porcelain", "--untracked-files=all"):
        raise ValueError(f"Zstandard source contains local changes; preserved without update or build: {source}")
    if selected:
        if git(source, "rev-parse", "HEAD") != lock["commit"]:
            raise ValueError("Zstandard source does not match the locked release commit")
        header = (source / "lib/zstd.h").read_text()
        version = ".".join(re.search(r"#define ZSTD_VERSION_" + part + r"\s+(\d+)", header)[1] for part in ("MAJOR", "MINOR", "RELEASE"))
        if version != lock["version"]:
            raise ValueError("Zstandard header version does not match the release lock")
        for name, expected in lock["license_sha256"].items():
            if digest(source / name) != expected:
                raise ValueError(f"Zstandard {name} does not match the official source license")


def update_source(source, lock):
    verify_source(source, lock, selected=False)
    try:
        selected = git(source, "rev-parse", "--verify", "refs/tags/" + lock["tag"] + "^{commit}")
    except RuntimeError:
        git(source, "fetch", "origin", "refs/tags/" + lock["tag"] + ":refs/tags/" + lock["tag"])
        selected = git(source, "rev-parse", "refs/tags/" + lock["tag"] + "^{commit}")
    if selected != lock["commit"]:
        raise ValueError("Zstandard official release tag does not match the locked commit")
    previous = git(source, "rev-parse", "HEAD")
    if previous != selected:
        git(source, "update-ref", "refs/laplace/zstd-before-update/" + previous, previous)
        git(source, "checkout", "--detach", selected)
    verify_source(source, lock)


def github_json(url):
    request = Request(url, headers={"Accept": "application/vnd.github+json", "User-Agent": "Laplace-dependency-check"})
    with urlopen(request, timeout=30) as response:
        return json.load(response)


def check_latest(lock=None):
    lock = lock or load_lock()
    release = github_json(lock["latest_api"])
    if release.get("draft") or release.get("prerelease") or release.get("tag_name") != lock["tag"]:
        raise ValueError(f"Zstandard lock {lock['tag']} is stale; official stable release is {release.get('tag_name')}")
    commit = github_json("https://api.github.com/repos/facebook/zstd/commits/" + lock["tag"])["sha"]
    if commit != lock["commit"]:
        raise ValueError("Zstandard official stable tag changed from the locked commit")
    return {"component": "zstd", "version": lock["version"], "tag": lock["tag"], "commit": commit, "current": True}


def probe(library, lock):
    library = Path(library).resolve(strict=True)
    spec = importlib.util.spec_from_file_location("laplace_zstd_runtime_probe", ROOT / "scripts/check-zstd-runtime.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    result = module.probe(str(library), 27)
    if result["version"] != lock["version"] or result["library_sha256"] != digest(library) or Path(result["library"]).resolve() != library:
        raise ValueError("Zstandard loaded library identity/version does not match the selected source build")
    return result


def configure_arguments(cmake, source, output, windows=None):
    windows = os.name == "nt" if windows is None else windows
    runtime = output / "runtime"
    args = [cmake, "-S", source / "build/cmake", "-B", output,
            "-DCMAKE_BUILD_TYPE=Release", "-DBUILD_SHARED_LIBS=ON", "-DZSTD_BUILD_SHARED=ON",
            "-DZSTD_BUILD_STATIC=OFF", "-DZSTD_BUILD_PROGRAMS=OFF", "-DZSTD_BUILD_TESTS=OFF",
            "-DZSTD_BUILD_CONTRIB=OFF", "-DZSTD_LEGACY_SUPPORT=ON", "-DZSTD_MULTITHREAD_SUPPORT=ON"]
    for kind in ("LIBRARY", "RUNTIME", "ARCHIVE"):
        args.extend([f"-DCMAKE_{kind}_OUTPUT_DIRECTORY={runtime}", f"-DCMAKE_{kind}_OUTPUT_DIRECTORY_RELEASE={runtime}"])
    if windows:
        # Keep the dependency DLL self-contained with respect to the MSVC CRT.
        args.append("-DZSTD_USE_STATIC_RUNTIME=ON")
    return [str(arg) for arg in args]


def selected_library(output, version, system=None):
    system = platform.system() if system is None else system
    names = ("zstd.dll", "libzstd.dll") if system == "Windows" else ((f"libzstd.{version}.dylib",) if system == "Darwin" else (f"libzstd.so.{version}",))
    matches = [output / "runtime" / name for name in names if (output / "runtime" / name).is_file()]
    if len(matches) != 1:
        raise ValueError(f"Zstandard build must yield exactly one {system} shared library: {output}")
    return matches[0].resolve()


def tool_identity(name):
    path = shutil.which(name)
    if not path:
        raise ValueError(f"Zstandard source build requires {name} on PATH")
    path = Path(path).resolve()
    return {"path": str(path), "sha256": digest(path)}


def recipe_inputs(lock, cmake):
    compiler = os.environ.get("CC") or ("cl" if os.name == "nt" else "cc")
    return {"commit": lock["commit"], "installer_sha256": digest(Path(__file__)),
            "platform": platform.platform(), "machine": platform.machine(),
            "cmake": tool_identity(cmake), "compiler": tool_identity(compiler),
            "environment": {key: os.environ.get(key, "") for key in (
                "CC", "CXX", "CFLAGS", "CXXFLAGS", "CPPFLAGS", "LDFLAGS", "CMAKE_GENERATOR",
                "CMAKE_GENERATOR_PLATFORM", "CMAKE_TOOLCHAIN_FILE")},
            "options": configure_arguments("cmake", Path("SOURCE"), Path("BUILD"))}


def compiler_receipt(output):
    cache = (output / "CMakeCache.txt").read_text()
    result = {}
    for key in ("CMAKE_C_COMPILER", "CMAKE_CXX_COMPILER", "CMAKE_GENERATOR"):
        found = re.search(r"^" + key + r":[^=]+=(.+)$", cache, re.M)
        if found:
            result[key] = found[1]
    for name in ("C", "CXX"):
        paths = sorted((output / "CMakeFiles").glob("*/CMake" + name + "Compiler.cmake"))
        if len(paths) != 1:
            raise ValueError("CMake did not retain a unique compiler identity")
        source = paths[0].read_text()
        for suffix in ("ID", "VERSION", "TARGET"):
            key = "CMAKE_" + name + "_COMPILER_" + suffix
            value = re.search(r"set\(" + key + r' "([^"]*)"\)', source)
            if value:
                result[key] = value[1]
    return result


def record_pin(source, lock):
    if source.resolve() != (external_root() / "zstd").resolve():
        return
    with exclusive(external_root() / "PINS.tsv") as handle:
        original = handle.read()
        lines = [line for line in original.splitlines() if line.split("\t", 1)[0] != "external/zstd"]
        lines.append("\t".join(("external/zstd", lock["repository"], lock["commit"])))
        handle.seek(0)
        handle.write("\n".join(lines) + "\n")
        handle.truncate()
        handle.flush()
        os.fsync(handle.fileno())


def read_receipt(source, lock):
    receipt = json.loads(git_metadata(source, "laplace-zstd-build.json").read_text())
    if receipt.get("schema") != SCHEMA or receipt.get("source_commit") != lock["commit"] or Path(receipt.get("source", "")).resolve() != source.resolve():
        raise ValueError("Zstandard source receipt does not match the selected source release")
    library = Path(receipt["library"])
    if not library.is_absolute() or digest(library) != receipt["library_sha256"]:
        raise ValueError("Zstandard shared library changed since its source build")
    probe(library, lock)
    for name, expected in lock["license_sha256"].items():
        if digest(library.parent / name) != expected:
            raise ValueError("Zstandard runtime license changed since its source build")
    return receipt


def configured_library(source=None):
    lock = load_lock()
    override = os.environ.get("LAPLACE_ZSTD_LIBRARY", "").strip()
    if not override and source is None and not os.environ.get("LAPLACE_ZSTD_SOURCE", "").strip():
        override = configuration().get("LAPLACE_ZSTD_LIBRARY")
    if override:
        path = Path(override)
        if not path.is_absolute():
            raise ValueError("LAPLACE_ZSTD_LIBRARY must be an absolute path")
        probe(path, lock)
        return path.resolve()
    source = (source or source_root()).absolute()
    verify_source(source, lock)
    return Path(read_receipt(source, lock)["library"])


def build(source=None, output_root=None, jobs=None, cmake="cmake", rebuild=False):
    source = (source or source_root()).absolute()
    output_root = (output_root or build_root()).absolute()
    lock = load_lock()
    recipe = recipe_inputs(lock, cmake)
    if jobs is None:
        jobs = int(os.environ.get("CMAKE_BUILD_PARALLEL_LEVEL") or os.environ.get("LAPLACE_BUILD_JOBS") or os.cpu_count() or 1)
    if jobs < 1:
        raise ValueError("Zstandard build jobs must be positive")
    if not source.exists():
        source.parent.mkdir(parents=True, exist_ok=True)
        print(run(["git", "clone", "--branch", lock["tag"], "--depth", "1", lock["repository"], source]), file=sys.stderr)
    verify_source(source, lock, selected=False)
    if output_root.resolve().is_relative_to(source.resolve()):
        raise ValueError("Zstandard build directory must be outside its source checkout")
    with exclusive(git_metadata(source, "laplace-zstd-build.lock")):
        update_source(source, lock)
        if not rebuild:
            try:
                previous = read_receipt(source, lock)
            except (OSError, ValueError, KeyError, RuntimeError):
                previous = {}
            if previous.get("recipe") == recipe:
                return Path(previous["library"])
        output_root.mkdir(parents=True, exist_ok=True)
        output = Path(tempfile.mkdtemp(prefix=lock["version"] + "-", dir=output_root))
        if os.name != "nt":
            output.chmod(0o2775)
        # Leave failed candidates and prior verified libraries intact for diagnosis.
        args = configure_arguments(cmake, source, output)
        print(run(args), file=sys.stderr)
        print(run([cmake, "--build", output, "--config", "Release", "--target", "libzstd_shared", "--parallel", str(jobs)], timeout=1200), file=sys.stderr)
        verify_source(source, lock)
        library = selected_library(output, lock["version"])
        verification = probe(library, lock)
        for name in lock["license_sha256"]:
            shutil.copy2(source / name, library.parent / name)
        receipt = {"schema": SCHEMA, "source": str(source.resolve()), "source_repository": lock["repository"],
                   "source_commit": lock["commit"], "source_tree": git(source, "rev-parse", "HEAD^{tree}"),
                   "source_tag": lock["tag"], "version": lock["version"], "library": str(library),
                   "library_sha256": digest(library), "licenses": lock["license_sha256"],
                   "license_directory": str(library.parent), "recipe": recipe,
                   "compiler": compiler_receipt(output), "configure_arguments": args,
                   "cmake_cache_sha256": digest(output / "CMakeCache.txt"), "verification": verification}
        text = json.dumps(receipt, indent=2) + "\n"
        (output / "laplace-zstd-build.json").write_text(text)
        state = git_metadata(source, "laplace-zstd-build.json")
        old = state.read_bytes() if state.exists() else None
        if old is not None:
            (output / "previous-selection.json").write_bytes(old)
        # Retain a shared operator-owned inode. Recovery bytes live beside the
        # verified candidate before this pointer changes; no library is overwritten.
        try:
            with state.open("w") as handle:
                handle.write(text)
                handle.flush()
                os.fsync(handle.fileno())
        except BaseException:
            if old is not None:
                state.write_bytes(old)
            raise
        record_pin(source, lock)
        return library


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-dir", type=Path)
    parser.add_argument("--prefix", type=Path, help="Installed runtime configuration prefix")
    parser.add_argument("--build-dir", type=Path, help="Persistent base directory for independent source builds")
    parser.add_argument("--jobs", type=int)
    parser.add_argument("--cmake", default="cmake")
    parser.add_argument("--rebuild", action="store_true")
    action = parser.add_mutually_exclusive_group()
    action.add_argument("--print-path", action="store_true", help="Verify and print the selected runtime library without installing")
    action.add_argument("--check-latest", action="store_true")
    args = parser.parse_args()
    if args.prefix:
        os.environ["LAPLACE_INSTALL_PREFIX"] = str(args.prefix)
    try:
        if args.check_latest:
            print(json.dumps(check_latest()))
        elif args.print_path:
            print(configured_library(args.source_dir))
        else:
            print(build(args.source_dir, args.build_dir, args.jobs, args.cmake, args.rebuild))
    except (OSError, ValueError, RuntimeError, KeyError, subprocess.TimeoutExpired) as error:
        print(f"Zstandard: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
