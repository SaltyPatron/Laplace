#!/usr/bin/env python3
"""Acquire pinned Xpra data in an owned prefix; never install host packages."""
from __future__ import annotations

import argparse
import fcntl
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import selectors
import shutil
import signal
import stat
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.request

_sibling = Path(__file__).resolve().with_name("chess_x11_runtime.py")
_spec = importlib.util.spec_from_file_location("laplace_selected_chess_x11_runtime", _sibling)
if _spec is None or _spec.loader is None:
    raise RuntimeError("selected X11 runtime helper is absent")
x11 = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(x11)

SCHEMA = "laplace.private-xpra-runtime/v1"
DEFAULT_ROOT = Path("/opt/laplace/tools/chess/xpra-runtime")
PYTHON = "/usr/bin/python3"
MAX_DOWNLOAD = 128 * 1024 * 1024
MAX_EXPANDED = 512 * 1024 * 1024
MAX_FILES = 60000
FINGERPRINT = "B4993B57323148E37977E5D873254CAD17978FAF"
PINS = (
    ("xpra-common", 7811512, "e09ec4801a6b806a97d372bd8d28cbe15057e9d8b7ea38b086cec7a63d160f35"),
    ("xpra-server", 417084, "60fa28fb8def36311bd65cb3d06ef0f74ed058fe7c98dbb1cdb5c836bbd9a73a"),
    ("xpra-x11", 831272, "18c8a2ba4863995a5553706fae9105f61e4b2c935b55a2b11ca6f59ce7541213"),
    ("xpra-client", 132840, "97fca25d2679497473580cbc5aa1199ba4e135af1b0d52ff61759b4fcb29b531"),
    ("xpra-client-gtk3", 248652, "2323e3f29961641badcf03b99f03268d45e158f7e2dac6d14e480016bd94f015"),
)
VERSION = "6.5.3-r0-1"
DEPENDENCIES = ("python3-gi", "python3-gi-cairo", "python3-cairo", "python3-pil",
                "python3-dbus", "gir1.2-gtk-3.0", "dbus-x11", "xauth", "x11-xkb-utils")
require, digest, canonical = x11.require, x11.digest, x11.canonical
execute = x11.execute


def runtime_root():
    return Path(os.environ.get("LAPLACE_XPRA_RUNTIME_ROOT", str(DEFAULT_ROOT))).absolute()


def owned_directory(path, *, create=False):
    path = Path(path).absolute()
    if create:
        missing, current = [], path
        while not current.exists():
            require(not current.is_symlink(), "runtime directory is a broken link")
            missing.append(current)
            current = current.parent
        x11.physical_directory(current)
        for directory in reversed(missing):
            try:
                directory.mkdir(mode=0o700)
            except FileExistsError:
                pass
            owned_directory(directory)
    path = x11.physical_directory(path)
    info = path.stat()
    require(info.st_uid == os.geteuid() and not info.st_mode & 0o022,
            "Xpra directory must be owned by the current account and not writable by others")
    return path


def check_release(release):
    require(release.get("schema") == "laplace.cutechess-user-runtime-release/v1"
            and release.get("version") == "6.5.3"
            and release.get("debian_version") == VERSION
            and release.get("distribution") == "jammy"
            and release.get("architecture") == "amd64"
            and release.get("repository") == "https://xpra.org"
            and release.get("signing_fingerprint") == FINGERPRINT
            and release.get("packages") ==
                [{"name": name, "size": size, "sha256": sha} for name, size, sha in PINS],
            "Xpra release differs from the reviewed official selection")


def key_fingerprint(text):
    values, awaiting = [], False
    for line in text.splitlines():
        parts = line.split(":")
        if parts[0] == "pub":
            awaiting = True
        elif parts[0] == "sub":
            awaiting = False
        elif parts[0] == "fpr" and awaiting:
            require(len(parts) > 9, "invalid signing key fingerprint record")
            values.append(parts[9])
            awaiting = False
    require(values and set(values) == {FINGERPRINT}, "official Xpra signing key differs")


