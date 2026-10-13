#!/usr/bin/env python3
"""Run installed application checks with a temporary, revoked workload API key."""
import argparse
import importlib.util
from pathlib import Path
import subprocess


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--base', required=True)
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ['--'] else args.command
    if not command:
        parser.error('a verification command is required')
    spec = importlib.util.spec_from_file_location(
        'application_verification', Path(__file__).with_name('verify-application-release.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    with module.verification_credential(args.base.rstrip('/')):
        return subprocess.run(command, check=False).returncode


if __name__ == '__main__':
    raise SystemExit(main())
