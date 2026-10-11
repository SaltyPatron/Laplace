#!/usr/bin/env python3
"""Enroll or install the monorepo chess controller and its OpenBao-rendered credentials."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import pwd
import re
import shutil
import socket
import subprocess
import time
import urllib.request

spec = importlib.util.spec_from_file_location('identity_runtime', Path(__file__).with_name('identity-runtime.py'))
identity = importlib.util.module_from_spec(spec)
spec.loader.exec_module(identity)


def config(path):
    c = json.loads(Path(path).read_text())
    for key in ['user', 'service', 'role']:
        if not re.fullmatch(r'[a-z][a-z0-9-]+', c[key]):
            raise ValueError('Invalid chess installation identity: ' + key)
    if not c['address'].startswith('https://') or not re.fullmatch(r'kv/data/[a-zA-Z0-9_/-]+', c['record']):
        raise ValueError('Declare an HTTPS OpenBao address and one scoped record.')
    return c


def enroll(c, output):
    os.environ['BAO_ADDR'] = c['address']
    policy = 'path ' + json.dumps(c['record']) + ' { capabilities = ["read"] }\n'
    for route, capability in [('auth/token/lookup-self', 'read'), ('auth/token/renew-self', 'update'), ('auth/token/revoke-self', 'update')]:
        policy += 'path ' + json.dumps(route) + ' { capabilities = [' + json.dumps(capability) + '] }\n'
    identity.run(['bao', 'policy', 'write', c['role'], '-'], policy)
    identity.run(['bao', 'write', 'auth/approle/role/' + c['role'], 'token_policies=' + c['role'],
        'token_no_default_policy=true', 'token_ttl=5m', 'token_max_ttl=10m', 'secret_id_ttl=0', 'secret_id_num_uses=0'])
    enrollment = {'address': c['address'], 'record': c['record'], 'host': c['host'],
        'role_id': json.loads(identity.run(['bao', 'read', '-format=json', 'auth/approle/role/' + c['role'] + '/role-id']))['data']['role_id'],
        'secret_id': json.loads(identity.run(['bao', 'write', '-f', '-format=json', 'auth/approle/role/' + c['role'] + '/secret-id']))['data']['secret_id']}
    token = identity.login(enrollment)
    try:
        record = identity.request(enrollment, c['record'], token)['data']['data']
        if any(field not in record for field in c['secret_files']):
            raise ValueError('The declared workload record does not contain every required credential.')
        for forbidden in ['kv/data/shared/lichess', 'sys/policies/acl/' + c['role']]:
            try:
                identity.request(enrollment, forbidden, token)
            except RuntimeError as error:
                if 'returned HTTP 403' not in str(error):
                    raise
            else:
                raise ValueError('Workload identity can read outside its declared credential record.')
    finally:
        identity.request(enrollment, 'auth/token/revoke-self', token, {})
    identity.write(output, json.dumps(enrollment, indent=2))
    print('Enrolled scoped workload: ' + c['role'])


def render(c, directory):
    record = json.loads((directory / 'candidate.json').read_text())
    for field, destination in c['secret_files'].items():
        value = record.get(field)
        if not isinstance(value, str) or not value or '\x00' in value:
            raise ValueError('Credential field missing or invalid: ' + field)
        target = Path(destination['path'])
        user = destination.get('user')
        if destination.get('service'):
            if not re.fullmatch(r'[a-zA-Z0-9_-]+\.service', destination['service']):
                raise ValueError('Invalid credential consumer service.')
            user = identity.run(['systemctl', 'show', destination['service'], '--property=User', '--value']).strip()
            if not user or user == 'root':
                raise ValueError('Credential consumer must declare an unprivileged service user.')
        account = pwd.getpwnam(user)
        target.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        os.chown(target.parent, account.pw_uid, account.pw_gid)
        identity.write(target, value, 0o600)
        os.chown(target, account.pw_uid, account.pw_gid)
        identity.run(['runuser', '-u', user, '--', 'test', '-r', str(target)])
    identity.write(directory / 'render-receipt.json', json.dumps({'source': 'openbao-agent', 'rendered_at': time.time(), 'fields': sorted(c['secret_files'])}))


def install(c, source, enrollment_path):
    if os.geteuid() != 0:
        raise ValueError('Run the host installation with sudo.')
    if c['host'] != socket.gethostname().lower().split('.')[0]:
        raise ValueError('Chess installation belongs to another host.')
    if c.get('uci_setup'):
        dependency = c['uci_setup']
        command = ['/usr/bin/python3', dependency['script'], '--installation', dependency['configuration']]
        if Path(dependency.get('enrollment', '')).is_file():
            command += ['--enrollment', dependency['enrollment']]
        identity.run(command)
    directory, state = Path(c['directory']), Path(c['state'])
    for path in [directory, state]:
        if not path.is_absolute() or path.is_symlink() or '..' in path.parts or len(path.parts) < 3:
            raise ValueError('Invalid installation directory: ' + str(path))
    enrollment = json.loads(Path(enrollment_path).read_text())
    if any(enrollment[k] != c[k] for k in ['host', 'record', 'address']):
        raise ValueError('Workload enrollment does not match this installation.')
    try:
        account = pwd.getpwnam(c['user'])
    except KeyError:
        identity.run(['useradd', '--system', '--home-dir', str(state), '--shell', '/usr/sbin/nologin', c['user']])
        account = pwd.getpwnam(c['user'])
    directory.mkdir(parents=True, exist_ok=True, mode=0o755)
    state.mkdir(parents=True, exist_ok=True, mode=0o700)
    os.chown(state, account.pw_uid, account.pw_gid)
    state.chmod(0o700)
    private = directory / 'agent'
    private.mkdir(exist_ok=True, mode=0o700)
    private.chmod(0o700)
    identity.write(private / 'enrollment.json', json.dumps(enrollment))
    identity.write(private / 'installation.json', json.dumps(c, indent=2))
    identity.write(private / 'role-id', enrollment['role_id'])
    identity.write(private / 'secret-id', enrollment['secret_id'])
    for name in ['chess-host.py', 'identity-runtime.py']:
        identity.write(private / name, Path(__file__).with_name(name).read_text(), 0o700)
    for filename, content in c['public_files'].items():
        if Path(filename).name != filename:
            raise ValueError('Public configuration must remain inside the installation directory.')
        identity.write(directory / filename, content if isinstance(content, str) else json.dumps(content, indent=2), 0o644)
    j = json.dumps
    template = '{{ with secret "' + c['record'] + '" }}{{ .Data.data | toJSON }}{{ end }}'
    agent = 'vault { address = ' + j(c['address']) + ' }\nauto_auth { method "approle" { config = {\n'
    agent += 'role_id_file_path = ' + j(str(private / 'role-id')) + '\nsecret_id_file_path = ' + j(str(private / 'secret-id')) + '\nremove_secret_id_file_after_reading = false\n} } }\n'
    agent += 'template_config { static_secret_render_interval = "1m"\nexit_on_retry_failure = true }\n'
    agent += 'template { contents = ' + j(template) + '\ndestination = ' + j(str(private / 'candidate.json')) + '\nperms = "0600"\nbackup = false\nerror_on_missing_key = true\n'
    agent += 'exec { command = ' + j(['/usr/bin/python3', str(private / 'chess-host.py'), 'render', '--configuration', str(private / 'installation.json')]) + '\ntimeout = "30s"\n}\n}\n'
    identity.write(private / 'agent.hcl', agent)
    bao = shutil.which('bao')
    if not bao:
        raise ValueError('Install OpenBao CLI before chess setup.')
    units = Path('/etc/systemd/system')
    credentials = c['service'] + '-credentials.service'
    identity.write(units / credentials, f'[Unit]\nDescription=Monorepo chess scoped OpenBao credentials\nAfter=network-online.target\nWants=network-online.target\n[Service]\nUMask=0077\nExecStart={bao} agent -config={private}/agent.hcl\nRestart=on-failure\nRestartSec=5\nNoNewPrivileges=true\n[Install]\nWantedBy=multi-user.target\n', 0o644)
    # Unit arguments are installation data. Only simple absolute executable/file paths
    # are accepted; no shell or credential interpolation is involved.
    for argument in c.get('command', []):
        if not re.fullmatch(r'/[a-zA-Z0-9_./-]+', argument):
            raise ValueError('Runtime command must contain absolute executable/file paths.')
    environment = ''.join('Environment=' + j(k + '=' + v) + '\n' for k, v in c['environment'].items())
    files = ''.join('EnvironmentFile=' + p + '\n' for p in c.get('environment_files', []))
    unit = f'[Unit]\nDescription=Laplace monorepo Lichess profile controller\nRequires={credentials}\nAfter={credentials}\n[Service]\nUser={c["user"]}\nGroup={c["user"]}\nUMask=0077\nWorkingDirectory={state}\n{environment}{files}ExecStart=' + ' '.join(c.get('command', []))
    unit += f'\nRestart=on-failure\nRestartSec=5\nTimeoutStopSec=40\nNoNewPrivileges=true\nProtectSystem=strict\nProtectHome=true\nReadWritePaths={state}\n[Install]\nWantedBy=multi-user.target\n'
    if c.get('command'):
        identity.write(units / (c['service'] + '.service'), unit, 0o644)
    identity.run(['systemctl', 'daemon-reload'])
    identity.run(['systemctl', 'stop', credentials])
    (private / 'candidate.json').unlink(missing_ok=True)
    before = time.time()
    identity.run(['systemctl', 'enable', '--now', credentials])
    identity.run(['systemctl', 'restart', credentials])
    # Force an independent workload fetch/render instead of accepting yesterday's file.
    for _ in range(60):
        receipt = private / 'render-receipt.json'
        if receipt.exists() and json.loads(receipt.read_text())['rendered_at'] >= before:
            break
        time.sleep(1)
    else:
        raise RuntimeError('OpenBao Agent did not independently render the chess credentials.')
    if c.get('command'):
        identity.run(['systemctl', 'enable', c['service'] + '.service'])
        identity.run(['systemctl', 'restart', c['service'] + '.service'])
    for _ in range(20):
        try:
            token = Path(c['health_token_file']).read_text().strip()
            request = urllib.request.Request(c['health_url'], headers={'Authorization': 'Bearer ' + token})
            with urllib.request.urlopen(request, timeout=2) as response:
                status = json.load(response)
                if response.status == 200:
                    break
        except OSError:
            time.sleep(.5)
    else:
        raise RuntimeError('Installed controller did not answer its authenticated status request.')
    if c.get('api_service'):
        if not re.fullmatch(r'[a-zA-Z0-9_-]+\.service', c['api_service']):
            raise ValueError('Invalid API service identity.')
        dropin = units / (c['api_service'] + '.d')
        dropin.mkdir(exist_ok=True)
        identity.write(dropin / '95-lichess-control.conf', '[Service]\nEnvironment=LAPLACE_LICHESS_CONTROL_CLIENT=' + str(directory / 'api-client.json') + '\n', 0o644)
        identity.run(['systemctl', 'daemon-reload'])
        identity.run(['systemctl', 'restart', c['api_service']])
    if c.get('nginx_server_file'):
        site = Path(c['nginx_server_file'])
        snippet = Path('/etc/nginx/laplace-mono/lichess-control.location.conf')
        snippet.parent.mkdir(exist_ok=True, mode=0o755)
        original = site.read_text()
        old_snippet = snippet.read_bytes() if snippet.exists() else None
        upstream = c['health_url'].removesuffix('/status')
        if not re.fullmatch(r'http://127\.0\.0\.1:[0-9]+', upstream):
            raise ValueError('Controller ingress requires a loopback upstream.')
        try:
            identity.write(snippet, 'location /internal/laplace-mono/lichess/ {\n    allow 100.64.0.0/10;\n    deny all;\n    proxy_pass ' + upstream + '/;\n    proxy_http_version 1.1;\n    proxy_buffering off;\n}\n', 0o644)
            include = '    include /etc/nginx/laplace-mono/*.location.conf;'
            if include.strip() not in original:
                updated, count = re.subn(r'(server_name[^;]+;)', r'\1\n' + include, original)
                if count != 1:
                    raise ValueError('Expected one declared server for controller ingress.')
                identity.write(site, updated, 0o644)
            identity.run(['nginx', '-t'])
            identity.run(['systemctl', 'reload', 'nginx'])
        except BaseException:
            identity.write(site, original, 0o644)
            if old_snippet is None:
                snippet.unlink(missing_ok=True)
            else:
                identity.write(snippet, old_snippet, 0o644)
            raise
    if Path(enrollment_path).resolve() != (private / 'enrollment.json').resolve():
        Path(enrollment_path).unlink()
    print(json.dumps({'host': c['host'], 'controller': status['controller'], 'phase': status['phase'], 'selection': status['selection'], 'credentials': 'openbao-agent'}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=['enroll', 'install', 'render'])
    parser.add_argument('--configuration', required=True)
    parser.add_argument('--enrollment')
    args = parser.parse_args()
    c = config(args.configuration)
    if args.operation == 'enroll':
        if not args.enrollment:
            parser.error('Enrollment needs an output path.')
        enroll(c, args.enrollment)
    elif args.operation == 'install':
        install(c, args.configuration, args.enrollment or str(Path(c['directory']) / 'agent/enrollment.json'))
    else:
        render(c, Path(c['directory']) / 'agent')


if __name__ == '__main__':
    main()