def download_key(path, deadline):
    remaining = min(30, deadline - time.monotonic())
    require(remaining > 0, "acquisition deadline expired")
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open("https://xpra.org/xpra.asc", timeout=remaining) as response:
        require(response.geturl().startswith("https://xpra.org/"),
                "signing key redirected outside the official origin")
        data = bytearray()
        while True:
            require(time.monotonic() < deadline, "acquisition deadline expired")
            chunk = response.read(65536)
            if not chunk:
                break
            data.extend(chunk)
            require(len(data) <= 1024 * 1024, "signing key exceeds its bound")
    path.write_bytes(data)


def simulation_packages(text):
    require(not re.search(r"^Remv ", text, re.M), "dependency selection would remove packages")
    rows = []
    for line in text.splitlines():
        if not line.startswith("Inst "):
            continue
        match = re.fullmatch(r"Inst (\S+) (?:\[[^]]+\] )?\((\S+) .+\)", line)
        require(match is not None, "unrecognized dependency selection")
        name, version = match.groups()
        require(x11.PACKAGE.fullmatch(name) and x11.VERSION.fullmatch(version),
                "invalid dependency identity")
        require(name.split(":")[0] not in
                ("libc6", "libc-bin", "libgcc-s1", "libstdc++6", "python3-minimal",
                 "python3.10-minimal", "libpython3.10-minimal", "python3.10"),
                "selection requires a host C runtime or Python replacement")
        rows.append((name, version))
    require(len(rows) <= 64 and len(rows) == len(set(rows)),
            "dependency closure exceeds its bound or repeats packages")
    return sorted(rows)


def package_record(text, name, version):
    result = x11.package_metadata(text, name, version)
    if name.split(":")[0].startswith("xpra"):
        expected = next((pin for pin in PINS if pin[0] == name.split(":")[0]), None)
        require(expected is not None and version == VERSION
                and result["architecture"] == "amd64"
                and result["bytes"] == expected[1] and result["sha256"] == expected[2],
                "authenticated Xpra package differs from its exact pin")
        filename = "dists/jammy/main/binary-amd64/" + expected[0] + "_" + VERSION + "_amd64.deb"
        records = [paragraph for paragraph in text.split("\n\n")
                   if ("Package: " + expected[0] + "\n") in paragraph + "\n"
                   and ("Version: " + VERSION + "\n") in paragraph + "\n"]
        require(records and all(("Filename: " + filename + "\n") in record + "\n"
                                for record in records), "official Xpra package location differs")
    return result


def bounded_payload(archive, destination, deadline):
    """Bound decompressed bytes while dpkg streams; do not run maintainer scripts."""
    child = subprocess.Popen(["/usr/bin/dpkg-deb", "--fsys-tarfile", str(archive)],
                             stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                             start_new_session=True)
    total = 0
    try:
        with selectors.DefaultSelector() as selector, destination.open("xb") as output:
            selector.register(child.stdout, selectors.EVENT_READ)
            while True:
                remaining = deadline - time.monotonic()
                require(remaining > 0, "package decompression exceeded its wall deadline")
                if not selector.select(min(remaining, 1)):
                    continue
                chunk = os.read(child.stdout.fileno(), 65536)
                if not chunk:
                    break
                total += len(chunk)
                require(total <= MAX_EXPANDED, "decompressed package exceeds its byte bound")
                output.write(chunk)
            require(child.wait(timeout=max(.01, deadline - time.monotonic())) == 0,
                    "package decompression failed")
    finally:
        for signum in (signal.SIGTERM, signal.SIGKILL):
            try:
                os.killpg(child.pid, signum)
            except ProcessLookupError:
                break
            try:
                child.wait(timeout=.5)
            except subprocess.TimeoutExpired:
                pass
        child.wait(timeout=1)
        child.stdout.close()


def member_path(name):
    path = PurePosixPath(name)
    require(not path.is_absolute() and ".." not in path.parts and "\x00" not in name,
            "package member escapes its private root")
    return path


