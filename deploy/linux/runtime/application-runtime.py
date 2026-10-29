#!/usr/bin/env python3
"""Provision and activate the declared application OS/database runtime identity."""
import argparse
import fcntl
import importlib.util
import json
import os
from pathlib import Path
import pwd
import re
import shutil
import subprocess
import sys
import urllib.error
import urllib.request


def literal(value):
    return "'" + value.replace("'", "''") + "'"


def verify_workload(config):
    token = None
    def request(path, payload=None):
        headers = {'Content-Type': 'application/json'}
        if token:
            headers['X-Vault-Token'] = token
        data = json.dumps(payload).encode() if payload is not None else None
        with urllib.request.urlopen(urllib.request.Request(config['bao_address'].rstrip('/') + '/v1/' + path,
                headers=headers, data=data), timeout=20) as response:
            body = response.read(1024 * 1024)
            return json.loads(body) if body else None
    def denied(path):
        try:
            request(path)
        except urllib.error.HTTPError as error:
            if error.code == 403:
                return
            raise ValueError('Unexpected OpenBao refusal status') from None
        raise ValueError('Workload identity can read a prohibited credential')
    credential = Path(config['credentials'])
    if credential.stat().st_mode & 0o077 or credential.stat().st_uid != 0:
        raise ValueError('Workload enrollment must be root-owned and private')
    try:
        token = request('auth/approle/login', json.loads(credential.read_text()))['auth']['client_token']
        allowed = [f"kv/data/hosts/{config['host']}/{record}" for record in ('substrate', config['record'])]
        for path in allowed:
            request(path)
            capabilities = request('sys/capabilities-self', {'paths': [path]})['capabilities']
            if set(capabilities) != {'read'}:
                raise ValueError('Workload credential access must be read-only')
        for path in (f"kv/data/hosts/{config['host']}/appdb", 'kv/data/shared/laplace-auth'):
            denied(path)
        request('auth/token/revoke-self', {})
        denied(allowed[0])
        token = None
    finally:
        if token:
            request('auth/token/revoke-self', {})
    return {'runtime_records': 'read-only', 'migration_credential': 'denied',
            'shared_identity_secrets': 'denied', 'revoked_token': 'denied'}


