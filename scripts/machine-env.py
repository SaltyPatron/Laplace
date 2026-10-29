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


def assignment_lexer(content):
    lexer = shlex.shlex(content, posix=True)
    lexer.whitespace = '\r\n'
    lexer.whitespace_split = True
    lexer.escape = ''  # Backslashes in paths are data.
    return lexer


def parse(content, process):
    values = canonical_process(process)
    # Emit translated process settings as well: consumers import only this output.
    declarations = {new: values[new] for old, new in LEGACY_DATABASE_KEYS.items() if process.get(old)}
    seen = set()
    configured = {}
    for item in assignment_lexer(content):
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
    return declarations


def resolve(path, process, explicit=False):
    try:
        content = path.read_text(encoding='utf-8-sig')
    except FileNotFoundError:
        if explicit:
            raise ValueError('LAPLACE_MACHINE_ENV does not name an existing file')
        return {**parse('', process), 'LAPLACE_CONFIG_SOURCE': 'defaults'}
    return {**parse(content, process), 'LAPLACE_CONFIG_SOURCE': str(path.resolve())}


def update_assignments(content, values):
    """Render a validated update, replacing aliases as one setting and retaining unrelated text."""
    parse(content, {})  # Contradictory existing declarations are never silently repaired.
    updates = {}
    for name, value in values.items():
        name, value = LEGACY_DATABASE_KEYS.get(name, name), str(value)
        if name in updates and updates[name] != value:
            raise ValueError('Conflicting configuration updates for: ' + name)
        updates[name] = value
    lines = iter(content.splitlines(keepends=True))
    result = []
    written = set()
    for line in lines:
        match = re.match(r'[ \t]*(?:export[ \t]+)?([A-Za-z_][A-Za-z0-9_]*)[ \t]*=', line)
        if not match:
            result.append(line)
            continue
        entry = line
        # Quoted multiline values belong to their assignment, including lines
        # which themselves happen to look like another setting.
        while True:
            try:
                list(assignment_lexer(entry))
                break
            except ValueError:
                entry += next(lines)
        name = match[1]
        canonical = LEGACY_DATABASE_KEYS.get(name, name)
        if canonical in written:
            continue
        if canonical in updates:
            result.append(canonical + '=' + shlex.quote(updates[canonical]) + '\n')
        else:
            result.append(entry[:match.start(1)] + canonical + entry[match.end(1):])
        written.add(canonical)
    text = ''.join(result)
    if text and not text.endswith('\n'):
        text += '\n'
    text += ''.join(key + '=' + shlex.quote(value) + '\n' for key, value in updates.items() if key not in written)
    parse(text, {})
    return text


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

    checkout_key = str(Path(checkout).resolve()).rstrip('\\/').lower() if os.name == 'nt' else str(Path(checkout).resolve())
    identity = hashlib.sha256(checkout_key.encode('utf-8') if os.name == 'nt' else os.fsencode(Path(checkout).resolve())).hexdigest()[:12 if os.name == 'nt' else 16]
    if os.name == 'nt':
        state = directory('LAPLACE_DATA_ROOT', str(Path(values['LOCALAPPDATA']) / 'Laplace') if values.get('LOCALAPPDATA') else None)
        directory('LAPLACE_DEPLOY', str(Path(state) / 'deploy'))
        pgdata = directory('LAPLACE_PGDATA', str(Path(state) / 'pgdata'))
        directory('LAPLACE_PGWAL', str(Path(pgdata) / 'pg_wal'))
    # Installed commands do not need a compiler workspace. Resolve the roots
    # declared for this host; consumers require only the directories they use.
    if values.get('LAPLACE_BUILD') or values.get('LAPLACE_BUILD_VOLUME') or values.get('LAPLACE_BUILD_ROOT'):
        build = directory('LAPLACE_BUILD', values.get('LAPLACE_BUILD_VOLUME') or values.get('LAPLACE_BUILD_ROOT'))
        default_build = Path(build) / 'laplace-mono' / 'worktrees' / identity
        if os.name == 'nt':
            canonical = str((Path(values['LAPLACE_SRC']) / 'Laplace').resolve()).rstrip('\\/').lower() if values.get('LAPLACE_SRC') else None
            default_build = Path(build) / 'Laplace' if checkout_key == canonical else Path(build) / 'worktrees' / (Path(checkout_key).name + '-' + identity)
        build_root = directory('LAPLACE_BUILD_ROOT', str(default_build))
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