def extract_payload(payload, root, budget):
    """Extract validated data without following package links or special files."""
    with tarfile.open(payload, "r:") as archive:
        links, count = [], 0
        for item in archive:
            count += 1
            require(count + budget["count"] <= MAX_FILES, "package file count exceeds its bound")
            relative = member_path(item.name)
            require(item.isdir() or item.isfile() or item.issym() or item.islnk(),
                    "package contains a special file")
            require(not item.mode & 0o6000, "package contains a privileged executable")
            require(0 <= item.size <= MAX_EXPANDED, "invalid package member size")
            budget["bytes"] += item.size
            require(budget["bytes"] <= MAX_EXPANDED, "package expansion exceeds its bound")
            target = root.joinpath(*relative.parts)
            require(not any(p.is_symlink() for p in (target.parent, *target.parent.parents)
                            if p.is_relative_to(root)),
                    "package member traverses an extracted link")
            if item.issym() or item.islnk():
                link = member_path(item.linkname) if item.islnk() else PurePosixPath(item.linkname)
                # Debian absolute links are rebased inside this generation only.
                combined = (PurePosixPath(*link.parts[1:]) if link.is_absolute()
                            else relative.parent / link if item.issym() else link)
                stack = []
                for part in combined.parts:
                    if part == "..":
                        require(bool(stack), "package link escapes its private root")
                        stack.pop()
                    elif part != ".":
                        stack.append(part)
                require(bool(stack), "package link points at its private root")
                links.append((item, target, root.joinpath(*stack)))
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            if item.isdir():
                require(not target.is_symlink() and (not target.exists() or target.is_dir()),
                        "package directory conflicts with an extracted file")
                target.mkdir(exist_ok=True)
                continue
            require(not target.is_symlink() and (not target.exists() or target.is_file()),
                    "package file conflicts with an extracted object")
            stream = archive.extractfile(item)
            require(stream is not None, "package file has no data")
            existed = target.exists()
            with stream, target.open("rb" if existed else "xb") as output:
                remaining = item.size
                while remaining:
                    data = stream.read(min(65536, remaining))
                    require(bool(data), "package file length differs")
                    remaining -= len(data)
                    if existed:
                        require(output.read(len(data)) == data, "packages disagree about a shared file")
                    else:
                        output.write(data)
                require(not stream.read(1) and (not existed or not output.read(1)),
                        "package file length differs")
            mode = 0o755 if item.mode & 0o111 else 0o644
            if existed:
                require(stat.S_IMODE(target.stat().st_mode) == mode, "packages disagree about file mode")
            else:
                target.chmod(mode)
        for item, target, link_target in links:
            target.parent.mkdir(parents=True, exist_ok=True)
            require(not any(p.is_symlink() for p in (target.parent, *target.parent.parents)
                            if p.is_relative_to(root)), "package link parent is another link")
            if item.islnk():
                require(link_target.is_file() and not link_target.is_symlink(),
                        "package hard link target is not a regular file")
                require(not target.exists() and not target.is_symlink(), "package hard link collides")
                os.link(link_target, target)
            else:
                value = os.path.relpath(link_target, target.parent)
                if target.is_symlink():
                    require(os.readlink(target) == value, "package links conflict")
                else:
                    require(not target.exists(), "package link conflicts with an extracted object")
                    target.symlink_to(value)
        budget["count"] += count


def file_inventory(root):
    result, total = {}, 0
    for path in sorted(root.rglob("*")):
        relative = path.relative_to(root).as_posix()
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode):
            require(path.resolve().is_relative_to(root.resolve()), "selected link escapes its root")
            result[relative] = {"symlink": os.readlink(path)}
        elif stat.S_ISREG(info.st_mode):
            total += info.st_size
            result[relative] = {"sha256": digest(path), "bytes": info.st_size,
                                "mode": stat.S_IMODE(info.st_mode)}
        else:
            require(stat.S_ISDIR(info.st_mode), "selected runtime contains a special file")
        require(total <= MAX_EXPANDED and len(result) <= MAX_FILES, "runtime inventory exceeds its bound")
    return result


