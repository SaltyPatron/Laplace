#!/usr/bin/env python3
"""Install the declared API/MCP identities; preserve data and roll back failed activation."""
import argparse
from datetime import datetime, timezone
import fcntl
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import pwd
import re
import secrets
import shlex
import socket
import stat
import subprocess
import sys
import urllib.error
import urllib.request
import uuid

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


SYNC = module('runtime_sync', HERE / 'database-sync.py')
TLS = module('runtime_tls', HERE / 'runtime_tls.py')
MACHINE = module('runtime_machine', ROOT / 'scripts/machine-env.py')


class CommandFailure(RuntimeError):
    def __init__(self, command, result):
        super().__init__(f'{Path(command).name} failed (exit {result.returncode})')
        self.diagnostics = result.stdout + result.stderr


def checked(argv, **kwargs):
    result = subprocess.run(argv, capture_output=True, text=True, **kwargs)
    if result.returncode:
        raise CommandFailure(argv[0], result)
    return result.stdout.strip()


def declarations(path):
    return MACHINE.resolve(path, {}, explicit=True)


def validate(config):
    TLS.validate(config['tls'])
    for key in ('host', 'operator', 'record', 'database', 'role', 'database_owner', 'runtime_user', 'pg_os_user', 'bao_role'):
        if not re.fullmatch(r'[a-z][a-z0-9_-]*', config[key]):
            raise ValueError('Invalid installation identity: ' + key)
    if not re.fullmatch(r'[a-z][a-z0-9_-]*', config['mcp']['user']):
        raise ValueError('Invalid MCP OS identity')
    identities = [config['role'], config['substrate']['role'], config['mcp']['role'], config['database_owner']]
    if len(set(identities)) != len(identities):
        raise ValueError('Application, substrate, MCP and migration roles must be distinct')
    if config['runtime_user'] in ('root', config['pg_os_user'], config['operator'], config['mcp']['user']):
        raise ValueError('API identity must be independent of administration and MCP')
    if config['mcp']['user'] in ('root', config['pg_os_user'], config['operator']):
        raise ValueError('MCP identity must be independent of administration')
    for item in (config, config['substrate'], config['mcp']):
        if not re.fullmatch(r'[a-z][a-z0-9_-]*', item['role']):
            raise ValueError('Invalid runtime database role')
        if not 1 <= int(item['pool_max']) <= int(item['connection_limit']):
            raise ValueError('Runtime pool exceeds its role connection allowance')
    for key in ('pgbin', 'pgsocket', 'database_data', 'prefix', 'machine_file', 'host_lock', 'credentials', 'work'):
        path = Path(config[key])
        if not path.is_absolute() or path == Path('/') or '..' in path.parts or path.is_symlink():
            raise ValueError('Invalid installation path: ' + key)
    prefix = Path(config['prefix'])
    states = [Path(config['state']), Path(config['mcp']['state'])]
    for state in states:
        if not state.is_absolute() or state == Path('/') or '..' in state.parts or state.is_symlink():
            raise ValueError('Invalid runtime state directory')
        if state == prefix or state in prefix.parents or prefix in state.parents:
            raise ValueError('Runtime state and deployed binaries must be independent')
    if states[0] == states[1] or states[0] in states[1].parents or states[1] in states[0].parents:
        raise ValueError('API and MCP state must be independent')
    if not config['bao_address'].startswith('https://'):
        raise ValueError('OpenBao requires HTTPS')
    for base in (config['public_base'], config['mcp']['base']):
        if not base.startswith(('http://127.0.0.1:', 'https://')):
            raise ValueError('Verification requires local HTTP or HTTPS')
    if not 1 <= int(config['port']) <= 65535:
        raise ValueError('Invalid database port')
    # This installation pins existing application data. It is not a database migration.
    if config['substrate']['database'] != config['database']:
        raise ValueError('This setup requires the current co-located application and substrate database')
    for item in (config, config['substrate']):
        if item['database_host'] != '127.0.0.1' or int(item['port']) != int(config['port']):
            raise ValueError('Runtime installation requires the declared local cluster')


