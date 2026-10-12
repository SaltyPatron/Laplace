#!/usr/bin/env python3
"""Read machine declarations as data; process values precede installation values."""
import argparse
import json
import os
from pathlib import Path
import re
import shlex
import hashlib


LEGACY_DATABASE_KEYS = {
    'LAPLACE_KNOWLEDGE_ROLE': 'LAPLACE_DATABASE_ROLE',
    'LAPLACE_KNOWLEDGE_RUNTIME_DB': 'LAPLACE_DATABASE_RUNTIME_DB',
    'LAPLACE_BAO_KNOWLEDGE_HOST': 'LAPLACE_BAO_DATABASE_HOST',
}


def canonical_process(process):
    values = dict(process)
    for old, new in LEGACY_DATABASE_KEYS.items():
        legacy = values.pop(old, '')
        if legacy:
            if values.get(new) and values[new] != legacy:
                raise ValueError(f'Conflicting configuration keys: {old} and {new}')
            values[new] = legacy
    return values


def machine_file(process):
    if process.get('LAPLACE_MACHINE_ENV'):
        return Path(process['LAPLACE_MACHINE_ENV'])
    return Path(process.get('PROGRAMDATA', 'C:/ProgramData')) / 'Laplace/machine.env' if os.name == 'nt' else Path('/etc/laplace/machine.env')


def resolve(path, process, explicit=False):
    values = canonical_process(process)
    # Emit translated process settings as well: consumers import only this output.
    declarations = {new: values[new] for old, new in LEGACY_DATABASE_KEYS.items() if process.get(old)}
    try:
        content = path.read_text(encoding='utf-8-sig')
    except FileNotFoundError:
        if explicit:
            raise ValueError('LAPLACE_MACHINE_ENV does not name an existing file')
        return {**declarations, 'LAPLACE_CONFIG_SOURCE': 'defaults'}
    lexer = shlex.shlex(content, posix=True)
    lexer.whitespace = '\r\n'
    lexer.whitespace_split = True
    lexer.escape = ''  # Backslashes in paths are data.
    seen = set()
    configured = {}
    for item in lexer:
        match = re.fullmatch(r'\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*', item, re.S)
        if not match:
            raise ValueError('Invalid machine configuration assignment')
        name, value = match.groups()
        if name.upper() in seen:
            raise ValueError('Duplicate machine configuration key: ' + name)
        seen.add(name.upper())
        name = LEGACY_DATABASE_KEYS.get(name, name)
        if '\0' in value:
            raise ValueError('NUL in machine configuration key: ' + name)
        if name in configured and configured[name] != value:
            raise ValueError('Conflicting configuration aliases for: ' + name)
        configured[name] = value
        if not values.get(name):
            def expand(reference):
                key = next(part for part in reference.groups() if part is not None)
                key = LEGACY_DATABASE_KEYS.get(key, key)
                if key not in values:
                    raise ValueError('Undefined configuration reference: ' + key)
                return values[key]
            value = re.sub(r'\$\{([A-Za-z_][A-Za-z0-9_]*)\}|\$([A-Za-z_][A-Za-z0-9_]*)|%([A-Za-z_][A-Za-z0-9_]*)%', expand, value)
            values[name] = value
            declarations[name] = value
    declarations['LAPLACE_CONFIG_SOURCE'] = str(path.resolve())
    return declarations