def verify(config, *, stdout_only=False):
    account = pwd.getpwnam(config['runtime_user'])
    service_user = subprocess.check_output(['systemctl', 'show', 'laplace-api', '-p', 'User', '--value'], text=True).strip()
    if service_user != account.pw_name or subprocess.call(['systemctl', 'is-active', '--quiet', 'laplace-api']):
        raise ValueError('The declared API identity is not serving')
    prefix, state = Path(config['prefix']), Path(config['state'])
    api = (prefix / 'app/laplace-api.env').read_text()
    runtime_line = next(line for line in api.splitlines() if line.startswith('LAPLACE_APP_DB='))
    if f"Username={config['role']};" not in runtime_line:
        raise ValueError('Installed API has not selected the runtime database login')
    machine = Path(config['machine_file']).read_text()
    runner_name = next((line.partition('=')[2].strip() for line in machine.splitlines() if line.startswith('LAPLACE_AGENT_USER=')), 'laplace-runner')
    runner = pwd.getpwnam(runner_name)
    boundaries = ((Path(runner.pw_dir) / '.pgpass', '-r', 'deployment_credentials'),
                  (Path(config['credentials']), '-r', 'host_approle'),
                  (prefix / 'app/Laplace.Endpoints.OpenAICompat.dll', '-w', 'deployed_binary'))
    checks = {}
    for path, access, name in boundaries:
        if not path.exists():
            raise ValueError('Cannot prove access refusal for a missing boundary: ' + name)
        result = subprocess.run(['runuser', '-u', account.pw_name, '--', 'test', access, str(path)])
        if result.returncode != 1:
            raise ValueError('Runtime unexpectedly crosses boundary: ' + name)
        checks[name] = 'denied'
    pgpass = state / '.pgpass'
    if pgpass.stat().st_mode & 0o077:
        raise ValueError('Runtime credentials are not private')
    roles = {line.split(':')[3] for line in pgpass.read_text().splitlines() if line and not line.startswith('#')}
    if config['database_owner'] in roles or config['role'] not in roles:
        raise ValueError('Runtime pgpass contains the wrong credential roles')
    environment = dict(os.environ, PGPASSFILE=str(pgpass), PGSSLMODE='require', PGCONNECT_TIMEOUT='10')
    if config.get('root_certificate'):
        environment.update(PGSSLMODE='verify-full', PGSSLROOTCERT=config['root_certificate'])
    environment.pop('PGPASSWORD', None)

    def runtime_sql(statement, database=config['database']):
        return subprocess.run(['runuser', '-u', account.pw_name, '--', str(Path(config['pgbin']) / 'psql'),
            '-X', '-w', '-At', '-v', 'ON_ERROR_STOP=1', '-v', 'VERBOSITY=sqlstate', '-h', config['database_host'],
            '-p', str(config['port']), '-U', config['role'], '-d', database, '-f', '-'],
            env=environment, input=statement, text=True, capture_output=True, timeout=20)
    result = runtime_sql('SELECT current_user; SELECT count(*) FROM app.api_keys')
    if result.returncode or result.stdout.splitlines()[0] != config['role']:
        raise ValueError('Runtime database identity cannot read its application data')
    for statement in (f"SET ROLE {config['database_owner']}",
                      'BEGIN; CREATE TABLE app.laplace_runtime_permission_probe(id integer); ROLLBACK;'):
        result = runtime_sql(statement)
        if not result.returncode or '42501' not in result.stderr:
            raise ValueError('Runtime database boundary did not refuse the prohibited operation')
    result = runtime_sql('SELECT 1', 'postgres')
    if not result.returncode or 'no pg_hba.conf entry' not in result.stderr:
        raise ValueError('Unrelated database connection was not refused by the declared HBA rule')
    checks.update(application_data='readable', database_owner_impersonation='denied', runtime_ddl='denied', unrelated_database='denied')
    checks['openbao'] = verify_workload(config)
    receipt = {'passed': True, 'runtime_user': account.pw_name, 'runtime_uid': account.pw_uid,
               'database_role': config['role'], 'credential_roles': sorted(roles), 'checks': checks}
    if not stdout_only:
        output = Path(config['work']) / 'runtime-verification.json'
        output.write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps(receipt))


def database_sql(config, statement, target=None):
    result = subprocess.run(['runuser', '-u', config['pg_os_user'], '--', str(Path(config['pgbin']) / 'psql'),
        '-X', '-w', '-At', '-v', 'ON_ERROR_STOP=1', '-h', config['pgsocket'], '-p', str(config['port']),
        '-d', target or config['database'], '-f', '-'], input=statement, text=True, capture_output=True, timeout=30)
    if result.returncode:
        raise RuntimeError('Runtime database operation failed; inspect the database server log')
    return result.stdout.strip()


def resource_environment(config, sql):
    data = Path(sql('SHOW data_directory'))
    if data.resolve() != Path(config['database_data']).resolve():
        raise ValueError('The declared application cluster differs from the connected database')
    service_pid = subprocess.check_output(['systemctl', 'show', config['database_service'], '-p', 'MainPID', '--value'], text=True).strip()
    if service_pid == '0' or service_pid != (data / 'postmaster.pid').read_text().splitlines()[0]:
        raise ValueError('The declared service does not own the connected database process')
    resource_keys = {'LAPLACE_PG_SERVING', 'LAPLACE_PG_WORK_NODES', 'LAPLACE_PG_RESERVED', 'LAPLACE_PG_BUFFERS_MAX'}
    if set(config['resource_settings']) != resource_keys:
        raise ValueError('Incomplete application resource declaration')
    # A caller's fixture values must never override facts on a serving host.
    environment = {key: value for key, value in os.environ.items() if not key.startswith('TUNE_FACT_')}
    environment.update(config['resource_settings'])
    environment.update(LAPLACE_PG_PROFILE='app', LAPLACE_PGDATA=str(data), LAPLACE_PGWAL=str(data / 'pg_wal'),
        LAPLACE_PG_DIR=str(Path(config['pgbin']).parent), LAPLACE_PGUSER=config['pg_os_user'],
        LAPLACE_PGSERVICE=config['database_service'], TUNE_SOCKET=config['pgsocket'], TUNE_PORT=str(config['port']),
        TUNE_CONN=f"host={config['pgsocket']} port={config['port']} user={config['pg_os_user']} dbname=postgres",
        TUNE_PGDATA=str(data), PGWAL=str(data / 'pg_wal'), PGBIN=config['pgbin'], PGUSER=config['pg_os_user'],
        SERVICE=config['database_service'],
        LAPLACE_CONNINFO=f"host={config['pgsocket']} port={config['port']} user={config['pg_os_user']} dbname={config['database']}")
    return data, environment