def selected_x11(document):
    selected = x11.load(document["x11"]["selection_root"])
    require(selected is not None and selected["manifest_sha256"] == document["x11"]["manifest_sha256"]
            and selected["runtime_id"] == document["x11"]["runtime_id"],
            "referenced X11 selection changed")
    return selected


def selected_environment(document, base=None):
    environment = dict(os.environ if base is None else base)
    for key in ("PYTHONHOME", "PYTHONPATH", "PYTHONSTARTUP", "LD_PRELOAD", "LD_AUDIT", "LD_LIBRARY_PATH",
                "GI_TYPELIB_PATH", "GIO_EXTRA_MODULES", "GTK_PATH", "GTK_MODULES"):
        environment.pop(key, None)
    for key in list(environment):
        if key.startswith("XPRA_") and key not in ("XPRA_SESSION_DIR", "XPRA_PRIVATE_XAUTH"):
            environment.pop(key)
    environment["PATH"] = "/usr/bin:/bin"
    environment = x11.selected_environment(selected_x11(document), environment)
    root = Path(document["root"])
    prefixes = {
        "PATH": [root / "usr/bin"],
        "LD_LIBRARY_PATH": [root / "usr/lib/x86_64-linux-gnu", root / "lib/x86_64-linux-gnu"],
        "GI_TYPELIB_PATH": [root / "usr/lib/x86_64-linux-gnu/girepository-1.0"],
        "PYTHONPATH": [root / "usr/lib/python3/dist-packages"],
        "XDG_DATA_DIRS": [root / "usr/share"],
    }
    for key, paths in prefixes.items():
        values = [str(path) for path in paths if path.is_dir()]
        values.extend(filter(None, environment.get(key, "/usr/local/share:/usr/share"
                                                   if key == "XDG_DATA_DIRS" else "").split(":")))
        environment[key] = ":".join(dict.fromkeys(values))
    environment.update(PYTHONNOUSERSITE="1", PYTHONDONTWRITEBYTECODE="1",
                       XPRA_INSTALL_PREFIX=str(root / "usr"),
                       XPRA_RESOURCES_DIR=str(root / "usr/share/xpra"),
                       XPRA_APP_DIR=str(root / "usr/share/xpra"),
                       XPRA_DEFAULT_CONF_DIRS="", XPRA_SYSTEM_CONF_DIRS="", XPRA_USER_CONF_DIRS="")
    return environment


PROBE = r'''
import hashlib, importlib, importlib.util, json, os, pathlib, sys
assert sys.version_info[:2] == (3, 10), "pinned Jammy packages require Python 3.10"
import gi
gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
gi.require_foreign("cairo")
from gi.repository import Gtk, Gdk, GLib, GObject
import cairo, PIL.Image, dbus, xpra
assert xpra.__version__ == "6.5.3"
modules = {}
for name in ("xpra", "xpra.scripts.parsing", "xpra.net.compression",
             "xpra.net.packet_encoding", "xpra.net.rencodeplus.rencodeplus",
             "xpra.net.lz4.lz4", "xpra.codecs.argb.argb", "xpra.codecs.pillow.encoder",
             "xpra.codecs.image", "xpra.client.base.client", "xpra.client.gtk3",
             "gi", "cairo", "PIL.Image", "dbus"):
    module = importlib.import_module(name)
    if getattr(module, "__file__", None):
        modules[name] = str(pathlib.Path(module.__file__).resolve())
for name in ("xpra.x11.bindings.core", "xpra.x11.bindings.window",
             "xpra.x11.bindings.display_source", "xpra.client.gtk3.client"):
    spec = importlib.util.find_spec(name)
    assert spec is not None and spec.origin, "required X11 binding is absent"
    modules[name] = str(pathlib.Path(spec.origin).resolve())
files = set(modules.values())
for module in tuple(sys.modules.values()):
    origin = getattr(module, "__file__", None)
    if origin and pathlib.Path(origin).is_file():
        files.add(str(pathlib.Path(origin).resolve()))
files.add(str(pathlib.Path(sys.executable).resolve()))
for line in pathlib.Path("/proc/self/maps").read_text().splitlines():
    value = line.split(maxsplit=5)
    if len(value) == 6 and value[5].startswith("/"):
        path = pathlib.Path(value[5])
        if path.is_file():
            files.add(str(path.resolve()))
assert len(files) <= 2048
def digest(path):
    result = hashlib.sha256()
    with open(path, "rb") as stream:
        for part in iter(lambda: stream.read(1048576), b""):
            result.update(part)
    return result.hexdigest()
print(json.dumps({"python": list(sys.version_info[:3]), "modules": modules,
                  "loaded_files": {path: digest(path) for path in sorted(files)}}))
'''


