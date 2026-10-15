#!/usr/bin/env python3
"""Install a downloaded API client using an existing private workspace key file."""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--origin', required=True)
    parser.add_argument('--key-file', type=Path, required=True)
    parser.add_argument('--destination', type=Path, required=True)
    parser.add_argument('--command-directory', type=Path, required=True)
    args = parser.parse_args()
    if not args.key_file.is_absolute() or not args.key_file.is_file():
        parser.error('Select an existing absolute API-key file path')
    runtime = Path(__file__).resolve().parents[2] / 'runtime'
    profile = {'version': 2, 'default_profile': 'service', 'profiles': {'service': {
        'runtime': {'kind': 'directory', 'directory': str(runtime)},
        'api': {'origin': args.origin, 'key_file': str(args.key_file)}}}}
    with tempfile.TemporaryDirectory(prefix='laplace-client-setup-') as temporary:
        configuration = Path(temporary) / 'client.json'
        configuration.write_text(json.dumps(profile), encoding='utf-8')
        return subprocess.run([
            sys.executable, str(Path(__file__).with_name('install-client.py')),
            '--configuration', str(configuration), '--destination', str(args.destination),
            '--command-directory', str(args.command_directory)], check=False).returncode


if __name__ == '__main__':
    raise SystemExit(main())