def validate_declaration(config):
    validate(config)
    if socket.gethostname().split('.')[0].lower() != config['host']:
        raise ValueError('The runtime declaration belongs to another host')
    current = dict(declarations(Path(config['machine_file'])))
    current.update({key: value for key, value in os.environ.items() if key.startswith('LAPLACE_') and value})
    for key, expected in {'LAPLACE_INSTALL_PREFIX': config['prefix'], 'LAPLACE_PGDATA': config['database_data'],
                          'LAPLACE_PG_DIR': str(Path(config['pgbin']).parent),
                          'LAPLACE_ROLE': config['database_owner']}.items():
        if current.get(key) != expected:
            raise ValueError('Installed configuration differs from runtime declaration: ' + key)
    # A database consumer name does not declare another PostgreSQL cluster.
    # Refuse contradictory topology before the base installer changes anything.
    for key, expected in {'LAPLACE_APP_DBNAME': config['database'],
                          'LAPLACE_APP_PGPORT': str(config['port']),
                          'LAPLACE_APP_PGDATA': config['database_data'],
                          'LAPLACE_PGPORT': str(config['port'])}.items():
        if current.get(key) and current[key] != expected:
            raise ValueError('Application database differs from runtime declaration: ' + key)
    if current.get('LAPLACE_APP_PGHOST') not in (None, '', config['pgsocket'], config['database_host'], 'localhost'):
        raise ValueError('Application database host differs from runtime declaration')


def selected_manifest(explicit=None):
    requested = explicit or os.environ.get('LAPLACE_RUNTIME_MANIFEST')
    if not requested:
        machine = module('runtime_selection', ROOT / 'scripts/machine-env.py')
        machine_path = Path(os.environ.get('LAPLACE_MACHINE_ENV') or '/etc/laplace/machine.env')
        current = machine.resolve(machine_path, os.environ, explicit=bool(os.environ.get('LAPLACE_MACHINE_ENV')))
        requested = current.get('LAPLACE_RUNTIME_MANIFEST')
    if requested:
        path = Path(requested)
        if not path.is_absolute():
            raise ValueError('LAPLACE_RUNTIME_MANIFEST / --manifest must be an absolute path')
        if not path.is_file():
            raise ValueError('Missing declared runtime manifest: ' + str(path))
        return path
    path = Path(__file__).with_name(socket.gethostname().split('.')[0].lower() + '-runtime.json')
    return path if path.is_file() else None


def storage_preflight(config):
    # Budget for simultaneous old/new helper and configuration files, using the
    # actual installer payload rather than a percentage of an unrelated disk.
    payloads = list(HERE.glob('*.py'))
    required_bytes = 2 * (sum(path.stat().st_size for path in payloads) + len(json.dumps(config).encode()))
    paths = [Path(config[key]) for key in ('work', 'prefix', 'state')]
    paths += [Path(config['mcp']['state']), Path(config['credentials']).parent,
              Path(config['tls']['root_certificate']).parent, Path('/etc/systemd/system')]
    result = []
    for path in paths:
        parent = path
        while not parent.exists():
            parent = parent.parent
        status = os.statvfs(parent)
        free = status.f_bavail * status.f_frsize
        if free < required_bytes or status.f_favail < len(paths) + len(payloads):
            raise ValueError(f'Insufficient installation space at {path} (filesystem containing {parent}): '
                             f'{free} bytes and {status.f_favail} inodes available; '
                             f'{required_bytes} bytes required for installation files. No service configuration changed.')
        result.append({'path': str(path), 'free_bytes': free, 'free_inodes': status.f_favail,
                       'installation_bytes': required_bytes})
    return result


def preflight(config):
    validate_declaration(config)
    MACHINE.update_assignments(Path(config['machine_file']).read_text(), machine_settings(config))
    storage = storage_preflight(config)
    prefix = Path(config['prefix'])
    for path in (prefix / 'app/laplace-api.env', prefix / 'app/laplace-mcp.env',
                 prefix / 'app/operations/scripts/verify-application-release.py',
                 prefix / 'app/.laplace-source-revision', Path(config['pgbin']) / 'psql'):
        if not path.is_file():
            raise ValueError('Missing installed prerequisite: ' + str(path))
    checked(['systemctl', 'is-active', '--quiet', config['database_service']], timeout=10)
    for service in ('laplace-api', 'laplace-mcp'):
        if checked(['systemctl', 'show', service, '-p', 'LoadState', '--value'], timeout=10) != 'loaded':
            raise ValueError('Missing installed service: ' + service)
    for path in config.get('required_mounts', []):
        checked(['mountpoint', '-q', path], timeout=10)
    for path in config.get('required_directories', []):
        if not Path(path).is_dir():
            raise ValueError('Missing declared storage directory: ' + str(path))
    for name, state in ((config['runtime_user'], config['state']), (config['mcp']['user'], config['mcp']['state'])):
        try:
            account = pwd.getpwnam(name)
        except KeyError:
            continue
        if account.pw_uid == 0 or account.pw_dir != state:
            raise ValueError('Existing runtime account has a different home: ' + name)
    trust = Path(config['tls']['root_certificate'])
    tls_status = 'installation_required'
    if trust.is_file():
        try:
            TLS.verify_endpoint(config['database_host'], config['port'], trust)
            tls_status = 'verified'
        except (OSError, ValueError):
            tls_status = 'repair_required'
    return {'host': config['host'], 'release': (prefix / 'app/.laplace-source-revision').read_text().strip(),
            'application_database': config['database'], 'application_data_moved': False,
            'configuration_update': 'verified',
            'database_tls': {'status': tls_status, 'root_certificate': str(trust)}, 'storage': storage}


