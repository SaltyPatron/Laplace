#!/usr/bin/env python3
"""Render the monorepo's service envelopes from its machine declaration."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('machine', ROOT / 'scripts/machine-env.py')
machine = importlib.util.module_from_spec(spec)
spec.loader.exec_module(machine)


def quote(value):
    value = str(value)
    if any(ord(c) < 32 for c in value):
        raise ValueError('Control character in service configuration')
    return '"' + value.replace('\\', '\\\\').replace('"', '\\"').replace('%', '%%') + '"'


def directory(config, name, default):
    value = config.get(name) or default
    path = Path(value)
    if not path.is_absolute() or path == Path('/') or '..' in path.parts:
        raise ValueError('Expected an absolute installation directory: ' + name)
    quote(value)
    return str(path)


def identity(value):
    if not re.fullmatch(r'[a-z_][a-z0-9_-]*\$?', value):
        raise ValueError('Invalid service identity')
    return value


def scalar_path(value):
    # These directives consume one complete path, not a quoted word list.
    quote(value)
    return str(value).replace('%', '%%')


def render(config):
    prefix = directory(config, 'LAPLACE_INSTALL_PREFIX', '/opt/laplace')
    locks = directory(config, 'LAPLACE_LOCKS', '/run/lock/laplace')
    dotnet = config.get('LAPLACE_DOTNET') or '/usr/bin/dotnet'
    if not Path(dotnet).is_absolute():
        raise ValueError('LAPLACE_DOTNET must be an absolute executable path')
    runner = identity(config.get('LAPLACE_AGENT_USER') or 'laplace-runner')
    group = identity(config.get('LAPLACE_GROUP') or runner)
    database_service = config.get('LAPLACE_APP_PGSERVICE') or config.get('LAPLACE_PGSERVICE')
    dependencies = 'After=network.target\n'
    if database_service:
        if not re.fullmatch(r'[A-Za-z0-9_.@-]+', database_service):
            raise ValueError('Invalid declared PostgreSQL service')
        if not database_service.endswith('.service'):
            database_service += '.service'
        dependencies = f'After=network.target {database_service}\nWants={database_service}\n'
    units = {}
    for surface, assembly, subdir in (
        ('api', 'Laplace.Endpoints.OpenAICompat.dll', 'app'),
        ('mcp', 'Laplace.Endpoints.Mcp.dll', 'app/mcp-runtime'),
    ):
        key = surface.upper()
        user = identity(config.get(f'LAPLACE_{key}_USER') or runner)
        service_group = identity(config.get(f'LAPLACE_{key}_GROUP') or (group if user == runner else user))
        state = config.get(f'LAPLACE_{key}_STATE_DIR')
        temporary = directory(config, f'LAPLACE_{key}_TMPDIR', f'{state}/tmp' if state else f'{prefix}/tmp')
        executable = f'{prefix}/{subdir}/{assembly}'
        unit = (f'[Unit]\nDescription=Laplace monorepo {key}\n{dependencies}'
                f'ConditionPathExists={scalar_path(executable)}\n\n[Service]\nType=simple\n'
                f'User={user}\nGroup={service_group}\nUMask=0077\n'
                + ''.join('Environment=' + quote(name + '=' + temporary) + '\n' for name in ('TMPDIR', 'TMP', 'TEMP'))
                + 'Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1\n'
                + f'WorkingDirectory={scalar_path(prefix + "/" + subdir)}\n'
                + f'EnvironmentFile={scalar_path(prefix + "/app/laplace-" + surface + ".env")}\n')
        secrets = ('operator', 'lichess', 'stripe', 'identity') if surface == 'api' else ('mcp',)
        for secret in secrets:
            unit += 'EnvironmentFile=' + ('-' if surface == 'api' else '') + scalar_path(f'{prefix}/secrets/{secret}.env') + '\n'
        # ExecStart expands dollars separately from unit quoting.
        unit += ('ExecStart=' + quote(dotnet).replace('$', '$$') + ' ' + quote(executable).replace('$', '$$')
                 + (' --http' if surface == 'mcp' else '') + '\n'
                 'Restart=on-failure\nRestartSec=5\nKillSignal=SIGINT\nTimeoutStopSec=30\n'
                 f'SyslogIdentifier=laplace-{surface}\n\n[Install]\nWantedBy=multi-user.target\n')
        units[f'laplace-{surface}.service'] = unit
        units[f'laplace-{surface}-restart.path'] = (
            f'[Unit]\nDescription=Laplace monorepo {key} deployment restart request\n[Path]\n'
            f'PathChanged={scalar_path(locks + "/restart-" + surface)}\n[Install]\nWantedBy=multi-user.target\n')
        units[f'laplace-{surface}-restart.service'] = (
            f'[Unit]\nDescription=Restart Laplace monorepo {key}\n[Service]\nType=oneshot\n'
            f'ExecStart=/usr/bin/systemctl restart laplace-{surface}.service\n')
    return units


def verify(directory, names):
    result = subprocess.run(['systemd-analyze', 'verify', '--man=no',
                             *[str(directory / name) for name in names]],
                            capture_output=True, text=True, timeout=30)
    # systemd can return success while ignoring invalid directives. Its warnings
    # about these units are failures, independently of the process exit status.
    diagnostics = [line for line in result.stderr.splitlines() if any(name in line for name in names)]
    if result.returncode or diagnostics:
        raise ValueError('Generated service units were rejected: ' + '\n'.join(diagnostics or [result.stderr]))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, help='Write all generated units to this directory')
    parser.add_argument('--verify', action='store_true', help='Validate with the installed systemd parser before writing')
    args = parser.parse_args()
    config = {**machine.resolve(machine.machine_file(os.environ), os.environ,
                               explicit=bool(os.environ.get('LAPLACE_MACHINE_ENV'))), **os.environ}
    units = render(config)
    if args.verify:
        with tempfile.TemporaryDirectory(prefix='laplace-units-') as temporary:
            directory = Path(temporary)
            for name, content in units.items():
                (directory / name).write_text(content, encoding='utf-8')
            verify(directory, units)
    if args.output:
        args.output.mkdir(parents=True, exist_ok=True)
        for name, content in units.items():
            path = args.output / name
            temporary = path.with_suffix(path.suffix + '.new')
            temporary.write_text(content, encoding='utf-8')
            temporary.chmod(0o644)
            os.replace(temporary, path)
    print(json.dumps({'services': list(units), 'configuration': config.get('LAPLACE_CONFIG_SOURCE')}))


if __name__ == '__main__':
    main()