def paths(path, process, checkout):
    """Resolve operational directories from the host declaration, without mount names."""
    declarations = resolve(path, process, bool(process.get('LAPLACE_MACHINE_ENV')))
    values = dict(declarations)
    values.update({k: v for k, v in canonical_process(process).items() if v})
    result = dict(declarations)

    def directory(name, value):
        value = values.get(name) or value
        if not value or not Path(value).is_absolute() or Path(value) == Path(Path(value).anchor) or any(c in value for c in '\r\n\0'):
            raise ValueError(f'Declare an absolute directory for {name} in {path} or the process environment')
        result[name] = values[name] = str(Path(value))
        return values[name]

    identity = hashlib.sha256(os.fsencode(Path(checkout).resolve())).hexdigest()[:16]
    # Installed commands do not need a compiler workspace. Resolve the roots
    # declared for this host; consumers require only the directories they use.
    if values.get('LAPLACE_BUILD') or values.get('LAPLACE_BUILD_VOLUME') or values.get('LAPLACE_BUILD_ROOT'):
        build = directory('LAPLACE_BUILD', values.get('LAPLACE_BUILD_VOLUME') or values.get('LAPLACE_BUILD_ROOT'))
        build_root = directory('LAPLACE_BUILD_ROOT', str(Path(build) / 'laplace-mono' / 'worktrees' / identity))
        directory('LAPLACE_BUILD_DIRECTORY', str(Path(build_root) / 'build'))
        directory('LAPLACE_DEPS_BUILD', str(Path(build) / 'laplace-mono' / 'dependencies'))
    if values.get('LAPLACE_WORK') or values.get('LAPLACE_WORK_ROOT'):
        work = directory('LAPLACE_WORK', values.get('LAPLACE_WORK_ROOT'))
        work_root = directory('LAPLACE_WORK_ROOT', str(Path(work) / 'laplace-mono'))
        directory('LAPLACE_SCRATCH_ROOT', str(Path(work_root) / 'scratch'))
        directory('LAPLACE_LOCK_ROOT', values.get('LAPLACE_LOCKS') or str(Path(work_root) / 'locks'))
    elif values.get('LAPLACE_LOCK_ROOT') or values.get('LAPLACE_LOCKS'):
        directory('LAPLACE_LOCK_ROOT', values.get('LAPLACE_LOCKS'))
    if values.get('LAPLACE_EXTERNAL') or values.get('LAPLACE_DEPSRC'):
        directory('LAPLACE_EXTERNAL', values.get('LAPLACE_DEPSRC'))
    if values.get('LAPLACE_PG_PREFIX') or values.get('LAPLACE_PG_DIR'):
        directory('LAPLACE_PG_PREFIX', values.get('LAPLACE_PG_DIR'))
    return result


def database(path, process):
    result = resolve(path, process, bool(process.get('LAPLACE_MACHINE_ENV')))
    values = {**result, **{k: v for k, v in canonical_process(process).items() if v}}
    for name, value in {
        'PGHOST': values.get('LAPLACE_PGHOST', '/tmp'),
        'PGPORT': values.get('LAPLACE_PGPORT', '5432'),
        'PGUSER': values.get('LAPLACE_DATABASE_ROLE') or values.get('LAPLACE_ROLE'),
        'PGDATABASE': values.get('LAPLACE_DBNAME', 'laplace-mono'),
    }.items():
        result[name] = values.get(name) or value
        if not result[name]:
            raise ValueError(f'Declare {name} or its LAPLACE role/connection setting; no administrator identity is assumed')
    def connection(host, port, user, db, application=False):
        # Npgsql quoted values preserve semicolons and quotes in configured names.
        fields = {'Host': host, 'Port': port, 'Username': user, 'Database': db}
        sslmode = (values.get('LAPLACE_APP_DB_SSLMODE') if application else None) or values.get('LAPLACE_DB_SSLMODE') or values.get('PGSSLMODE')
        if sslmode:
            fields['SSL Mode'] = sslmode.replace('-', '')
        certificate = (values.get('LAPLACE_APP_DB_ROOT_CERTIFICATE') if application else None) or values.get('LAPLACE_DB_ROOT_CERTIFICATE') or values.get('PGSSLROOTCERT')
        if certificate:
            fields['Root Certificate'] = certificate
        return ';'.join(k + '="' + v.replace('"', '""') + '"' for k, v in fields.items())
    result['LAPLACE_DB'] = values.get('LAPLACE_DB') or connection(*(result[k] for k in ('PGHOST', 'PGPORT', 'PGUSER', 'PGDATABASE')))
    result['LAPLACE_QUERY_DB'] = values.get('LAPLACE_QUERY_DB') or result['PGDATABASE']
    if values.get('LAPLACE_APP_DBNAME'):
        result['LAPLACE_APP_DB'] = values.get('LAPLACE_APP_DB') or connection(
            values.get('LAPLACE_APP_PGHOST') or result['PGHOST'],
            values.get('LAPLACE_APP_PGPORT') or result['PGPORT'],
            values.get('LAPLACE_APP_ROLE') or values.get('LAPLACE_ROLE') or result['PGUSER'],
            values['LAPLACE_APP_DBNAME'], application=True)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--format', choices=('shell', 'json'), default='shell')
    parser.add_argument('--paths', action='store_true', help='Resolve configured build, work, scratch and dependency roots')
    parser.add_argument('--database', action='store_true', help='Resolve the declared monorepo database identity')
    parser.add_argument('--checkout', type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    path = machine_file(os.environ)
    result = paths(path, os.environ, args.checkout) if args.paths else resolve(path, os.environ, bool(os.environ.get('LAPLACE_MACHINE_ENV')))
    if args.database:
        result.update(database(path, os.environ))
    if args.format == 'json':
        print(json.dumps(result))
    else:
        # Only validated names and shell-quoted literal values cross this boundary.
        print('\n'.join('export ' + name + '=' + shlex.quote(value) for name, value in result.items()))


if __name__ == '__main__':
    main()
