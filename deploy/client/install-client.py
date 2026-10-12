#!/usr/bin/env python3
"""Install the Linux user command from a declared client profile, without sudo."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--configuration', type=Path, required=True)
    parser.add_argument('--destination', type=Path, required=True)
    parser.add_argument('--command-directory', type=Path, required=True)
    args = parser.parse_args()
    for path in (args.configuration, args.destination, args.command_directory):
        if not path.is_absolute():
            parser.error('Client setup paths must be absolute')
    config = json.loads(args.configuration.read_text())
    if config['version'] != 1 or config['default_profile'] not in config['profiles']:
        raise ValueError('Invalid client declaration/default profile')
    args.destination.mkdir(parents=True, exist_ok=True)
    args.command_directory.mkdir(parents=True, exist_ok=True)
    launcher = args.destination / 'laplace-mono.py'
    shutil.copy2(Path(__file__).with_name('laplace-mono.py'), launcher)
    launcher.chmod(0o755)
    (args.destination / 'client.json').write_text(json.dumps(config, indent=2) + '\n')
    command = args.command_directory / 'laplace-mono'
    if command.exists() or command.is_symlink():
        if not command.is_symlink() or command.resolve() != launcher.resolve():
            raise ValueError(f'Another command already owns {command}; preserved')
    else:
        command.symlink_to(launcher)
    subprocess.run([str(command), '--where'], check=True)
    subprocess.run([str(command), 'decompose', 'Laplace'], check=True)
    print(f'Installed laplace-mono: {command}')
    if str(args.command_directory) not in os.environ.get('PATH', '').split(os.pathsep):
        print(f'Command directory is not in this shell PATH: {args.command_directory}')


if __name__ == '__main__':
    main()
