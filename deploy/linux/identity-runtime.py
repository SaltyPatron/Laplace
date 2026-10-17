#!/usr/bin/env python3
"""Enroll and install the monorepo's independently scoped browser identity agent."""
import argparse
from datetime import datetime, timezone
import fcntl
import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

SERVICE = 'laplace-mono-identity.service'
KEYS = ('google_client_id', 'google_client_secret', 'microsoft_client_id', 'microsoft_client_secret')


def run(args, payload=None):
    result = subprocess.run(args, input=payload, text=True, capture_output=True, timeout=90)
    if result.returncode:
        raise RuntimeError('Command failed: ' + ' '.join(args[:2]))
    return result.stdout


def write(path, value, mode=0o600):
    path = Path(path)
    raw = value.encode() if isinstance(value, str) else value
    if path.is_symlink():
        raise ValueError('Refusing a symlink at ' + str(path))
    if path.exists() and path.read_bytes() == raw:
        path.chmod(mode)
        return False
    fd, temporary = tempfile.mkstemp(prefix='.identity-', dir=path.parent)
    try:
        with os.fdopen(fd, 'wb') as stream:
            os.fchmod(stream.fileno(), mode)
            stream.write(raw)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    return True


def request(config, path, token='', payload=None):
    headers = {'Content-Type': 'application/json'}
    if token:
        headers['X-Vault-Token'] = token
    req = urllib.request.Request(config['address'] + '/v1/' + path,
        data=None if payload is None else json.dumps(payload).encode(), headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=20) as response:
            body = response.read()
            return json.loads(body) if body else {}
    except urllib.error.HTTPError as error:
        raise RuntimeError('OpenBao ' + path + ' returned HTTP ' + str(error.code)) from None


def login(config):
    return request(config, 'auth/approle/login', payload={k: config[k] for k in ('role_id', 'secret_id')})['auth']['client_token']


def validate_record(record):
    # Provider-issued credentials are printable ASCII. This also prevents a
    # provider field from creating another systemd environment assignment.
    for key in KEYS:
        value = record.get(key)
        if not isinstance(value, str) or not value or any(ord(c) < 33 or ord(c) > 126 for c in value):
            raise ValueError('Missing or invalid provider field: ' + key)
    return record


def verify_access(config):
    token = login(config)
    path = 'kv/data/' + config['record']
    unrelated = ['kv/data/shared/laplace-auth', 'sys/policies/acl/default', 'kv/data/hosts/hart-desktop/appdb']
    try:
        record = validate_record(request(config, path, token)['data']['data'])
        capabilities = request(config, 'sys/capabilities-self', token, {'paths': [path, *unrelated]})['data']
        if capabilities[path] != ['read'] or any(capabilities[p] != ['deny'] for p in unrelated):
            raise ValueError('The workload policy is not scoped to its one identity record')
        try:
            request(config, unrelated[0], token)
        except RuntimeError as error:
            if 'HTTP 403' not in str(error):
                raise
        else:
            raise ValueError('An unrelated credential read was permitted')
        request(config, 'auth/token/renew-self', token, {})
    finally:
        request(config, 'auth/token/revoke-self', token, {})
    try:
        request(config, path, token)
    except RuntimeError as error:
        if 'HTTP 403' not in str(error):
            raise
    else:
        raise ValueError('Revoked token was accepted')
    fresh = login(config)
    try:
        if request(config, path, fresh)['data']['data'] != record:
            raise ValueError('Fresh authentication returned different credentials')
    finally:
        request(config, 'auth/token/revoke-self', fresh, {})
    return record


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args):
        return None


