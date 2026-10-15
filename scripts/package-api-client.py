#!/usr/bin/env python3
"""Package a self-contained published CLI and its authenticated user installers."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile
import zipfile


ROOT = Path(__file__).resolve().parents[1]


def package(published: Path, output: Path, platform: str, revision: str) -> Path:
    if not re.fullmatch(r'[0-9a-f]{40}', revision):
        raise ValueError('Select the exact 40-character source revision')
    executable = 'Laplace.Cli.exe' if platform == 'win-x64' else 'Laplace.Cli'
    core_runtime = 'coreclr.dll' if platform == 'win-x64' else 'libcoreclr.so'
    for name in (executable, core_runtime, 'Laplace.Cli.dll', 'Laplace.Cli.runtimeconfig.json'):
        if not (published / name).is_file():
            raise ValueError(f'Self-contained publication is missing {name}')
    name = f'laplace-api-client-{platform}-{revision[:12]}'
    suffix = '.zip' if platform == 'win-x64' else '.tar.gz'
    output.mkdir(parents=True, exist_ok=True)
    archive = output / (name + suffix)
    if archive.exists():
        raise FileExistsError(f'Package already exists: {archive}')
    with tempfile.TemporaryDirectory(prefix='client-package-', dir=output) as temporary:
        staging = Path(temporary)
        bundle = staging / name
        shutil.copytree(published, bundle / 'runtime', symlinks=False)
        client = bundle / 'deploy/client'
        client.mkdir(parents=True)
        files = ('Install-LaplaceApiClient.ps1', 'Install-LaplaceClient.ps1', 'laplace-mono.ps1') if platform == 'win-x64' else (
            'install-api-client.py', 'install-client.py', 'laplace-mono.py')
        for file in files:
            shutil.copy2(ROOT / 'deploy/client' / file, client / file)
        if platform == 'win-x64':
            windows = bundle / 'deploy/windows'
            windows.mkdir()
            shutil.copy2(ROOT / 'deploy/windows/database-connection.ps1', windows / 'database-connection.ps1')
        shutil.copy2(ROOT / 'deploy/client/README.txt', bundle / 'README.txt')
        manifest = {'format': 'laplace.api-client-package/v1', 'revision': revision,
                    'platform': platform, 'files': {
                        path.relative_to(bundle).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
                        for path in sorted(bundle.rglob('*')) if path.is_file()}}
        (bundle / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
        # The archive is completed in scratch before its final path becomes visible.
        staged_archive = staging / archive.name
        if platform == 'win-x64':
            with zipfile.ZipFile(staged_archive, 'w', compression=zipfile.ZIP_DEFLATED) as target:
                for path in sorted(bundle.rglob('*')):
                    if path.is_file():
                        target.write(path, path.relative_to(staging))
        else:
            with tarfile.open(staged_archive, 'w:gz') as target:
                target.add(bundle, arcname=name)
        staged_archive.rename(archive)
    return archive


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--published', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--platform', choices=('win-x64', 'linux-x64'), required=True)
    parser.add_argument('--revision', required=True)
    args = parser.parse_args()
    archive = package(args.published.resolve(), args.output.resolve(), args.platform, args.revision)
    # Execute the unpacked package, rather than the source publication.
    with tempfile.TemporaryDirectory(prefix='client-unpack-', dir=args.output) as temporary:
        root = Path(temporary)
        if args.platform == 'win-x64':
            with zipfile.ZipFile(archive) as source:
                source.extractall(root)
            executable = 'Laplace.Cli.exe'
        else:
            with tarfile.open(archive) as source:
                source.extractall(root, filter='data')
            executable = 'Laplace.Cli'
        runtime = next(root.iterdir()) / 'runtime' / executable
        result = subprocess.run([str(runtime), '--api-client', '--help'], cwd=root,
                                capture_output=True, text=True, timeout=30, check=True)
        if 'laplace-mono content export' not in result.stdout:
            raise ValueError('Packaged client did not expose its API commands')
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    archive.with_name(archive.name + '.sha256').write_text(f'{digest}  {archive.name}\n', encoding='utf-8')
    print(f'Packaged and executed {archive.name} sha256={digest}')


if __name__ == '__main__':
    main()
