#!/usr/bin/env python3
"""Select authenticated Ubuntu X11 tools in a private prefix without host installation."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import signal
import subprocess
import tarfile
import tempfile
import time

SCHEMA = "laplace.private-x11-runtime/v1"
DEFAULT_ROOT = Path("/opt/laplace/tools/chess/x11-runtime")
PACKAGES = ("xvfb", "xdotool")
HOST_TOOLS = ("xauth", "xprop", "xwininfo", "xkbcomp")
TOOLS = ("Xvfb", "xdotool", *HOST_TOOLS)
MAX_ARCHIVE = 64 * 1024 * 1024
MAX_EXPANDED = 256 * 1024 * 1024
HEX = re.compile(r"[0-9a-f]{64}")
PACKAGE = re.compile(r"[a-z0-9][a-z0-9+.-]*(?::amd64)?")
VERSION = re.compile(r"[A-Za-z0-9.+:~_-]+")
RESOURCES = ("/usr/bin/xkbcomp", "/usr/share/X11/xkb/rules/evdev",
             "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")


def require(value, message):
    if not value:
        raise RuntimeError(message)


def digest(path):
    with Path(path).open("rb") as stream:
        result = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode()


def runtime_root():
    return Path(os.environ.get("LAPLACE_X11_RUNTIME_ROOT", str(DEFAULT_ROOT))).absolute()


def checked_path(path):
    value = str(path)
    require(path.is_absolute() and not any(c in value for c in '"\r\n\\'),
            "runtime paths must be absolute and usable in native apt configuration")
    return value


def physical_directory(path, *, create=False):
    path = Path(path).absolute()
    require(not any(part.is_symlink() for part in (path, *path.parents)),
            "runtime directories may not traverse symlinks")
    if create:
        path.mkdir(parents=True, exist_ok=True)
    require(path.is_dir(), "runtime directory is absent")
    return path


def retain_logs(work, destination):
    destination.mkdir(parents=True, exist_ok=True)
    for path in (work / "logs").glob("*"):
        if path.is_file() and not path.is_symlink():
            with path.open("rb") as stream:
                size = path.stat().st_size
                stream.seek(max(0, size - 1024 * 1024))
                (destination / path.name).write_bytes(stream.read(1024 * 1024))


def run_owned(arguments, *, timeout, **options):
    child = subprocess.Popen(arguments, start_new_session=True, **options)
    try:
        stdout, stderr = child.communicate(timeout=timeout)
        return subprocess.CompletedProcess(arguments, child.returncode, stdout, stderr)
    finally:
        # apt and dpkg may own workers. Reap only the process group created here.
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
        for stream in (child.stdout, child.stderr):
            if stream is not None:
                stream.close()


def execute(arguments, deadline, *, env=None, cwd=None, output=None):
    remaining = deadline - time.monotonic()
    require(remaining > 0, "X11 provisioning exceeded its wall deadline")
    if output is not None:
        with Path(output).open("wb") as target:
            completed = run_owned(arguments, env=env, cwd=cwd, stdout=target,
                                       stderr=subprocess.STDOUT, timeout=remaining)
        require(completed.returncode == 0,
                "X11 provisioning command failed: " + Path(arguments[0]).name
                + "; retained log " + str(output))
        return ""
    completed = run_owned(arguments, env=env, cwd=cwd, stdout=subprocess.PIPE,
                          stderr=subprocess.PIPE, text=True, timeout=min(30, remaining))
    require(len(completed.stdout.encode()) <= 4 * 1024 * 1024,
            "X11 command response exceeds its bound")
    require(completed.returncode == 0,
            "X11 command failed: " + Path(arguments[0]).name + ": "
            + (completed.stdout + completed.stderr)[-2000:])
    return completed.stdout


def simulation_packages(output):
    require(not re.search(r"^Remv ", output, re.M), "runtime selection would remove host packages")
    rows = []
    for line in output.splitlines():
        if not line.startswith("Inst "):
            continue
        match = re.fullmatch(r"Inst (\S+) (?:\[[^]]+\] )?\((\S+) .+\)", line)
        require(match is not None, "unrecognized apt package selection")
        name, version = match.groups()
        require(PACKAGE.fullmatch(name) and VERSION.fullmatch(version),
                "invalid apt package identity")
        require(name.split(":")[0] not in ("libc6", "libc-bin", "libgcc-s1", "libstdc++6"),
                "X11 selection unexpectedly replaces a host compiler or C runtime")
        rows.append((name, version))
    require(0 < len(rows) <= 24 and len(set(rows)) == len(rows),
            "X11 dependency closure is empty, duplicated or exceeds its bound")
    return sorted(rows)


def package_metadata(text, name, version):
    documents = []
    for paragraph in text.strip().split("\n\n"):
        fields = {}
        for line in paragraph.splitlines():
            if line.startswith((" ", "\t")):
                continue
            key, separator, value = line.partition(": ")
            if separator:
                require(key not in fields, "duplicate apt metadata field")
                fields[key] = value
        if fields.get("Package") == name.split(":")[0] and fields.get("Version") == version:
            documents.append(fields)
    require(bool(documents), "selected apt package metadata is absent")
    values = {(d.get("Architecture"), d.get("SHA256"), d.get("Size")) for d in documents}
    require(len(values) == 1, "selected apt package metadata conflicts")
    arch, sha, size = values.pop()
    require(arch in ("amd64", "all") and isinstance(sha, str) and HEX.fullmatch(sha)
            and isinstance(size, str) and size.isdecimal() and 0 < int(size) <= MAX_ARCHIVE,
            "selected apt package metadata is invalid")
    return {"name": name.split(":")[0], "version": version, "architecture": arch,
            "sha256": sha, "bytes": int(size)}


def validate_tar(path):
    count = total = 0
    with tarfile.open(path, "r:") as archive:
        for item in archive:
            count += 1
            total += item.size
            name = PurePosixPath(item.name)
            require(not name.is_absolute() and ".." not in name.parts
                    and count <= 12000 and total <= MAX_EXPANDED
                    and (item.isdir() or item.isfile() or item.issym() or item.islnk()),
                    "package archive path, type or expansion differs")
            if item.issym() or item.islnk():
                target = PurePosixPath(item.linkname)
                require(not target.is_absolute(), "absolute package link is not relocatable")
                combined = name.parent / target if item.issym() else target
                depth = 0
                for part in combined.parts:
                    depth += -1 if part == ".." else (0 if part == "." else 1)
                    require(depth >= 0, "package link escapes its private root")
    return total


def file_inventory(root):
    result = {}
    total = 0
    for path in sorted(root.rglob("*")):
        relative = path.relative_to(root).as_posix()
        if path.is_symlink():
            require(path.resolve().is_relative_to(root.resolve()),
                    "extracted package symlink escapes its private root")
            result[relative] = {"symlink": os.readlink(path)}
        elif path.is_file():
            total += path.stat().st_size
            require(total <= MAX_EXPANDED, "runtime file inventory exceeds its byte bound")
            result[relative] = {"sha256": digest(path), "bytes": path.stat().st_size}
        else:
            require(path.is_dir(), "extracted runtime contains a special file")
    require(len(result) <= 12000, "runtime file inventory exceeds its bound")
    return result


def selected_environment(document, base=None, *, qt_prefix=None):
    result = dict(os.environ if base is None else base)
    root = Path(document["root"])
    paths = {"PATH": [root / "usr/bin"],
             "LD_LIBRARY_PATH": [root / "usr/lib/x86_64-linux-gnu", root / "lib/x86_64-linux-gnu"]}
    for name, prefixes in paths.items():
        values = [str(p) for p in prefixes if p.is_dir()]
        if qt_prefix is not None:
            values.insert(0, str(Path(qt_prefix) / ("bin" if name == "PATH" else "lib")))
        values.extend(filter(None, result.get(name, "").split(os.pathsep)))
        result[name] = os.pathsep.join(dict.fromkeys(values))
    return result


def loaded_files(environment, deadline):
    files = {}
    tool_paths = {}
    for name in TOOLS:
        path = shutil.which(name, path=environment.get("PATH"))
        require(path is not None, "selected runtime is missing " + name)
        path = str(Path(path).resolve(strict=True))
        tool_paths[name] = path
        files[path] = digest(path)
        output = execute(["/usr/bin/ldd", path], deadline, env=environment)
        require("not found" not in output, "selected X11 tool has an unresolved shared library")
        for line in output.splitlines():
            match = re.search(r"=> (/\S+)", line) or re.match(r"\s*(/\S+)", line)
            if match:
                library = str(Path(match[1]).resolve(strict=True))
                files[library] = digest(library)
    for resource in RESOURCES:
        require(Path(resource).is_file(), "required host XKB/font resource is absent: " + resource)
        files[str(Path(resource).resolve(strict=True))] = digest(resource)
    require(len(files) <= 256, "loaded X11 dependency inventory exceeds its bound")
    return tool_paths, files


def load(root=None):
    base = runtime_root() if root is None else Path(root).absolute()
    path = base / "current.json"
    if not path.exists():
        return None
    require(path.is_file() and not path.is_symlink() and path.stat().st_size <= 4 * 1024 * 1024,
            "runtime selection is not a bounded regular manifest")
    document = json.loads(path.read_text())
    identity = document.pop("manifest_sha256", None)
    require(document.get("schema") == SCHEMA and HEX.fullmatch(str(identity))
            and hashlib.sha256(canonical(document)).hexdigest() == identity,
            "runtime manifest identity differs")
    root_path = Path(document["root"])
    require(root_path == base / "generations" / document["runtime_id"] / "root"
            and HEX.fullmatch(document["runtime_id"]) and root_path.is_dir(),
            "runtime root differs from its selected generation")
    physical_directory(root_path)
    require(file_inventory(root_path) == document["files"], "selected runtime package bytes changed")
    for path, expected in document["loaded_files"].items():
        require(Path(path).is_file() and digest(path) == expected,
                "selected X11 executable/library/resource changed: " + path)
    document["manifest_sha256"] = identity
    return document


def provision(root, deadline, evidence=None):
    root = Path(checked_path(root)).absolute()
    release = {}
    for line in Path("/etc/os-release").read_text().splitlines():
        key, separator, value = line.partition("=")
        if separator:
            release[key] = value.strip('"')
    require(release.get("ID") == "ubuntu" and release.get("VERSION_CODENAME") == "jammy"
            and platform.machine() == "x86_64",
            "private X11 packages currently qualify Ubuntu 22.04 amd64")
    for name in HOST_TOOLS:
        require(shutil.which(name) is not None, "required installed X11 tool is absent: " + name)
    for resource in RESOURCES:
        require(Path(resource).is_file(), "required installed XKB/font resource is absent: " + resource)
    keyring = Path("/usr/share/keyrings/ubuntu-archive-keyring.gpg")
    require(keyring.is_file(), "Ubuntu archive trust keyring is absent")
    physical_directory(root, create=True)
    work = Path(tempfile.mkdtemp(prefix=".prepare-", dir=root))
    try:
        for relative in ("lists/partial", "cache/archives/partial", "empty", "logs", "downloads"):
            (work / relative).mkdir(parents=True, exist_ok=True)
        sources = work / "sources.list"
        sources.write_text("".join(
            "deb [arch=amd64 signed-by=" + str(keyring) + "] " + uri + " " + suite + " main universe\n"
            for uri, suite in (
                ("https://archive.ubuntu.com/ubuntu", "jammy"),
                ("https://archive.ubuntu.com/ubuntu", "jammy-updates"),
                ("https://security.ubuntu.com/ubuntu", "jammy-security"))))
        configuration = work / "apt.conf"
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
            "APT::Get::AllowUnauthenticated": "false",
        }
        configuration.write_text("".join(key + " " + json.dumps(str(value)) + ";\n"
                                        for key, value in values.items()))
        environment = {**os.environ, "APT_CONFIG": str(configuration), "LC_ALL": "C", "LANG": "C"}
        execute(["/usr/bin/apt-get", "update", "-o", "APT::Update::Error-Mode=any"],
                deadline, env=environment, output=work / "logs/update.log")
        simulated = execute(["/usr/bin/apt-get", "--simulate", "--no-install-recommends",
                             "--no-remove", "install", *PACKAGES], deadline, env=environment)
        (work / "logs/selection.txt").write_text(simulated)
        rows = simulation_packages(simulated)
        metadata = [package_metadata(execute(["/usr/bin/apt-cache", "show", name + "=" + version],
                                            deadline, env=environment), name, version)
                    for name, version in rows]
        require(sum(item["bytes"] for item in metadata) <= MAX_ARCHIVE,
                "X11 package download closure exceeds its bound")
        indexes = {path.name: digest(path) for path in (work / "lists").iterdir()
                   if path.is_file() and path.name.endswith("_InRelease")}
        require(len(indexes) == 3, "signed Ubuntu archive indexes are incomplete")
        recipe = {"schema": SCHEMA, "os": "ubuntu-22.04", "architecture": "amd64",
                  "packages": metadata, "archive_keyring_sha256": digest(keyring),
                  "apt_index_sha256": indexes}
        identity = hashlib.sha256(canonical(recipe)).hexdigest()
        generation = root / "generations" / identity
        selected_root = generation / "root"
        staging = work / "root"
        staging.mkdir()
        cache = root / "archives"
        physical_directory(cache, create=True)
        expanded = 0
        for item in metadata:
            archive = cache / (item["sha256"] + ".deb")
            if not archive.exists():
                destination = work / "downloads" / item["name"]
                destination.mkdir()
                execute(["/usr/bin/apt-get", "download", item["name"] + "=" + item["version"]],
                        deadline, env=environment, cwd=destination,
                        output=work / "logs" / (item["name"] + ".download.log"))
                candidates = list(destination.glob("*.deb"))
                require(len(candidates) == 1 and candidates[0].is_file()
                        and not candidates[0].is_symlink(), "apt did not return one regular package")
                require(candidates[0].stat().st_size == item["bytes"]
                        and digest(candidates[0]) == item["sha256"], "downloaded package hash or size differs")
                candidates[0].replace(archive)
            require(not archive.is_symlink() and archive.stat().st_size == item["bytes"]
                    and digest(archive) == item["sha256"], "cached package hash or size differs")
            fields = execute(["/usr/bin/dpkg-deb", "--field", str(archive),
                              "Package", "Version", "Architecture"], deadline)
            expected_fields = {key: value for key, value in
                               (("Package", item["name"]), ("Version", item["version"]),
                                ("Architecture", item["architecture"]))}
            require(dict(line.split(": ", 1) for line in fields.strip().splitlines()) == expected_fields,
                    "authenticated package control identity differs")
            tar_path = work / "payload.tar"
            with tar_path.open("wb") as output:
                completed = run_owned(["/usr/bin/dpkg-deb", "--fsys-tarfile", str(archive)],
                                           stdout=output, stderr=subprocess.PIPE,
                                           timeout=max(1, deadline - time.monotonic()))
            require(completed.returncode == 0 and tar_path.stat().st_size <= MAX_EXPANDED,
                    "package payload exceeds its extraction bound")
            expanded += validate_tar(tar_path)
            require(expanded <= MAX_EXPANDED, "package closure exceeds its extraction bound")
            execute(["/usr/bin/dpkg-deb", "--extract", str(archive), str(staging)], deadline)
        inventory = file_inventory(staging)
        physical_directory(generation, create=True)
        if selected_root.exists():
            physical_directory(selected_root)
            require(file_inventory(selected_root) == inventory, "existing runtime generation conflicts")
        else:
            staging.replace(selected_root)
        document = {**recipe, "runtime_id": identity, "root": str(selected_root), "files": inventory}
        tool_paths, libraries = loaded_files(selected_environment(document), deadline)
        document.update(tools=tool_paths, loaded_files=libraries,
                        sources=sources.read_text(),
                        host_xkb_directory="/usr/share/X11/xkb",
                        maintainer_scripts_executed=False, host_packages_installed=False)
        require(len(document["apt_index_sha256"]) == 3, "signed Ubuntu archive indexes are incomplete")
        document["manifest_sha256"] = hashlib.sha256(canonical(document)).hexdigest()
        retained = generation / "acquisition"
        if not retained.exists():
            retained.mkdir()
            shutil.copy2(configuration, retained / "apt.conf")
            shutil.copy2(sources, retained / "sources.list")
            retain_logs(work, retained / "logs")
            for path in (work / "lists").iterdir():
                if path.is_file() and path.name.endswith("_InRelease"):
                    shutil.copy2(path, retained / path.name)
        temporary = root / (".current-" + str(os.getpid()) + ".json")
        temporary.write_text(json.dumps(document, indent=2, sort_keys=True) + "\n")
        temporary.replace(root / "current.json")
        if evidence is not None:
            physical_directory(evidence, create=True)
            retain_logs(work, evidence / "logs")
            shutil.copy2(root / "current.json", evidence / "current.json")
            shutil.copy2(sources, evidence / "sources.list")
            for path in (work / "lists").iterdir():
                if path.is_file() and path.name.endswith("_InRelease"):
                    shutil.copy2(path, evidence / path.name)
        return load(root)
    except (Exception, KeyboardInterrupt) as error:
        failure = root / "failures" / work.name.removeprefix(".prepare-")
        physical_directory(failure, create=True)
        retain_logs(work, failure)
        (failure / "failure.json").write_text(json.dumps({
            "schema": SCHEMA, "status": "failed", "error_type": type(error).__name__,
            "error": str(error)[-2000:]}, sort_keys=True) + "\n")
        if evidence is not None:
            physical_directory(evidence, create=True)
            retain_logs(work, evidence / "logs")
            shutil.copy2(failure / "failure.json", evidence / "failure.json")
        raise RuntimeError("X11 runtime provisioning failed; retained diagnostic " + str(failure)) from error
    finally:
        shutil.rmtree(work)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=runtime_root())
    parser.add_argument("--deadline-seconds", type=int, default=280)
    parser.add_argument("--evidence-output", type=Path, default=os.environ.get("LAPLACE_X11_EVIDENCE"))
    args = parser.parse_args()
    require(30 <= args.deadline_seconds <= 300, "provisioning deadline must be 30..300 seconds")
    previous_term = signal.getsignal(signal.SIGTERM)
    def interrupted(_signum, _frame):
        raise InterruptedError("X11 provisioning interrupted")
    signal.signal(signal.SIGTERM, interrupted)
    try:
        value = provision(args.root, time.monotonic() + args.deadline_seconds, args.evidence_output)
    finally:
        signal.signal(signal.SIGTERM, previous_term)
    print(json.dumps({"schema": value["schema"], "runtime_id": value["runtime_id"],
                      "manifest": str(args.root / "current.json"),
                      "manifest_sha256": value["manifest_sha256"],
                      "packages": value["packages"], "tools": value["tools"],
                      "host_packages_installed": False}, sort_keys=True))


if __name__ == "__main__":
    main()
