#!/usr/bin/env python3
"""Install the Linux user command from a declared client profile, without sudo."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile


def operator_environment(profile):
    """Enroll native work paths from the same machine declaration as CI/setup."""
    if 'knowledge' in profile:
        if 'database' in profile and profile['database'] != profile['knowledge']:
            raise ValueError('Conflicting operator database declarations; preserved')
        profile['database'] = profile.pop('knowledge')
    if 'database' not in profile or profile.get('credential', {}).get('kind') != 'peer':
        raise ValueError('Operator enrollment requires a declared database and peer credential')
    environment = profile.setdefault('environment', {})
    source = Path(__file__).resolve().parents[2] / 'scripts/machine-env.py'
    if source.is_file():
        spec = importlib.util.spec_from_file_location('client_machine', source)
        machine = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(machine)
        declaration = os.environ.get('LAPLACE_MACHINE_ENV') or '/etc/laplace/machine.env'
        overrides = dict(environment)
        if os.environ.get('LAPLACE_MACHINE_ENV'):
            overrides['LAPLACE_MACHINE_ENV'] = declaration
        paths = machine.paths(Path(declaration), overrides, Path(profile['runtime']['directory']))
        for key in ('LAPLACE_WORK', 'LAPLACE_WORK_ROOT'):
            if paths.get(key): environment.setdefault(key, paths[key])
    work = environment.get('LAPLACE_WORK_ROOT') or environment.get('LAPLACE_WORK')
    if not work or not Path(work).is_absolute():
        raise ValueError('The operator profile needs a declared absolute LAPLACE_WORK_ROOT')
    environment.setdefault('LAPLACE_WORK_ROOT', work)
    Path(work).mkdir(parents=True, exist_ok=True)


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
    if config['version'] not in (1, 2) or config['default_profile'] not in config['profiles']:
        raise ValueError('Invalid client declaration/default profile')
    launcher = args.destination / 'laplace-mono.py'
    command = args.command_directory / 'laplace-mono'
    if command.exists() or command.is_symlink():
        if not command.is_symlink() or command.resolve() != launcher.resolve():
            raise ValueError(f'Another command already owns {command}; preserved')
    if config['version'] == 1:
        for profile in config['profiles'].values():
            operator_environment(profile)
    source_launcher = Path(__file__).with_name('laplace-mono.py')
    check = ['ops'] if config['version'] == 2 else ['decompose', 'Laplace']
    verify_env = dict(os.environ, LAPLACE_MONO_CLIENT_CONFIG=str(args.configuration))
    if config['version'] == 2:
        subprocess.run([sys.executable, str(source_launcher), '--profile', config['default_profile'], *check],
                       env=verify_env, check=True)
    else:
        # Check the upgraded declaration with the selected runtime before replacing
        # either installed file. The scoped preflight declaration is always removed.
        with tempfile.TemporaryDirectory(prefix='laplace-client-', dir=args.configuration.parent) as preflight:
            selected = Path(preflight) / 'client.json'
            selected.write_text(json.dumps(config))
            operator_env = dict(os.environ, LAPLACE_MONO_CLIENT_CONFIG=str(selected))
            for operation in (['--where'], check):
                subprocess.run([sys.executable, str(source_launcher), '--profile', config['default_profile'], *operation],
                               env=operator_env, check=True)
    args.destination.mkdir(parents=True, exist_ok=True)
    args.command_directory.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source_launcher, launcher)
    launcher.chmod(0o755)
    (args.destination / 'client.json').write_text(json.dumps(config, indent=2) + '\n')
    if not command.is_symlink():
        command.symlink_to(launcher)
    installed_env = dict(os.environ, LAPLACE_MONO_CLIENT_CONFIG=str(args.destination / 'client.json'))
    selected_command = [str(command), '--profile', config['default_profile']]
    subprocess.run([*selected_command, '--where'], env=installed_env, check=True)
    subprocess.run([*selected_command, *check], env=installed_env, check=True)
    print(f'Installed laplace-mono: {command}')
    if str(args.command_directory) not in os.environ.get('PATH', '').split(os.pathsep):
        print(f'Command directory is not in this shell PATH: {args.command_directory}')


if __name__ == '__main__':
    main()