def verify_application(config, record):
    opener = urllib.request.build_opener(NoRedirect)
    for attempt in range(30):
        try:
            # Identity activation is independent of substrate ingestion. Verify
            # the serving process here and each provider's client ID below.
            with urllib.request.urlopen(config['api_url'] + '/health', timeout=3) as response:
                if response.status != 200:
                    raise RuntimeError('Application health failed')
            for provider in ('google', 'microsoft'):
                origin = urllib.parse.urlsplit(config['origin'])
                req = urllib.request.Request(config['api_url'] + '/v1/auth/login/' + provider,
                    headers={'Host': origin.netloc, 'X-Forwarded-Host': origin.netloc, 'X-Forwarded-Proto': origin.scheme})
                try:
                    opener.open(req, timeout=3)
                except urllib.error.HTTPError as redirect:
                    target = urllib.parse.urlsplit(redirect.headers.get('Location', ''))
                    query = urllib.parse.parse_qs(target.query)
                    if redirect.code not in (302, 303) or query.get('client_id') != [record[provider + '_client_id']]:
                        raise RuntimeError('Application has not loaded the selected ' + provider + ' client')
                else:
                    raise RuntimeError('Application did not start provider authentication')
            return
        except (OSError, RuntimeError):
            if attempt == 29:
                raise RuntimeError('Application health or provider activation failed') from None
            time.sleep(1)


def refresh(config, directory, source='installation'):
    record = validate_record(json.loads((directory / 'candidate.json').read_text()))
    rendered = ''.join('LAPLACE_AUTH_' + key.upper() + '=' + json.dumps(record[key]) + '\n' for key in KEYS)
    target = directory / 'identity.env'
    lock = Path(config['lock'])
    with lock.open('a') as handle:
        # through the turnstile, as deploy/linux/host-lock.sh takes the lock: while this waits, no later reader is
        # granted the lock ahead of it (flock alone grants a new shared holder past a waiting exclusive one)
        mask = os.umask(0o002)
        try:
            turnstile = open(str(lock) + '.turnstile', 'a')
        finally:
            os.umask(mask)
        with turnstile:
            fcntl.flock(turnstile, fcntl.LOCK_EX)
            fcntl.flock(handle, fcntl.LOCK_EX)
        previous = target.read_bytes() if target.exists() else None
        changed = write(target, rendered)
        try:
            if changed:
                run(['systemctl', 'restart', config['api_service']])
            verify_application(config, record)
        except Exception:
            if changed:
                if previous is None:
                    target.unlink(missing_ok=True)
                else:
                    write(target, previous)
                run(['systemctl', 'restart', config['api_service']])
            raise
        receipt = {'verified_utc': datetime.now(timezone.utc).isoformat(), 'host': config['host'],
                   'record': 'kv/' + config['record'], 'google_client_id': record['google_client_id'],
                   'changed': changed, 'application_provider_configuration': 'verified', 'source': source}
        write(directory / 'receipt.json', json.dumps(receipt, indent=2) + '\n', 0o644)
    print(json.dumps(receipt))


def enroll(args):
    declaration = json.loads(Path(args.configuration).read_text())
    environment = next(e for e in declaration['environments'] if e['name'] == args.environment)
    if environment['name'] == 'development':
        raise ValueError('Desktop uses its existing DPAPI enrollment')
    output = Path(args.enrollment)
    os.environ['BAO_ADDR'] = declaration['bao_address']
    role = environment['role']
    if not re.fullmatch(r'[a-z0-9-]+', role) or not re.fullmatch(r'[a-z0-9/-]+', environment['record']):
        raise ValueError('Invalid identity policy reference')
    policy = 'path "kv/data/' + environment['record'] + '" { capabilities = ["read"] }\n'
    for path, cap in [('auth/token/lookup-self', 'read'), ('auth/token/renew-self', 'update'),
                      ('auth/token/revoke-self', 'update'), ('sys/capabilities-self', 'update')]:
        policy += 'path "' + path + '" { capabilities = ["' + cap + '"] }\n'
    run(['bao', 'policy', 'write', role, '-'], policy)
    run(['bao', 'write', 'auth/approle/role/' + role, 'token_policies=' + role, 'token_no_default_policy=true',
         'token_ttl=5m', 'token_max_ttl=10m', 'secret_id_ttl=0', 'secret_id_num_uses=0'])
    config = {'host': environment['host'], 'environment': environment['name'], 'address': declaration['bao_address'],
              'record': environment['record'], 'role': role, 'api_url': args.api_url,
              'origin': environment['callbacks'][0].removesuffix('/signin-google'),
              'api_service': args.api_service, 'lock': args.lock}
    if output.exists():
        previous = json.loads(output.read_text())
        if any(previous[k] != v for k, v in config.items()):
            raise ValueError('Existing enrollment has a different target configuration')
        config = previous
    else:
        config['role_id'] = json.loads(run(['bao', 'read', '-format=json', 'auth/approle/role/' + role + '/role-id']))['data']['role_id']
        config['secret_id'] = json.loads(run(['bao', 'write', '-f', '-format=json', 'auth/approle/role/' + role + '/secret-id']))['data']['secret_id']
    record = verify_access(config)
    write(output, json.dumps(config, indent=2) + '\n')
    print(json.dumps({'enrollment': str(output), 'host': config['host'], 'policy': role,
                      'google_client_id': record['google_client_id'], 'read_deny_renew_revoke_reauthenticate': 'passed'}))