class Bao:
    def __init__(self, config):
        self.env = {k: v for k, v in os.environ.items() if k not in ('BAO_TOKEN', 'VAULT_TOKEN', 'BAO_NAMESPACE', 'VAULT_NAMESPACE')}
        self.env['BAO_ADDR'] = config['bao_address']
        self.command = ['bao']
        if os.geteuid() == 0:
            self.command = ['runuser', '-u', config['operator'], '--', 'bao']
        elif pwd.getpwuid(os.geteuid()).pw_name != config['operator']:
            raise ValueError('Use the declared installation operator')

    def call(self, args, payload=None, missing=False):
        result = subprocess.run(self.command + args, env=self.env, input=payload,
                                capture_output=True, text=True, timeout=30)
        if result.returncode:
            if missing and 'No value found at ' in result.stderr:
                return None
            raise ValueError('OpenBao initial enrollment failed during ' + ' '.join(args[:2]) +
                             '; check the declared operator login and enrollment policy')
        return result.stdout

    def record(self, path, selectors):
        prior = self.call(['kv', 'get', '-format=json', '-mount=kv', path], missing=True)
        if prior is not None:
            record = json.loads(prior)['data']['data']
            return validate_record(record, selectors, path)
        record = dict(selectors, password=secrets.token_hex(32))
        self.call(['kv', 'put', '-mount=kv', path, '-'], json.dumps(record))
        return record

    def enroll(self, config):
        name = config['bao_role']
        paths = [f'kv/data/hosts/{config["host"]}/{record}' for record in ('substrate', config['record'])]
        policy = ''.join(f'path "{path}" {{ capabilities = ["read"] }}\n' for path in paths)
        policy += ('path "sys/capabilities-self" { capabilities = ["update"] }\n'
                   'path "auth/token/lookup-self" { capabilities = ["read"] }\n'
                   'path "auth/token/revoke-self" { capabilities = ["update"] }\n')
        self.call(['policy', 'write', name, '-'], policy)
        self.call(['write', '-format=json', 'auth/approle/role/' + name, '-'], json.dumps({
            'token_policies': [name], 'token_no_default_policy': True, 'token_ttl': '5m',
            'token_max_ttl': '10m', 'secret_id_ttl': 0, 'secret_id_num_uses': 0, 'bind_secret_id': True}))
        role_id = json.loads(self.call(['read', '-format=json', f'auth/approle/role/{name}/role-id']))['data']['role_id']
        credential = Path(config['credentials'])
        if credential.exists():
            if credential.is_symlink() or credential.stat().st_uid != 0 or credential.stat().st_mode & 0o077:
                raise ValueError('Existing workload enrollment must be root-owned and private')
            if json.loads(credential.read_text())['role_id'] != role_id:
                raise ValueError('Existing workload enrollment belongs to another role')
            return
        secret_id = json.loads(self.call(['write', '-format=json', '-f', f'auth/approle/role/{name}/secret-id']))['data']['secret_id']
        SYNC.atomic(credential, json.dumps({'role_id': role_id, 'secret_id': secret_id}) + '\n', 0o600, 0, 0)


def validate_record(record, selectors, path):
    if any(str(record.get(k)) != str(v) for k, v in selectors.items()):
        raise ValueError('Existing OpenBao record selects a different installation: ' + path)
    if not re.fullmatch(r'[0-9a-f]{64}', record.get('password', '')):
        raise ValueError('Existing runtime password is not a generated credential')
    return record


def enrollment(path):
    path = Path(path)
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o077:
        raise ValueError('Workload enrollment must be a root-owned private regular file')
    value = json.loads(path.read_text())
    if set(value) != {'role_id', 'secret_id'} or not all(isinstance(v, str) and v for v in value.values()):
        raise ValueError('Invalid installed AppRole enrollment')
    return value


