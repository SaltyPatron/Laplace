#!/usr/bin/env python3
"""Installed Linux monorepo command; configuration selects an authenticated API or operator database."""
import json
import os
from pathlib import Path
import sys
from urllib.parse import urlsplit


def absolute(value, name):
    path = Path(value)
    if not path.is_absolute():
        raise ValueError(f'Declare an absolute {name} path')
    return path


def main():
    config_path = absolute(os.environ.get('LAPLACE_MONO_CLIENT_CONFIG') or Path(__file__).resolve().with_name('client.json'), 'client configuration')
    config = json.loads(config_path.read_text(encoding='utf-8-sig'))
    if config['version'] not in (1, 2):
        raise ValueError('Unsupported monorepo client configuration version')
    args = sys.argv[1:]
    selection = os.environ.get('LAPLACE_MONO_PROFILE') or config['default_profile']
    if args[:1] == ['--profiles']:
        for name, profile in config['profiles'].items():
            if config['version'] == 2:
                print(f"{name}\t{profile['api']['origin']}\t{profile['runtime']['directory']}")
            else:
                print(f"{name}\t{profile['database']['host']}\t{profile['database']['database']}\t{profile['runtime']['directory']}")
        return 0
    if args[:1] == ['--profile']:
        if len(args) < 2:
            raise ValueError('Usage: laplace-mono --profile <name> <command> [arguments]')
        selection, args = args[1], args[2:]
    if selection not in config['profiles']:
        raise ValueError('Unknown profile. Run laplace-mono --profiles')
    profile = config['profiles'][selection]
    if profile['runtime']['kind'] != 'directory':
        raise ValueError('Linux runtime kind must be directory')
    executable = absolute(profile['runtime']['directory'], 'runtime directory') / 'Laplace.Cli'
    if not executable.is_file() or not os.access(executable, os.X_OK):
        raise ValueError(f'Installed monorepo CLI is missing or not executable: {executable}')
    remote = config['version'] == 2
    if remote:
        if 'database' in profile or 'credential' in profile:
            raise ValueError('API profiles must not include database credentials or targets')
        origin = profile['api']['origin']
        parsed = urlsplit(origin)
        if parsed.scheme != 'https' or not parsed.hostname or parsed.username or parsed.password or parsed.query or parsed.fragment or parsed.path not in ('', '/'):
            raise ValueError('API address must be an HTTPS origin')
        key_file = absolute(profile['api']['key_file'], 'API credential file')
    else:
        database = profile['database']
    if args[:1] == ['--where']:
        location = dict(implementation='laplace-mono', profile=selection, configuration=str(config_path), executable=str(executable))
        if remote:
            location['api_origin'] = origin
        else:
            location.update(database_host=database['host'], database_name=database['database'], database_role=database['role'])
        print(json.dumps(location, indent=2))
        return 0
    env = dict(os.environ)
    for name in ('LAPLACE_API_URL', 'LAPLACE_API_KEY_FILE', 'LAPLACE_DB', 'LAPLACE_APP_DB', 'PGPASSWORD', 'PGPASSFILE', 'LAPLACE_ENGINE_BUILD', 'LAPLACE_EXT_BUILD', 'LAPLACE_BUILD_ROOT', 'LAPLACE_PERFCACHE_BIN', 'LAPLACE_NATIVE_DIR'):
        env.pop(name, None)
    env.update(profile.get('environment', {}))
    if remote:
        for name in tuple(env):
            if name.upper().startswith('PG') or name.upper() in ('LAPLACE_DB', 'LAPLACE_APP_DB'):
                env.pop(name)
        env['LAPLACE_API_URL'] = origin
        env['LAPLACE_API_KEY_FILE'] = str(key_file)
    offline = not args or '--help' in args or '-h' in args or args[0] in ('decompose', 'cpu-topology')
    if not remote and not offline:
        if profile['credential']['kind'] != 'peer' or not database['host'].startswith('/'):
            raise ValueError('This Linux profile requires an installed local peer mapping; no service credential is borrowed')
        if not 0 < int(database['port']) < 65536 or int(database['pool_max']) < 1:
            raise ValueError('Invalid database port or connection budget')
        fields = dict(Host=database['host'], Port=database['port'], Database=database['database'], Username=database['role'])
        fields['Maximum Pool Size'] = database['pool_max']
        env['LAPLACE_DB'] = ';'.join(key + '="' + str(value).replace('"', '""') + '"' for key, value in fields.items())
    # Preserve the caller's working directory and arguments, including relative inputs.
    command_args = ['--api-client', *args] if remote else args
    os.execve(executable, [str(executable), *command_args], env)


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError) as error:
        print(f'laplace-mono: {error}', file=sys.stderr)
        sys.exit(1)