def main(argv=None, *, resource_tuner=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--phase', choices=('prepare', 'activate', 'verify', 'resources', 'resources-check'), required=True)
    parser.add_argument('--stdout-only', action='store_true', help='Verify without writing a remote receipt')
    args = parser.parse_args(argv)
    if args.stdout_only and args.phase not in ('verify', 'resources-check'):
        raise ValueError('--stdout-only applies only to verify and resources-check')
    config = json.load(sys.stdin)
    if os.geteuid() != 0:
        raise ValueError('The authorized host administrator must install the runtime identity')
    for field in ('database', 'role', 'database_owner', 'runtime_user', 'pg_os_user', 'record'):
        if not re.fullmatch(r'[a-z][a-z0-9_-]*', config[field]):
            raise ValueError('Invalid identity declaration: ' + field)
    if config['role'] == config['database_owner'] or config['runtime_user'] in ('root', config['pg_os_user']):
        raise ValueError('Runtime must be separate from database administration')
    if args.phase in ('prepare', 'activate') and not re.fullmatch('[0-9a-f]{64}', config['password']):
        raise ValueError('Invalid generated runtime credential')
    state, prefix = Path(config['state']), Path(config['prefix'])
    if not state.is_absolute() or state == Path('/') or state.is_symlink() or state == prefix or state in prefix.parents or prefix in state.parents:
        raise ValueError('Runtime state must be an independent absolute directory')
    if args.phase == 'verify':
        verify(config, stdout_only=args.stdout_only)
        return
    if args.phase == 'resources-check':
        # Readback must not import installers, create scratch/locks or copy code
        # into the serving host. The caller supplies the selected tuning owner.
        with open(config['host_lock'], 'r') as lock:
            fcntl.flock(lock, fcntl.LOCK_SH | fcntl.LOCK_NB)
            sql = lambda statement, target=None: database_sql(config, statement, target)
            if sql('SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname=' + literal(config['database']), 'postgres') != config['database_owner']:
                raise ValueError('The declared application database owner does not match the installation')
            _, environment = resource_environment(config, sql)
            if resource_tuner is None:
                resource_tuner = Path(__file__).with_name('tune-pg.sh').read_text()
            result = subprocess.run(['bash', '-c', 'source /dev/stdin; tune_main --check'],
                                    input=resource_tuner, text=True, capture_output=True, env=environment, timeout=30)
            if result.returncode:
                raise RuntimeError('Application resource readback failed: ' + (result.stderr or result.stdout).strip())
            print(result.stdout, end='')
            print(json.dumps({'application_resources': 'verified', 'database_service': config['database_service'],
                              'resource_settings': config['resource_settings']}))
        return
    spec = importlib.util.spec_from_file_location('substrate_sync', Path(__file__).with_name('database-sync.py'))
    sync = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(sync)
    inherited = os.environ.get('LAPLACE_HOST_LOCK_FD')
    with (os.fdopen(os.dup(int(inherited)), 'a') if inherited else open(config['host_lock'], 'a')) as lock:
        if not os.path.samestat(os.fstat(lock.fileno()), os.stat(config['host_lock'])):
            raise ValueError('Inherited operation lock belongs to a different installation')
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        database, role, owner = (config[name] for name in ('database', 'role', 'database_owner'))

        def sql(statement, target=database):
            return database_sql(config, statement, target)

        if sql('SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname=' + literal(database), 'postgres') != owner:
            raise ValueError('The declared application database owner does not match the installation')
        if args.phase == 'resources':
            data, environment = resource_environment(config, sql)
            tuner = str(Path(__file__).with_name('tune-pg.sh'))
            subprocess.run(['bash', '-c', 'source "$1"; tune_validate_plan', 'application-resources', tuner],
                env=environment, check=True, timeout=30)
            def recover_resources():
                subprocess.run(['systemctl', 'restart', config['database_service']], check=True, timeout=60)
                sync.activate(config['runtime_user'], prefix, config['public_base'])
            machine = Path(config['machine_file'])
            with sync.configuration_transaction([machine, data / 'postgresql.auto.conf'], recover_resources):
                # App-specific names do not retune a co-located substrate cluster.
                sync.assignments(machine, {key.replace('LAPLACE_PG_', 'LAPLACE_APP_PG_'): value
                                          for key, value in config['resource_settings'].items()})
                subprocess.run(['bash', tuner], env=environment, check=True, timeout=180)
                sync.activate(config['runtime_user'], prefix, config['public_base'])
            verify(config)
            print(json.dumps({'application_resources': 'applied-and-verified', 'database_service': config['database_service'],
                              'resource_settings': config['resource_settings']}))
            return
        if args.phase == 'prepare':
            privileged = sql('SELECT rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls '
                             'FROM pg_roles WHERE rolname=' + literal(role))
            if privileged == 't':
                raise ValueError('Refusing to repurpose a privileged role')
            if not privileged:
                sql(f'CREATE ROLE "{role}" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD ' + literal(config['password']))
            if sql(f"SELECT pg_has_role({literal(role)}, {literal(owner)}, 'MEMBER')") != 'f':
                raise ValueError('Runtime must not inherit the migration owner')
            limit = int(config['connection_limit'])
            if limit < 1 or limit < int(config['pool_max']):
                raise ValueError('Runtime connection allocation is invalid')
            sql(f'ALTER ROLE "{role}" CONNECTION LIMIT {limit}; GRANT CONNECT ON DATABASE "{database}" TO "{role}"; '
                f'GRANT USAGE ON SCHEMA app TO "{role}"; '
                f'GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA app TO "{role}"; '
                f'GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA app TO "{role}"; '
                f'ALTER DEFAULT PRIVILEGES FOR ROLE "{owner}" IN SCHEMA app GRANT SELECT,INSERT,UPDATE,DELETE ON TABLES TO "{role}"; '
                f'ALTER DEFAULT PRIVILEGES FOR ROLE "{owner}" IN SCHEMA app GRANT USAGE,SELECT ON SEQUENCES TO "{role}";')
            hba = Path(sql('SHOW hba_file'))
            remote = hba.with_name('pg_hba.runtime.conf')
            before = hba.read_text()
            rules = (f'hostssl {database} {role} 127.0.0.1/32 scram-sha-256\n'
                     f'hostssl /^laplace_restore_[0-9a-f]{{16}}$ {role} 127.0.0.1/32 scram-sha-256\n')
            previous = remote.read_text() if remote.exists() else None
            stat = hba.stat()
            try:
                sync.atomic(remote, rules, 0o640, stat.st_uid, stat.st_gid)
                include = 'include_if_exists pg_hba.runtime.conf'
                if include not in before.splitlines():
                    sync.atomic(hba, include + '\n' + before, stat.st_mode & 0o777, stat.st_uid, stat.st_gid)
                if sql('SELECT count(*) FROM pg_hba_file_rules WHERE error IS NOT NULL') != '0':
                    raise ValueError('Runtime HBA rules did not validate')
                if sql('SELECT pg_reload_conf()') != 't':
                    raise ValueError('Runtime HBA reload failed')
            except BaseException:
                sync.atomic(hba, before, stat.st_mode & 0o777, stat.st_uid, stat.st_gid)
                if previous is None:
                    remote.unlink(missing_ok=True)
                else:
                    sync.atomic(remote, previous, 0o640, stat.st_uid, stat.st_gid)
                sql('SELECT pg_reload_conf()')
                raise
            try:
                account = pwd.getpwnam(config['runtime_user'])
            except KeyError:
                subprocess.run(['useradd', '--system', '--user-group', '--home-dir', str(state), '--no-create-home',
                                '--shell', '/usr/sbin/nologin', config['runtime_user']], check=True)
                account = pwd.getpwnam(config['runtime_user'])
            if account.pw_uid == 0 or Path(account.pw_dir) != state:
                raise ValueError('Existing OS identity does not match the declared runtime home')
            for directory in (state, state / 'logs', state / 'tmp', state / 'keys', state / 'out'):
                if any(path.is_symlink() for path in (directory, *directory.parents)):
                    raise ValueError('Runtime state must not traverse symbolic links')
                directory.mkdir(parents=True, exist_ok=True)
                directory.chmod(0o700)
                os.chown(directory, account.pw_uid, account.pw_gid)
            old_keys = prefix / 'secrets/data-protection'
            for old in old_keys.glob('*.xml'):
                target = state / 'keys' / old.name
                if target.is_symlink():
                    raise ValueError('Runtime key destination must not be a symbolic link')
                if not target.exists():
                    shutil.copyfile(old, target)
                    target.chmod(0o600)
                    os.chown(target, account.pw_uid, account.pw_gid)
            print(json.dumps({'prepared': True, 'runtime_user': account.pw_name, 'database_role': role,
                              'connection_limit': limit, 'activated': False}))
            return

        account = pwd.getpwnam(config['runtime_user'])
        if Path(account.pw_dir) != state or not (state / '.pgpass').is_file():
            raise ValueError('Prepare the runtime identity and refresh its verified credentials first')
        prior_user = subprocess.check_output(['systemctl', 'show', 'laplace-api', '-p', 'User', '--value'], text=True).strip()
        if not prior_user:
            raise ValueError('No current API service identity')
        machine, api = Path(config['machine_file']), prefix / 'app/laplace-api.env'
        dropin = Path('/etc/systemd/system/laplace-api.service.d/runtime-identity.conf')
        dropin.parent.mkdir(parents=True, exist_ok=True)
        tls = (f"SSL Mode=VerifyFull;Root Certificate={config['root_certificate']};"
               if config.get('root_certificate') else 'SSL Mode=Require;')
        runtime_db = (f"Host={config['database_host']};Port={config['port']};Database={database};Username={role};"
                      f"{tls}Maximum Pool Size={int(config['pool_max'])}")
        with sync.configuration_transaction([machine, api, dropin],
                lambda: (subprocess.run(['systemctl', 'daemon-reload'], check=True), sync.activate(prior_user, prefix, config['public_base']))):
            sync.assignments(machine, {'LAPLACE_API_USER': config['runtime_user'], 'LAPLACE_API_STATE_DIR': state,
                'LAPLACE_API_LOG_DIR': state / 'logs', 'LAPLACE_DATA_PROTECTION_KEYS': state / 'keys',
                'LAPLACE_BAO_APP_RUNTIME_RECORD': config['record'], 'LAPLACE_BAO_RUNTIME_CREDENTIALS': config['credentials'],
                'LAPLACE_APP_RUNTIME_DB': runtime_db})
            sync.assignments(api, {'LAPLACE_APP_DB': runtime_db, 'LAPLACE_OPS_LOG_DIR': state / 'logs',
                'LAPLACE_DATA_PROTECTION_KEYS': state / 'keys', 'LAPLACE_AGENTS_CONFIG': state / 'agents.json',
                'LAPLACE_OUT': state / 'out'})
            sync.atomic(dropin, f'[Service]\nUser={config["runtime_user"]}\nGroup={config["runtime_user"]}\nUMask=0077\n'
                f'EnvironmentFile=-{prefix}/secrets/agents.env\n'
                f'Environment="HOME={state}" "PGPASSFILE={state}/.pgpass" "TMPDIR={state}/tmp" "TMP={state}/tmp" "TEMP={state}/tmp"\n', 0o644, 0, 0)
            subprocess.run(['systemctl', 'daemon-reload'], check=True)
            sync.activate(config['runtime_user'], prefix, config['public_base'])
        print(json.dumps({'activated': True, 'runtime_user': config['runtime_user'], 'database_role': role,
                          'prior_runtime_user': prior_user, 'runtime_verification': 'passed'}))


if __name__ == '__main__':
    main()