def runtime_records(config):
    """Bootstrap once; subsequent repairs read through the installed workload identity."""
    selections = [(name, {key: item[key] for key in ('database_host', 'database', 'role', 'port')})
                  for name, item in ((config['record'], config), ('substrate', config['substrate']))]
    if not os.path.lexists(config['credentials']):
        operator = Bao(config)
        operator.call(['token', 'lookup', '-field=ttl'])
        for name, selectors in selections:
            operator.record(f'hosts/{config["host"]}/{name}', selectors)
        operator.enroll(config)
    # Existing invalid enrollment fails here. It never falls back to an operator
    # token, rewrites policy, rotates credentials, or retrieves another password.
    credentials = enrollment(config['credentials'])
    token = None

    def request(path, payload=None):
        headers = {'Content-Type': 'application/json'}
        if token:
            headers['X-Vault-Token'] = token
        data = json.dumps(payload).encode() if payload is not None else None
        try:
            with urllib.request.urlopen(urllib.request.Request(config['bao_address'].rstrip('/') + '/v1/' + path,
                    data=data, headers=headers), timeout=20) as response:
                body = response.read(1024 * 1024)
                return json.loads(body) if body else None
        except urllib.error.HTTPError as error:
            raise ValueError(f'OpenBao workload operation {path} failed (HTTP {error.code}); '
                             'installed AppRole authentication and record access are required') from None
        except urllib.error.URLError:
            raise ValueError('OpenBao workload endpoint could not be reached or its TLS identity verified') from None

    try:
        auth = request('auth/approle/login', credentials)['auth']
        token = auth['client_token']
        if auth.get('metadata', {}).get('role_name') != config['bao_role']:
            raise ValueError('Installed AppRole belongs to another runtime identity')
        records = {}
        for name, selectors in selections:
            path = f'kv/data/hosts/{config["host"]}/{name}'
            record = request(path)['data']['data']
            records[name] = validate_record(record, selectors, path)
            if set(request('sys/capabilities-self', {'paths': [path]})['capabilities']) != {'read'}:
                raise ValueError('Workload credential access must remain read-only')
        return records
    finally:
        if token:
            request('auth/token/revoke-self', {})


def connection(item, peer=False, socket_path=None):
    tls = ('SSL Mode=VerifyFull;Root Certificate=' + item['root_certificate'] + ';'
           if item.get('root_certificate') else 'SSL Mode=Require;')
    return (f'Host={socket_path if peer else item["database_host"]};Port={int(item["port"])};'
            f'Database={item["database"]};Username={item["role"]};'
            + ('' if peer else tls) + f'Maximum Pool Size={int(item["pool_max"])}')


def mcp_verify(config):
    prefix = Path(config['prefix'])
    token = declarations(prefix / 'secrets/mcp.env')['LAPLACE_MCP_TOKEN']
    payload = json.dumps({'jsonrpc': '2.0', 'id': 1, 'method': 'initialize', 'params': {
        'protocolVersion': '2025-06-18', 'capabilities': {}, 'clientInfo': {'name': 'runtime-setup', 'version': '1'}}}).encode()
    session = None
    for authenticated, expected in ((False, 401), (True, 200)):
        headers = {'Content-Type': 'application/json', 'Accept': 'application/json, text/event-stream'}
        if authenticated:
            headers['Authorization'] = 'Bearer ' + token
        try:
            with urllib.request.urlopen(urllib.request.Request(config['mcp']['base'].rstrip('/') + '/mcp', data=payload, headers=headers), timeout=15) as response:
                status, body = response.status, response.read(1048576).decode()
                session = response.headers.get('Mcp-Session-Id')
        except urllib.error.HTTPError as error:
            status, body = error.code, ''
        if status != expected or (authenticated and ('protocolVersion' not in body or 'serverInfo' not in body)):
            raise ValueError('Installed MCP authentication/initialization failed')
    headers['MCP-Protocol-Version'] = '2025-06-18'
    if session:
        headers['Mcp-Session-Id'] = session
    for message in ({'jsonrpc': '2.0', 'method': 'notifications/initialized'},
                    {'jsonrpc': '2.0', 'id': 2, 'method': 'tools/call',
                     'params': {'name': 'api', 'arguments': {'query': 'substrate_counts'}}}):
        with urllib.request.urlopen(urllib.request.Request(config['mcp']['base'].rstrip('/') + '/mcp',
                data=json.dumps(message).encode(), headers=headers), timeout=30) as response:
            body = response.read(1048576).decode()
        if 'id' in message:
            if body.lstrip().startswith('{'):
                reply = json.loads(body)
            else:
                reply = next((json.loads(line[5:].strip()) for line in body.splitlines()
                              if line.startswith('data:') and line[5:].strip().startswith('{')), {})
            if reply.get('error') or reply.get('result', {}).get('isError') is not False:
                raise ValueError('MCP substrate_counts tool failed')
    return {'anonymous': 'denied', 'authenticated_initialize': 'passed', 'substrate_counts_tool': 'passed'}