def qualify(document, deadline):
    environment = selected_environment(document, {"HOME": str(Path.home()), "LANG": "C", "LC_ALL": "C"})
    xpra = Path(document["root"]) / "usr/bin/xpra"
    require(xpra.is_file() and not xpra.is_symlink(), "selected Xpra entrypoint is absent")
    version = execute([PYTHON, "-s", "-B", str(xpra), "--version"], deadline, env=environment).strip()
    require(re.match(r"(?i)^xpra v?6\.5\.3(?:\b|$)", version) is not None,
            "selected Xpra reports a different version")
    proof = json.loads(execute([PYTHON, "-s", "-B", "-c", PROBE], deadline, env=environment))
    prefix = Path(document["root"]).resolve()
    require(all(Path(path).is_relative_to(prefix) for name, path in proof["modules"].items()
                if name.startswith("xpra")), "Xpra modules escaped the selected generation")
    native_paths = [path for path in proof["modules"].values() if path.endswith(".so")]
    for tool in ("xauth", "dbus-launch", "xkbcomp", "Xvfb"):
        path = (selected_x11(document)["tools"]["Xvfb"] if tool == "Xvfb"
                else shutil.which(tool, path=environment["PATH"]))
        require(path is not None, "selected Xpra helper is absent: " + tool)
        path = str(Path(path).resolve(strict=True))
        proof["loaded_files"][path] = digest(path)
        native_paths.append(path)
    for path in native_paths:
        output = execute(["/usr/bin/ldd", path], deadline, env=environment)
        require("not found" not in output, "selected native module has an unresolved library")
        for line in output.splitlines():
            match = re.search(r"=> (/\S+)", line) or re.match(r"\s*(/\S+)", line)
            if match:
                library = str(Path(match[1]).resolve(strict=True))
                proof["loaded_files"][library] = digest(library)
    require(len(proof["loaded_files"]) <= 2048, "loaded dependency inventory exceeds its bound")
    document.update(tools={"xpra": str(xpra), "python": PYTHON,
                           "Xvfb": selected_x11(document)["tools"]["Xvfb"]},
                    qualification=proof, loaded_files=proof["loaded_files"])
    return document


def load(root=None):
    base = runtime_root() if root is None else Path(root).absolute()
    path = base / "current.json"
    if not path.exists():
        return None
    owned_directory(base)
    info = path.lstat()
    require(stat.S_ISREG(info.st_mode) and info.st_uid == os.geteuid()
            and not info.st_mode & 0o022 and info.st_size <= 32 * 1024 * 1024,
            "Xpra selection is not an owned bounded regular manifest")
    document = json.loads(path.read_text())
    identity = document.pop("manifest_sha256", None)
    require(document.get("schema") == SCHEMA and x11.HEX.fullmatch(str(identity))
            and hashlib.sha256(canonical(document)).hexdigest() == identity,
            "Xpra manifest identity differs")
    check_release(document["release"])
    root_path = Path(document["root"])
    require(x11.HEX.fullmatch(document["runtime_id"])
            and root_path == base / "generations" / document["runtime_id"] / "root",
            "Xpra root differs from the selected generation")
    owned_directory(root_path)
    require(file_inventory(root_path) == document["files"], "selected Xpra package bytes changed")
    for path, expected in document["loaded_files"].items():
        require(Path(path).is_file() and digest(path) == expected,
                "selected Xpra module, executable, or library changed")
    selected_x11(document)
    require(document["tools"] == {"xpra": str(root_path / "usr/bin/xpra"), "python": PYTHON,
                                 "Xvfb": selected_x11(document)["tools"]["Xvfb"]},
            "Xpra tool selection differs")
    document["manifest_sha256"] = identity
    return document


