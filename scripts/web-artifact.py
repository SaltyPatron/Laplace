#!/usr/bin/env python3
"""Build once from content inputs, then seal and verify the qualified web artifact."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import tempfile
from itertools import chain
from pathlib import Path

SCHEMA = 1


def sha256_file(path: Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(block)
    return value.hexdigest()


def dist_digest(dist: Path, *, ignore: frozenset[str] = frozenset()) -> str:
    if not dist.is_dir() or dist.is_symlink():
        raise ValueError(f"web dist directory is missing or not a real directory: {dist}")
    files = sorted(
        path for path in dist.rglob("*")
        if path.is_file() and path.relative_to(dist).as_posix() not in ignore
    )
    if not files:
        raise ValueError(f"web dist directory is empty: {dist}")
    value = hashlib.sha256()
    for path in files:
        if path.is_symlink():
            raise ValueError(f"web dist may not contain symlinks: {path}")
        relative = path.relative_to(dist).as_posix().encode("utf-8")
        value.update(len(relative).to_bytes(4, "big"))
        value.update(relative)
        value.update(bytes.fromhex(sha256_file(path)))
    return value.hexdigest()


def git_head(root: Path) -> str:
    return subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=root,
        check=True,
        text=True,
        capture_output=True,
    ).stdout.strip()


def document(root: Path) -> dict:
    dist = root / "web" / "dist"
    lock = root / "web" / "package-lock.json"
    openapi = root / "web" / "openapi" / "openapi.json"
    if not lock.is_file():
        raise ValueError("web/package-lock.json is missing")
    if not openapi.is_file():
        raise ValueError("web/openapi/openapi.json is missing")
    return {
        "schema": SCHEMA,
        "source_sha": git_head(root),
        "dist_sha256": dist_digest(dist),
        "package_lock_sha256": sha256_file(lock),
        "openapi_sha256": sha256_file(openapi),
    }


def write_atomic(path: Path, payload: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temp_name = tempfile.mkstemp(prefix=path.name + ".", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(payload, stream, sort_keys=True, indent=2)
            stream.write("\n")
        os.replace(temp_name, path)
    finally:
        try:
            os.unlink(temp_name)
        except FileNotFoundError:
            pass


def build_inputs(root: Path, tools: dict) -> str:
    """Content inputs of the SPA, including its generated API/architecture edges."""
    names = subprocess.run(['git', 'ls-files', '-z', '--cached', '--others', '--exclude-standard',
                            '--', 'web'], cwd=root, check=True, capture_output=True).stdout
    paths = {root / os.fsdecode(name) for name in names.split(b'\0') if name}
    paths.update(root / 'web' / name for name in
                 ('openapi/openapi.json', '.env', '.env.local', '.env.production', '.env.production.local'))
    # package-architecture reads working sources only for these evidence entries;
    # the full source-map is a static snapshot, not 3,000 live build dependencies.
    from documentation import documentation_root
    atlas = documentation_root(root) / 'docs/operation-atlas'
    paths.update(path for path in atlas.rglob('*')
                 if path.is_file() and '__pycache__' not in path.parts)
    evidence = []
    if (atlas / 'machine.json').is_file():
        for diagram in json.loads((atlas / 'machine.json').read_text(encoding='utf-8'))['diagrams'].values():
            evidence.extend(diagram.get('evidence', []))
    if (atlas / 'traces.json').is_file():
        for trace in json.loads((atlas / 'traces.json').read_text(encoding='utf-8')):
            evidence.extend(trace.get('evidence', []))
    for entry in evidence:
        if isinstance(entry, dict) and entry.get('sha256'):
            paths.add(root.parent / entry['path'])
    inputs = []
    for path in sorted(paths):
        relative = os.path.relpath(path, root).replace('\\', '/')
        if relative.startswith(('web/dist/', 'web/node_modules/', 'web/public/architecture/')) or relative == 'web/src/api/types.gen.ts':
            continue
        inputs.append((relative, sha256_file(path) if path.is_file() else None))
    environment = {key: value for key, value in os.environ.items()
                   if key.startswith('VITE_') or key in ('NODE_ENV', 'LAPLACE_API_URL')}
    value = {'files': inputs, 'tools': tools, 'environment': environment,
             'builder': sha256_file(Path(__file__))}
    return hashlib.sha256(json.dumps(value, sort_keys=True).encode()).hexdigest()


def build_web(root: Path, state: Path, *, force=False, skip_install=False) -> dict:
    web = root / 'web'
    dist = web / 'dist'
    for path in chain((dist,), dist.rglob('*')):
        if path.is_symlink() or (hasattr(path, 'is_junction') and path.is_junction()):
            raise ValueError(f'Web build output may not redirect outside its directory: {path}')
    npm = shutil.which('npm.cmd' if os.name == 'nt' else 'npm')
    node = shutil.which('node')
    if not npm or not node:
        raise ValueError('Web building requires the declared Node and npm toolchain')
    tools = {name: subprocess.run([program, '--version'], check=True, capture_output=True,
                                 text=True).stdout.strip() for name, program in (('node', node), ('npm', npm))}
    package = json.loads((web / 'package.json').read_text(encoding='utf-8'))
    dependencies = {key: package.get(key) for key in
                    ('dependencies', 'devDependencies', 'optionalDependencies', 'overrides', 'engines')}
    dependency_key = hashlib.sha256(json.dumps({'package': dependencies, 'tools': tools,
                    'lock': sha256_file(web / 'package-lock.json'),
                    'npmrc': [sha256_file(path) if path.is_file() else None
                              for path in (root / '.npmrc', web / '.npmrc')]}, sort_keys=True).encode()).hexdigest()
    dependency_state = state.with_name(state.stem + '-dependencies.json')
    hidden_lock = web / 'node_modules/.package-lock.json'
    try:
        installed = json.loads(dependency_state.read_text(encoding='utf-8'))
    except (OSError, ValueError):
        installed = {}
    current_dependencies = (installed.get('schema') == 1 and installed.get('inputs') == dependency_key and hidden_lock.is_file()
                            and installed.get('lock') == sha256_file(hidden_lock))
    restored = False
    if not skip_install and (force or not current_dependencies):
        subprocess.run([npm, 'ci', '--prefer-offline', '--no-audit', '--no-fund'], cwd=web, check=True)
        restored = True
    if not hidden_lock.is_file():
        raise ValueError('The installed npm dependency lock is missing; restore dependencies first')
    for program in ('typescript/bin/tsc', 'vite/bin/vite.js'):
        if not (web / 'node_modules' / program).is_file():
            raise ValueError('Required installed web builder is missing: ' + program)
    # An explicit --skip-install is not evidence that the installed tree matches
    # the requested lock. Only a restore can establish a new dependency receipt.
    if restored:
        write_atomic(dependency_state, {'schema': 1, 'inputs': dependency_key, 'lock': sha256_file(hidden_lock)})
    inputs = {**tools, 'dependencies': dependency_key, 'installed_lock': sha256_file(hidden_lock)}
    key = build_inputs(root, inputs)
    try:
        previous = json.loads(state.read_text(encoding='utf-8'))
        reuse = (not force and previous.get('inputs') == key
                 and previous.get('dist_sha256') == dist_digest(web / 'dist'))
    except (OSError, ValueError):
        reuse = False
    if reuse:
        return {'action': 'reused', 'dependencies_restored': restored, 'dist_sha256': previous['dist_sha256']}
    subprocess.run([npm, 'run', 'build'], cwd=web, check=True)
    if build_inputs(root, inputs) != key:
        raise ValueError('Web source inputs changed during the build; rerun to qualify the current inputs')
    payload = {'schema': 1, 'inputs': key,
               'dist_sha256': dist_digest(web / 'dist'), 'built_source_sha': git_head(root)}
    write_atomic(state, payload)
    return {'action': 'built', 'dependencies_restored': restored, **payload}


def verify(root: Path, manifest: Path) -> dict:
    try:
        expected = json.loads(manifest.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise ValueError(f"qualified web manifest is unreadable: {manifest}") from error
    current = document(root)
    if expected.get("schema") != SCHEMA:
        raise ValueError("qualified web manifest schema differs")
    for key in ("source_sha", "dist_sha256", "package_lock_sha256", "openapi_sha256"):
        if expected.get(key) != current[key]:
            raise ValueError(
                f"qualified web artifact differs for {key}: "
                f"expected {expected.get(key)!r}, current {current[key]!r}"
            )
    return current


def installed_verify(root: Path, manifest: Path, directory: Path) -> dict:
    expected = verify(root, manifest)
    directory = directory.resolve(strict=True)
    receipt = directory / ".laplace-web-source-revision"
    if receipt.is_symlink() or not receipt.is_file():
        raise ValueError("installed web artifact has no real source-revision receipt")
    source_sha = receipt.read_text(encoding="utf-8").strip()
    if source_sha != expected["source_sha"]:
        raise ValueError(
            f"installed web source differs: expected {expected['source_sha']}, found {source_sha}"
        )
    installed_digest = dist_digest(
        directory, ignore=frozenset({".laplace-web-source-revision"})
    )
    if installed_digest != expected["dist_sha256"]:
        raise ValueError(
            "installed web artifact differs: "
            f"expected {expected['dist_sha256']}, found {installed_digest}"
        )
    return {
        **expected,
        "installed_directory": str(directory),
        "installed_dist_sha256": installed_digest,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("operation", choices=("build", "seal", "verify", "verify-installed", "digest"))
    parser.add_argument("--root", default=".")
    parser.add_argument("--manifest")
    parser.add_argument("--directory")
    parser.add_argument('--force', action='store_true', help='Restore dependencies and rebuild the web artifact')
    parser.add_argument('--skip-install', action='store_true', help='Use an existing declared npm installation')
    args = parser.parse_args()

    root = Path(args.root).resolve()
    manifest = Path(args.manifest).resolve() if args.manifest else None
    directory = Path(args.directory).resolve() if args.directory else None
    try:
        if args.operation == 'build':
            if manifest is None or directory is not None:
                parser.error('build requires --manifest for its build receipt and no --directory')
            print(json.dumps(build_web(root, manifest, force=args.force, skip_install=args.skip_install)))
            return 0
        if args.operation == "digest":
            if directory is None or manifest is not None:
                parser.error("digest requires --directory and no --manifest")
            print(dist_digest(directory))
            return 0
        if manifest is None:
            parser.error(f"{args.operation} requires --manifest")
        if args.operation == "seal":
            if directory is not None:
                parser.error("seal does not accept --directory")
            payload = document(root)
            write_atomic(manifest, payload)
            print(
                f"WEB_ARTIFACT_SEALED source={payload['source_sha']} "
                f"dist={payload['dist_sha256']} manifest={manifest}"
            )
        elif args.operation == "verify-installed":
            if directory is None:
                parser.error("verify-installed requires --directory")
            payload = installed_verify(root, manifest, directory)
            print(
                f"WEB_ARTIFACT_INSTALLED source={payload['source_sha']} "
                f"dist={payload['installed_dist_sha256']} directory={directory}"
            )
        else:
            if directory is not None:
                parser.error("verify does not accept --directory")
            payload = verify(root, manifest)
            print(
                f"WEB_ARTIFACT_VERIFIED source={payload['source_sha']} "
                f"dist={payload['dist_sha256']}"
            )
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"web-artifact: {error}", file=__import__("sys").stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