def install(config, lock):
    print('Authenticating the installed runtime AppRole (operator login is used only for initial enrollment)...', flush=True)
    records = runtime_records(config)
    for name, item in ((config['record'], config), ('substrate', config['substrate'])):
        item['password'] = records[name]['password']

    def sql(statement):
        return checked(['runuser', '-u', config['pg_os_user'], '--', config['pgbin'] + '/psql',
            '-XwAt', '-v', 'ON_ERROR_STOP=1', '-h', config['pgsocket'], '-p', str(config['port']),
            '-d', config['database'], '-f', '-'], input=statement, timeout=30)
    if Path(sql('SHOW data_directory')).resolve() != Path(config['database_data']).resolve():
        raise ValueError('Connected cluster differs from runtime declaration')
    print('Verifying the database certificate and runtime trust before activation...', flush=True)
    with TLS.configured(config, sql, SYNC) as proof:
        config['_tls_verification'] = proof
    # A verified certificate repair is preparation, just like roles and enrollment.
    # Keep it if a later identity activation fails; old serving connections remain valid.
    config['root_certificate'] = config['tls']['root_certificate']
    config['substrate']['root_certificate'] = config['tls']['root_certificate']
    return install_identities(config, lock)


def machine_settings(config):
    mcp, state = config['mcp'], Path(config['mcp']['state'])
    substrate = dict(config['substrate'], root_certificate=config['tls']['root_certificate'])
    return {'LAPLACE_DATABASE_RUNTIME_DB': connection(substrate),
        'LAPLACE_MCP_RUNTIME_DB': connection(dict(mcp, database=substrate['database'], port=config['port']), True, config['pgsocket']),
        'LAPLACE_MCP_USER': mcp['user'], 'LAPLACE_MCP_STATE_DIR': state, 'LAPLACE_MCP_LOG_DIR': state / 'logs',
        'LAPLACE_APP_DBNAME': config['database'], 'LAPLACE_APP_PGHOST': config['pgsocket'],
        'LAPLACE_APP_PGPORT': config['port'], 'LAPLACE_APP_ROLE': config['database_owner'],
        'LAPLACE_BAO_DATABASE_HOST': config['host'], 'LAPLACE_API_VERIFICATION_URL': config['public_base'],
        'LAPLACE_RUNTIME_DB_ROOT_CERTIFICATE': config['tls']['root_certificate']}


def write_machine_settings(config):
    path = Path(config['machine_file'])
    stat = path.stat()
    candidate = MACHINE.update_assignments(path.read_text(), machine_settings(config))
    SYNC.atomic(path, candidate, stat.st_mode & 0o777, stat.st_uid, stat.st_gid)