def create_staging_root(work):
    return owned_directory(Path(work) / "root", create=True)


def promote_generation(staging, generation, inventory):
    """Promote only a private, complete staging tree to its owned generation."""
    owned_directory(staging)
    require(file_inventory(staging) == inventory, "staging runtime inventory changed")
    owned_directory(generation, create=True)
    selected_root = Path(generation) / "root"
    if selected_root.exists():
        owned_directory(selected_root)
        require(file_inventory(selected_root) == inventory, "existing runtime generation conflicts")
    else:
        staging.replace(selected_root)
    owned_directory(selected_root)
    return selected_root


def publish_selection(root, document):
    """Recheck the real generation boundary before replacing current.json."""
    root = owned_directory(root)
    identity = document["runtime_id"]
    require(x11.HEX.fullmatch(identity) and Path(document["root"])
            == root / "generations" / identity / "root", "selection generation path differs")
    selected_root = owned_directory(Path(document["root"]))
    require(file_inventory(selected_root) == document["files"],
            "runtime generation changed before selection")
    data = json.dumps(document, indent=2, sort_keys=True) + "\n"
    with tempfile.NamedTemporaryFile(mode="w", dir=root, prefix=".current-", delete=False) as stream:
        temporary = Path(stream.name)
        stream.write(data)
        stream.flush()
        os.fsync(stream.fileno())
    try:
        temporary.replace(root / "current.json")
    finally:
        temporary.unlink(missing_ok=True)


def apt_configuration(work, key):
    ubuntu = Path("/usr/share/keyrings/ubuntu-archive-keyring.gpg")
    require(ubuntu.is_file(), "Ubuntu archive trust keyring is absent")
    for relative in ("lists/partial", "cache/archives/partial", "empty", "logs", "downloads"):
        (work / relative).mkdir(parents=True, exist_ok=True)
    sources = work / "sources.list"
    sources.write_text("".join(
        "deb [arch=amd64 signed-by=" + str(ubuntu) + "] " + uri + " " + suite + " main universe\n"
        for uri, suite in (
            ("https://archive.ubuntu.com/ubuntu", "jammy"),
            ("https://archive.ubuntu.com/ubuntu", "jammy-updates"),
            ("https://security.ubuntu.com/ubuntu", "jammy-security")))
        + "deb [arch=amd64 signed-by=" + str(key) + "] https://xpra.org jammy main\n")
    values = {
        "Dir::Etc::parts": work / "empty", "Dir::Etc::main": "/dev/null",
        "Dir::Etc::sourcelist": sources, "Dir::Etc::sourceparts": work / "empty",
        "Dir::Etc::preferences": "/dev/null", "Dir::Etc::preferencesparts": work / "empty",
        "Dir::State::lists": work / "lists", "Dir::State::status": "/var/lib/dpkg/status",
        "Dir::State::extended_states": work / "extended_states",
        "Dir::Cache": work / "cache", "Dir::Cache::archives": work / "cache/archives",
        "Dir::Cache::pkgcache": "", "Dir::Cache::srcpkgcache": "",
        "Dir::Log": work / "logs", "APT::Install-Recommends": "false",
        "Acquire::Retries": "2", "Acquire::http::Timeout": "30",
        "Acquire::https::Timeout": "30", "Acquire::AllowInsecureRepositories": "false",
        "Acquire::AllowDowngradeToInsecureRepositories": "false",
        "APT::Get::AllowUnauthenticated": "false", "Acquire::Languages": "none",
    }
    configuration = work / "apt.conf"
    configuration.write_text("".join(key + " " + json.dumps(str(value)) + ";\n"
                                    for key, value in values.items()))
    return {"PATH": "/usr/bin:/bin", "HOME": str(work), "APT_CONFIG": str(configuration),
            "LC_ALL": "C", "LANG": "C"}, ubuntu