def install(args):
    if os.geteuid() != 0:
        raise ValueError('Identity installation requires root')
    directory = Path(args.directory)
    source = Path(args.enrollment) if args.enrollment else directory / 'enrollment.json'
    config = json.loads(source.read_text())
    if socket.gethostname().lower().split('.')[0] != config['host']:
        raise ValueError('Enrollment belongs to a different host')
    if not re.fullmatch(r'[a-zA-Z0-9_-]+\.service', config['api_service']):
        raise ValueError('Invalid application service')
    for path in (str(directory), config['lock']):
        if not re.fullmatch(r'/[a-zA-Z0-9_./-]+', path) or '..' in Path(path).parts:
            raise ValueError('Unsafe identity installation path')
    record = verify_access(config)
    if subprocess.run(['systemctl', 'is-active', '--quiet', SERVICE]).returncode == 0:
        run(['systemctl', 'stop', SERVICE])
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    if directory.is_symlink() or directory.stat().st_uid != 0:
        raise ValueError('Identity configuration must be a root-owned real directory')
    directory.chmod(0o700)
    write(directory / 'enrollment.json', json.dumps(config, indent=2) + '\n')
    write(directory / 'role-id', config['role_id'])
    write(directory / 'secret-id', config['secret_id'])
    write(directory / 'identity-runtime.py', Path(__file__).read_bytes(), 0o700)
    template = '{{ with secret "kv/data/' + config['record'] + '" }}{{ .Data.data | toJSON }}{{ end }}\n'
    write(directory / 'identity.ctmpl', template)
    agent = '''vault { address = ADDRESS }
auto_auth {
  method "approle" {
    config = {
      role_id_file_path = ROLE
      secret_id_file_path = SECRET
      remove_secret_id_file_after_reading = false
    }
  }
}
template_config {
  static_secret_render_interval = "1m"
  exit_on_retry_failure = true
}
template {
  source = TEMPLATE
  destination = CANDIDATE
  perms = "0600"
  backup = false
  error_on_missing_key = true
  exec {
    command = COMMAND
    timeout = "180s"
  }
}
'''
    values = {'ADDRESS': config['address'], 'ROLE': str(directory / 'role-id'), 'SECRET': str(directory / 'secret-id'),
              'TEMPLATE': str(directory / 'identity.ctmpl'), 'CANDIDATE': str(directory / 'candidate.json'),
              'COMMAND': ['/usr/bin/python3', str(directory / 'identity-runtime.py'), 'refresh', '--directory', str(directory), '--agent']}
    for key, value in values.items():
        agent = agent.replace(key, json.dumps(value))
    write(directory / 'agent.hcl', agent)
    bao = shutil.which('bao')
    if not bao:
        raise ValueError('OpenBao CLI is not installed')
    unit = f'''[Unit]
Description=Laplace monorepo scoped browser credential refresh
After=network-online.target
Wants=network-online.target
[Service]
Type=simple
UMask=0077
ExecStartPre=/usr/bin/rm -f {directory}/candidate.json
ExecStart={bao} agent -config={directory}/agent.hcl
Restart=on-failure
RestartSec=15
NoNewPrivileges=true
ProtectHome=true
PrivateTmp=true
[Install]
WantedBy=multi-user.target
'''
    write(Path('/etc/systemd/system') / SERVICE, unit, 0o644)
    dropin = Path('/etc/systemd/system') / (config['api_service'] + '.d')
    dropin.mkdir(exist_ok=True)
    write(dropin / '90-managed-identity.conf', '[Unit]\nWants=' + SERVICE + '\nAfter=network-online.target\n'
          '[Service]\nEnvironmentFile=-' + str(directory / 'identity.env') + '\n', 0o644)
    run(['systemctl', 'daemon-reload'])
    # Stage from an independently authenticated read. Subsequent changes are
    # rendered and applied by Agent, which renews its own token in memory.
    write(directory / 'candidate.json', json.dumps(record))
    refresh(config, directory)
    # Force a fresh Agent fetch while the last verified serving file remains in
    # place. An installer-authenticated read is not evidence of Agent operation.
    (directory / 'candidate.json').unlink()
    run(['systemctl', 'enable', '--now', SERVICE])
    for attempt in range(30):
        observed = json.loads((directory / 'receipt.json').read_text())
        if observed.get('source') == 'openbao-agent':
            break
        time.sleep(1)
    else:
        raise RuntimeError('Agent did not independently render and verify the identity; inspect its journal')
    run(['systemctl', 'is-active', '--quiet', SERVICE])
    if source.resolve() != (directory / 'enrollment.json').resolve():
        source.unlink()
    print('Installed ' + SERVICE + '; source enrollment consumed; human login is not used by the service.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=('enroll', 'install', 'refresh', 'verify'))
    parser.add_argument('--configuration', default=str(Path(__file__).parents[1] / 'identity-environments.json'))
    parser.add_argument('--environment', choices=('development', 'staging', 'production'))
    parser.add_argument('--enrollment')
    parser.add_argument('--directory', default='/etc/laplace-mono/identity')
    parser.add_argument('--api-url', default='http://127.0.0.1:5187')
    parser.add_argument('--api-service', default='laplace-api.service')
    parser.add_argument('--lock', default='/run/lock/laplace/host-resource.lock')
    parser.add_argument('--agent', action='store_true', help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.operation == 'enroll':
        if not args.environment or not args.enrollment:
            parser.error('enroll requires --environment and --enrollment')
        enroll(args)
    elif args.operation == 'install':
        install(args)
    else:
        directory = Path(args.directory)
        config = json.loads((directory / 'enrollment.json').read_text())
        if args.operation == 'refresh':
            refresh(config, directory, 'openbao-agent' if args.agent else 'manual')
        else:
            if directory.stat().st_uid != 0 or directory.stat().st_mode & 0o777 != 0o700:
                raise ValueError('Identity directory permissions differ from root-only 0700')
            for name in ('enrollment.json', 'role-id', 'secret-id', 'identity.env', 'candidate.json', 'agent.hcl'):
                info = (directory / name).lstat()
                if (directory / name).is_symlink() or info.st_uid != 0 or info.st_mode & 0o777 != 0o600:
                    raise ValueError('Identity file permissions differ from root-only 0600: ' + name)
            run(['systemctl', 'is-enabled', '--quiet', SERVICE])
            run(['systemctl', 'is-active', '--quiet', SERVICE])
            record = verify_access(config)
            verify_application(config, record)
            print(json.dumps({'host': config['host'], 'scoped_access_and_application': 'passed',
                              'google_client_id': record['google_client_id']}))


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        # Never include a provider response, credential value or subprocess output.
        raise SystemExit('Browser identity operation failed: ' + str(error) if isinstance(error, (RuntimeError, ValueError))
                         else 'Browser identity operation failed: ' + type(error).__name__) from None