def install_identities(config, lock):
    env = dict(os.environ, LAPLACE_HOST_LOCK_FD=str(lock.fileno()),
               LAPLACE_INSTALL_CAPTURE_DIAGNOSTICS='1',
               PATH=config['pgbin'] + os.pathsep + os.environ.get('PATH', ''), BAO_ADDR=config['bao_address'])

    def helper(name, args=(), payload=None):
        try:
            output = checked(['python3', str(HERE / name), *args], env=env,
                pass_fds=(lock.fileno(),), input=json.dumps(payload) if payload is not None else None, timeout=180)
        except CommandFailure as error:
            phase = args[1] if args and args[0] == '--phase' else 'provision/verify'
            diagnostic = Path(config['work']) / ('runtime-failure-' + uuid.uuid4().hex + '.log')
            SYNC.atomic(diagnostic, error.diagnostics, 0o600, 0, 0)
            raise RuntimeError(f'{name}: {phase} failed; private diagnostics: {diagnostic}') from None
        return json.loads(output)

    def sql(statement):
        return checked(['runuser', '-u', config['pg_os_user'], '--', config['pgbin'] + '/psql',
            '-XwAt', '-v', 'ON_ERROR_STOP=1', '-h', config['pgsocket'], '-p', str(config['port']),
            '-d', config['database'], '-f', '-'], input=statement, timeout=30)

    if Path(sql('SHOW data_directory')).resolve() != Path(config['database_data']).resolve():
        raise ValueError('Connected cluster differs from runtime declaration')
    print('Reconciling scoped runtime credentials and database roles...', flush=True)
    helper('application-runtime.py', ['--phase', 'prepare'], config)
    substrate_config = dict(config['substrate'], psql=config['pgbin'] + '/psql', admin_socket=config['pgsocket'],
        admin_role=config['pg_os_user'], admin_os_user=config['pg_os_user'], schema_owner=config['database_owner'],
        lock=config['host_lock'], client_address='127.0.0.1')
    helper('database-role.py', payload=substrate_config)
    mcp, state, prefix = config['mcp'], Path(config['mcp']['state']), Path(config['prefix'])
    try:
        account = pwd.getpwnam(mcp['user'])
    except KeyError:
        checked(['useradd', '--system', '--user-group', '--home-dir', str(state), '--no-create-home',
                 '--shell', '/usr/sbin/nologin', mcp['user']], timeout=20)
        account = pwd.getpwnam(mcp['user'])
    for directory in (state, state / 'logs', state / 'tmp', state / 'out'):
        if any(path.is_symlink() for path in (directory, *directory.parents)):
            raise ValueError('Runtime state must not traverse symbolic links')
        directory.mkdir(parents=True, exist_ok=True)
        directory.chmod(0o700)
        os.chown(directory, account.pw_uid, account.pw_gid)
    helper('database-role.py', payload=dict(substrate_config, role=mcp['role'], peer_user=mcp['user'],
                                            connection_limit=mcp['connection_limit']))
    fresh = helper('database-sync.py', ['--host', config['host'], '--credentials', config['credentials'],
        '--user', config['runtime_user'], '--prefix', config['prefix'], '--machine-file', config['machine_file'],
        '--lock', config['host_lock'], '--bao-address', config['bao_address'], '--application-record', config['record'],
        '--root-certificate', config['root_certificate']])
    SYNC.validate_substrate_receipt(prefix, fresh['native_receipt'])
    machine, api, mcp_env = Path(config['machine_file']), prefix / 'app/laplace-api.env', prefix / 'app/laplace-mcp.env'
    dropins = [Path(f'/etc/systemd/system/laplace-{service}.service.d/runtime-identity.conf') for service in ('api', 'mcp')]
    for path in dropins:
        path.parent.mkdir(parents=True, exist_ok=True)
    prior_user = checked(['systemctl', 'show', 'laplace-api', '-p', 'User', '--value'], timeout=10)

    def recover():
        checked(['systemctl', 'daemon-reload'], timeout=20)
        checked(['systemctl', 'restart', 'laplace-mcp'], timeout=30)
        SYNC.activate(prior_user, prefix, config['public_base'])

    print('Activating API and MCP identities; failed verification restores serving configuration...', flush=True)
    with SYNC.configuration_transaction([machine, api, mcp_env, *dropins], recover):
        settings = machine_settings(config)
        mcp_connection = settings['LAPLACE_MCP_RUNTIME_DB']
        substrate_connection = settings['LAPLACE_DATABASE_RUNTIME_DB']
        write_machine_settings(config)
        SYNC.assignments(api, {'LAPLACE_DB': substrate_connection})
        SYNC.assignments(mcp_env, {'LAPLACE_DB': mcp_connection, 'LAPLACE_OPS_LOG_DIR': state / 'logs',
            'LAPLACE_OUT': state / 'out', 'LAPLACE_AGENTS_CONFIG': state / 'agents.json'})
        SYNC.atomic(dropins[1], f'[Service]\nUser={mcp["user"]}\nGroup={mcp["user"]}\nUMask=0077\n'
            f'EnvironmentFile=-{prefix}/secrets/agents.env\n'
            f'Environment="HOME={state}" "TMPDIR={state}/tmp" "TMP={state}/tmp" "TEMP={state}/tmp"\n', 0o644, 0, 0)
        checked(['systemctl', 'daemon-reload'], timeout=20)
        checked(['systemctl', 'restart', 'laplace-mcp'], timeout=30)
        helper('application-runtime.py', ['--phase', 'activate'], config)
        verification = helper('application-runtime.py', ['--phase', 'verify'], config)
        verification['mcp'] = mcp_verify(config)
        substrate_args = ['runuser', '-u', config['runtime_user'], '--', config['pgbin'] + '/psql',
            '-XwAt', '-v', 'ON_ERROR_STOP=1', '-h', config['substrate']['database_host'],
            '-p', str(config['port']), '-U', config['substrate']['role'], '-d', config['substrate']['database'], '-f', '-']
        substrate_env = dict(os.environ, PGPASSFILE=str(Path(config['state']) / '.pgpass'),
                             PGSSLMODE='verify-full', PGSSLROOTCERT=config['root_certificate'], PGCONNECT_TIMEOUT='10')
        substrate_env.pop('PGPASSWORD', None)
        actual = checked(substrate_args, env=substrate_env,
            input="SELECT current_user; SELECT encode(laplace.perfcache_receipt(),'hex');", timeout=20)
        if actual.splitlines() != [config['substrate']['role'], fresh['native_receipt']]:
            raise ValueError('API substrate identity/native operation differs from the selected substrate')
        refused = subprocess.run(substrate_args, env=substrate_env, input='SELECT * FROM app.tenants LIMIT 1;',
                                 text=True, capture_output=True, timeout=20)
        if not refused.returncode or 'permission denied' not in refused.stderr:
            raise ValueError('API substrate login can cross the application data boundary')
        verification['substrate'] = {'role': config['substrate']['role'], 'native_receipt': fresh['native_receipt'],
                                     'application_data': 'denied'}
        peer_args = ['runuser', '-u', mcp['user'], '--', config['pgbin'] + '/psql', '-XwAt', '-v', 'ON_ERROR_STOP=1',
            '-h', config['pgsocket'], '-p', str(config['port']), '-U', mcp['role'], '-d', config['substrate']['database'], '-f', '-']
        peer_result = checked(peer_args, input="SELECT current_user; SELECT encode(laplace.perfcache_receipt(),'hex');", timeout=20)
        if peer_result.splitlines() != [mcp['role'], fresh['native_receipt']]:
            raise ValueError('MCP peer identity/native operation differs from the selected substrate')
        denied = subprocess.run(peer_args, input='SELECT * FROM app.tenants LIMIT 1;', text=True, capture_output=True, timeout=20)
        if not denied.returncode or 'permission denied' not in denied.stderr:
            raise ValueError('MCP can cross the application data boundary')
        runner = pwd.getpwnam(declarations(machine)['LAPLACE_AGENT_USER'])
        for path, access in ((Path(runner.pw_dir) / '.pgpass', '-r'), (Path(config['credentials']), '-r'),
                             (prefix / 'app/mcp-runtime/Laplace.Endpoints.Mcp.dll', '-w')):
            if not path.exists() or subprocess.run(['runuser', '-u', mcp['user'], '--', 'test', access, str(path)]).returncode != 1:
                raise ValueError('MCP deployment/runtime access boundary failed')
        verification['mcp'].update(database_peer_identity=mcp['role'], native_receipt=fresh['native_receipt'], application_data='denied')
    # This existing setup operation installs a root-owned credential refresh timer.
    # A timer failure leaves verified serving identities in place and is retryable.
    install_refresh(config, lock)
    checked(['systemctl', 'is-active', '--quiet', 'laplace-database-refresh.timer'], timeout=10)
    verification['credential_refresh_timer'] = 'active'
    return verification