def provision(root, release_path, deadline, evidence=None):
    require(os.getuid() == os.geteuid() and os.geteuid() != 0,
            "acquire Xpra as the existing unprivileged service account")
    root = Path(x11.checked_path(Path(root).absolute()))
    release = json.loads(Path(release_path).read_text())
    check_release(release)
    os_release = dict(line.split("=", 1) for line in Path("/etc/os-release").read_text().splitlines()
                      if "=" in line and not line.startswith("#"))
    require(os_release.get("ID", "").strip('"') == "ubuntu"
            and os_release.get("VERSION_CODENAME", "").strip('"') == "jammy"
            and platform.machine() == "x86_64", "private Xpra qualifies Ubuntu Jammy amd64")
    selected = x11.load()
    require(selected is not None, "acquire the selected private X11 runtime first")
    owned_directory(root, create=True)
    lock_fd = os.open(root / "acquire.lock", os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    with os.fdopen(lock_fd, "a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        return _provision_locked(root, release, selected, deadline, evidence)


def _provision_locked(root, release, selected, deadline, evidence):
    work = Path(tempfile.mkdtemp(prefix=".prepare-", dir=root))
    try:
        key = work / "xpra.asc"
        download_key(key, deadline)
        (work / "gnupg").mkdir(mode=0o700)
        key_fingerprint(execute(["/usr/bin/gpg", "--no-options", "--batch",
                                 "--homedir", str(work / "gnupg"), "--with-colons",
                                 "--show-keys", str(key)], deadline,
                                env={"PATH": "/usr/bin:/bin", "HOME": str(work), "LC_ALL": "C"}))
        environment, ubuntu_key = apt_configuration(work, key)
        execute(["/usr/bin/apt-get", "update", "-o", "APT::Update::Error-Mode=any"],
                deadline, env=environment, output=work / "logs/update.log")
        wanted = [name + "=" + VERSION for name, _, _ in PINS]
        simulated = execute(["/usr/bin/apt-get", "--simulate", "--no-install-recommends",
                             "--no-remove", "install", *wanted, *DEPENDENCIES],
                            deadline, env=environment)
        (work / "logs/selection.txt").write_text(simulated)
        rows = set(simulation_packages(simulated))
        rows.update((name, VERSION) for name, _, _ in PINS)
        require(len(rows) <= 64, "dependency closure exceeds its bound")
        metadata = [package_record(execute(["/usr/bin/apt-cache", "show", name + "=" + version],
                                           deadline, env=environment), name, version)
                    for name, version in sorted(rows)]
        require(sum(item["bytes"] for item in metadata) <= MAX_DOWNLOAD,
                "package download closure exceeds its bound")
        indexes = {path.name: digest(path) for path in (work / "lists").iterdir()
                   if path.is_file() and path.name.endswith("_InRelease")}
        require(len(indexes) == 4 and "xpra.org_dists_jammy_InRelease" in indexes,
                "authenticated Ubuntu and Xpra archive indexes are incomplete")
        recipe = {"schema": SCHEMA, "os": "ubuntu-22.04", "architecture": "amd64",
                  "release": release, "packages": metadata, "apt_index_sha256": indexes,
                  "xpra_key_sha256": digest(key), "xpra_key_fingerprint": FINGERPRINT,
                  "ubuntu_keyring_sha256": digest(ubuntu_key),
                  "x11": {"selection_root": str(Path(selected["root"]).parents[2]),
                          "runtime_id": selected["runtime_id"],
                          "manifest_sha256": selected["manifest_sha256"]}}
        identity = hashlib.sha256(canonical(recipe)).hexdigest()
        generation = root / "generations" / identity
        selected_root = generation / "root"
        staging = create_staging_root(work)
        cache = owned_directory(root / "archives", create=True)
        budget = {"count": 0, "bytes": 0}
        for item in metadata:
            archive = cache / (item["sha256"] + ".deb")
            if not archive.exists() and not archive.is_symlink():
                destination = work / "downloads" / item["name"]
                destination.mkdir()
                execute(["/usr/bin/apt-get", "download", item["name"] + "=" + item["version"]],
                        deadline, env=environment, cwd=destination,
                        output=work / "logs" / (item["name"] + ".download.log"))
                candidates = list(destination.glob("*.deb"))
                require(len(candidates) == 1 and candidates[0].is_file()
                        and not candidates[0].is_symlink(), "APT did not return one regular package")
                require(candidates[0].stat().st_size == item["bytes"]
                        and digest(candidates[0]) == item["sha256"], "downloaded package bytes differ")
                candidates[0].replace(archive)
            require(archive.is_file() and not archive.is_symlink()
                    and archive.stat().st_uid == os.geteuid()
                    and archive.stat().st_size == item["bytes"]
                    and digest(archive) == item["sha256"], "cached package bytes differ")
            fields = execute(["/usr/bin/dpkg-deb", "--field", str(archive),
                              "Package", "Version", "Architecture"], deadline, env=environment)
            require(dict(line.split(": ", 1) for line in fields.strip().splitlines())
                    == {"Package": item["name"], "Version": item["version"],
                        "Architecture": item["architecture"]}, "package control identity differs")
            payload = work / "payload.tar"
            bounded_payload(archive, payload, deadline)
            extract_payload(payload, staging, budget)
            payload.unlink()
        inventory = file_inventory(staging)
        selected_root = promote_generation(staging, generation, inventory)
        document = {**recipe, "runtime_id": identity, "root": str(selected_root), "files": inventory,
                    "maintainer_scripts_executed": False, "host_packages_installed": False}
        qualify(document, deadline)
        document["manifest_sha256"] = hashlib.sha256(canonical(document)).hexdigest()
        retained = generation / "acquisition"
        owned_directory(retained, create=True)
        for name in ("apt.conf", "sources.list", "xpra.asc"):
            shutil.copy2(work / name, retained / name)
        x11.retain_logs(work, retained / "logs")
        for name in indexes:
            shutil.copy2(work / "lists" / name, retained / name)
        publish_selection(root, document)
        if evidence is not None:
            owned_directory(evidence, create=True)
            shutil.copy2(root / "current.json", evidence / "current.json")
            x11.retain_logs(work, evidence / "logs")
        return load(root)
    except (Exception, KeyboardInterrupt) as error:
        failure = owned_directory(root / "failures" / work.name.removeprefix(".prepare-"), create=True)
        x11.retain_logs(work, failure / "logs")
        (failure / "failure.json").write_text(json.dumps({
            "schema": SCHEMA, "status": "failed", "error_type": type(error).__name__,
            "error": str(error)[-2000:]}, sort_keys=True) + "\n")
        raise RuntimeError("private Xpra acquisition failed; diagnostic: " + str(failure)) from error
    finally:
        shutil.rmtree(work)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=runtime_root())
    parser.add_argument("--release", type=Path, required=True)
    parser.add_argument("--deadline-seconds", type=int, default=600)
    parser.add_argument("--evidence-output", type=Path)
    args = parser.parse_args()
    require(30 <= args.deadline_seconds <= 900, "acquisition deadline must be 30..900 seconds")
    previous = signal.getsignal(signal.SIGTERM)
    def interrupted(_signum, _frame):
        raise InterruptedError("Xpra acquisition interrupted")
    signal.signal(signal.SIGTERM, interrupted)
    try:
        document = provision(args.root, args.release, time.monotonic() + args.deadline_seconds,
                             args.evidence_output)
    finally:
        signal.signal(signal.SIGTERM, previous)
    print(json.dumps({"schema": SCHEMA, "runtime_id": document["runtime_id"],
                      "manifest_sha256": document["manifest_sha256"],
                      "manifest": str(args.root / "current.json"),
                      "host_packages_installed": False, "maintainer_scripts_executed": False},
                     sort_keys=True))