def install_refresh(config, lock):
    """Install the workload refresh independently of a source checkout or human login."""
    directory = Path(config.get('refresh_directory', '/usr/local/libexec/laplace-mono'))
    if not directory.is_absolute() or '..' in directory.parts or directory == Path('/'):
        raise ValueError('Invalid credential refresh directory')
    directory.mkdir(parents=True, exist_ok=True)
    for path in (directory, *directory.parents):
        info = path.stat()
        if path.is_symlink() or info.st_uid != 0 or info.st_mode & 0o022:
            raise ValueError('Credential refresh code requires root-owned, non-writable parents: ' + str(path))
    script = directory / 'database-sync.py'
    SYNC.atomic(script, (HERE / 'database-sync.py').read_text(), 0o755, 0, 0)
    arguments = ['--host', config['host'], '--credentials', config['credentials'],
                 '--user', config['runtime_user'], '--prefix', config['prefix'],
                 '--machine-file', config['machine_file'], '--bao-address', config['bao_address'],
                 '--lock', config['host_lock'], '--root-certificate', config['tls']['root_certificate'],
                 '--application-record', config['record'], '--restart-on-change',
                 '--verification-base', config['public_base']]
    command = ['/usr/bin/python3', str(script), *arguments]
    checked(command, env=dict(os.environ, LAPLACE_HOST_LOCK_FD=str(lock.fileno())),
            pass_fds=(lock.fileno(),), timeout=120)
    launcher = directory / 'refresh'
    SYNC.atomic(launcher, '#!/bin/sh\nset -eu\nexec ' + shlex.join(command) + '\n', 0o755, 0, 0)
    # Retire the previous timer before enabling its replacement so credentials
    # have one refresh owner during a naming migration.
    legacy_timer = 'laplace-knowledge-refresh.timer'
    if checked(['systemctl', 'show', legacy_timer, '-p', 'LoadState', '--value'], timeout=10) == 'loaded':
        checked(['systemctl', 'disable', '--now', legacy_timer], timeout=20)
        checked(['systemctl', 'stop', 'laplace-knowledge-refresh.service'], timeout=30)
    executable = str(launcher).replace('\\', '\\\\').replace('"', '\\"').replace('%', '%%').replace('$', '$$')
    SYNC.atomic(Path('/etc/systemd/system/laplace-database-refresh.service'),
                '[Unit]\nDescription=Refresh Laplace monorepo workload credentials through OpenBao\n'
                'After=network-online.target\nWants=network-online.target\n[Service]\nType=oneshot\n'
                f'ExecStart="{executable}"\nNoNewPrivileges=true\nUMask=0077\n', 0o644, 0, 0)
    SYNC.atomic(Path('/etc/systemd/system/laplace-database-refresh.timer'),
                '[Unit]\nDescription=Refresh Laplace monorepo workload credentials\n[Timer]\n'
                'OnBootSec=2min\nOnUnitActiveSec=15min\nPersistent=true\n[Install]\n'
                'WantedBy=timers.target\n', 0o644, 0, 0)
    checked(['systemctl', 'daemon-reload'], timeout=20)
    checked(['systemctl', 'enable', '--now', 'laplace-database-refresh.timer'], timeout=20)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path, help='Absolute runtime declaration; otherwise use the machine declaration or matching host manifest')
    parser.add_argument('--select', action='store_true', help='Resolve and validate declared topology without changing the installation')
    parser.add_argument('--check', action='store_true', help='Check installed prerequisites; root also verifies the private workload enrollment')
    parser.add_argument('--storage-check', action='store_true', help='Check the filesystems the declared installation will write before changing services')
    args = parser.parse_args()
    manifest = selected_manifest(args.manifest)
    if manifest is None:
        if args.select:
            return
        raise ValueError('No runtime manifest declared for this host; set LAPLACE_RUNTIME_MANIFEST in machine.env')
    config = json.loads(manifest.read_text())
    if args.select:
        validate_declaration(config)
        print(manifest)
        return
    if args.storage_check:
        validate_declaration(config)
        print(json.dumps({'storage': storage_preflight(config), 'changed': False}))
        return
    if args.check:
        receipt = preflight(config)
        if os.path.lexists(config['credentials']):
            if os.geteuid() == 0:
                runtime_records(config)
                receipt['workload_authentication'] = 'verified'
            else:
                receipt['workload_authentication'] = 'requires_privileged_check'
        else:
            Bao(config).call(['token', 'lookup', '-field=ttl'])
            receipt['workload_authentication'] = 'initial_enrollment_required'
        print(json.dumps(dict(receipt, preflight='passed', activated=False)))
        return
    if os.geteuid() != 0:
        raise ValueError('Install identities with sudo bash deploy/linux/site-host.sh --runtime-only; --check does not require sudo')
    validate_declaration(config)
    with open(config['host_lock'], 'a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        work = Path(config['work'])
        work.mkdir(parents=True, exist_ok=True)
        started = datetime.now(timezone.utc)
        output = work / (started.strftime('%Y%m%dT%H%M%SZ') + '-runtime-' + uuid.uuid4().hex[:8] + '.json')
        receipt = dict(operation='runtime', host=config['host'], started_utc=started.isoformat(),
                       manifest_sha256=hashlib.sha256(manifest.read_bytes()).hexdigest(),
                       source=checked(['git', '-C', str(ROOT), 'rev-parse', 'HEAD'], timeout=10))
        try:
            receipt.update(preflight(config))
            receipt['verification'] = install(config, lock)
            # The newly enabled timer may fire immediately. Release the installation
            # lock before waiting for its independent credential-refresh operation.
            fcntl.flock(lock, fcntl.LOCK_UN)
            checked(['systemctl', 'start', 'laplace-database-refresh.service'], timeout=90)
            receipt['status'] = 'passed'
        except Exception as error:
            receipt.update(status='failed', error=str(error))
            raise
        finally:
            if '_tls_verification' in config:
                receipt['database_tls'] = config['_tls_verification']
            receipt['finished_utc'] = datetime.now(timezone.utc).isoformat()
            SYNC.atomic(output, json.dumps(receipt, indent=2) + '\n', 0o644, 0, 0)
            print('Runtime setup receipt: ' + str(output), flush=True)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print('Runtime setup failed: ' + str(error), file=sys.stderr)
        sys.exit(1)
