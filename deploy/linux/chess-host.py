#!/usr/bin/env python3
"""Enroll or install the monorepo chess controller and its OpenBao-rendered credentials."""
import argparse
import base64
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import pwd
import re
import shutil
import secrets
import socket
import subprocess
import sys
import time
import urllib.request
import urllib.error
import urllib.parse

sys.dont_write_bytecode = True
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
    render_match_engines(c, record)
    identity.write(directory / 'render-receipt.json', json.dumps({'source': 'openbao-agent', 'rendered_at': time.time(), 'fields': sorted(c['secret_files'])}))


def render_match_engines(c, record):
    selection = c.get('match_engines')
    if not selection:
        return
    connector = Path(selection['connector'])
    directory = Path(selection['directory'])
    service = c.get('api_service', '')
    if (not connector.is_absolute() or not connector.is_file() or not directory.is_absolute()
            or directory == directory.parent or not re.fullmatch(r'[a-zA-Z0-9_-]+\.service', service)):
        raise ValueError('Match engines require installed connector, private directory and API service declarations.')
    user = identity.run(['systemctl', 'show', service, '--property=User', '--value']).strip()
    if not user or user == 'root':
        raise ValueError('Match engines require an unprivileged API service.')
    account = pwd.getpwnam(user)
    profiles = {}
    for name, declared in selection['profiles'].items():
        field = declared['token_field']
        endpoint = urllib.parse.urlsplit(declared['endpoint'])
        if (not re.fullmatch(r'[a-zA-Z0-9_-]+', name) or not re.fullmatch(r'(uci_[a-z0-9_]+|[a-z0-9_]+_uci)_token', field)
                or endpoint.scheme != 'wss' or not endpoint.hostname or endpoint.username or endpoint.password
                or endpoint.query or endpoint.fragment or not re.fullmatch(r'[a-f0-9]{64}', record.get(field, ''))):
            raise ValueError('Invalid authenticated match engine profile: ' + name)
        profile = {key: declared[key] for key in ['label', 'implementation', 'environment', 'knowledge', 'endpoint']}
        profile.update(transport='websocket', tokenFile=str(directory / (name + '.token')))
        profiles[name] = profile
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    os.chown(directory, account.pw_uid, account.pw_gid)
    os.chmod(directory, 0o700)
    for name, declared in selection['profiles'].items():
        path = directory / (name + '.token')
        identity.write(path, record[declared['token_field']], 0o600)
        os.chown(path, account.pw_uid, account.pw_gid)
    path = directory / 'connector.json'
    identity.write(path, json.dumps({'version': 1, 'profiles': profiles}, indent=2), 0o600)
    os.chown(path, account.pw_uid, account.pw_gid)
    identity.run(['runuser', '-u', user, '--', 'test', '-x', str(connector)])


def match_engine_environment(selection):
    return ('[Service]\n'
        + 'Environment=' + json.dumps('LAPLACE_CHESS_UCI_CONFIG=' + str(Path(selection['directory']) / 'connector.json')) + '\n'
        + 'Environment=' + json.dumps('LAPLACE_CHESS_UCI_CONNECTOR=' + selection['connector']) + '\n')


def configure_match_engines(c, source, engines):
    private = Path(c['directory']) / 'agent'
    if (os.geteuid() != 0 or c['host'] != socket.gethostname().lower().split('.')[0]
            or Path(source).resolve() != (private / 'installation.json').resolve()):
        raise ValueError('Run match-engine setup as root on the declared host using its retained installation.')
    candidate = dict(c, match_engines=json.loads(Path(engines).read_text()))
    enrollment = json.loads((private / 'enrollment.json').read_text())
    token = identity.login(enrollment)
    try:
        record = identity.request(enrollment, c['record'], token)['data']['data']
    finally:
        identity.request(enrollment, 'auth/token/revoke-self', token, {})
    render_match_engines(candidate, record)
    selection = candidate['match_engines']
    user = identity.run(['systemctl', 'show', c['api_service'], '--property=User', '--value']).strip()
    for name in selection['profiles']:
        identity.run(['runuser', '-u', user, '--', selection['connector'], 'verify', '--configuration',
            str(Path(selection['directory']) / 'connector.json'), '--profile', name])
    dropin = Path('/etc/systemd/system') / (c['api_service'] + '.d') / '96-chess-engines.conf'
    previous = {p: p.read_bytes() if p.exists() else None for p in [Path(source), private / 'chess-host.py', dropin]}
    try:
        identity.write(source, json.dumps(candidate, indent=2))
        identity.write(private / 'chess-host.py', Path(__file__).read_text(), 0o700)
        dropin.parent.mkdir(exist_ok=True)
        identity.write(dropin, match_engine_environment(selection), 0o644)
        identity.run(['systemctl', 'daemon-reload'])
        identity.run(['systemctl', 'restart', c['api_service']])
        identity.run(['systemctl', 'is-active', '--quiet', c['api_service']])
    except BaseException:
        for path, content in previous.items():
            if content is None:
                path.unlink(missing_ok=True)
            else:
                identity.write(path, content, 0o644 if path == dropin else 0o700 if path.name == 'chess-host.py' else 0o600)
        identity.run(['systemctl', 'daemon-reload'])
        identity.run(['systemctl', 'restart', c['api_service']])
        raise
    print(json.dumps({'host': c['host'], 'profiles': sorted(selection['profiles']),
        'credentials': 'installed-approle', 'configuration': str(Path(selection['directory']) / 'connector.json')}))


def export_certificate(c, private):
    export = c['certificate_export']
    enrollment = json.loads((private / 'certificate-enrollment.json').read_text())
    if enrollment['record'] != export['record'] or enrollment['address'] != c['address']:
        raise ValueError('Certificate publishing identity does not match the declared record.')
    certificate = Path(export['certificate'])
    match = identity.run(['openssl', 'x509', '-in', str(certificate), '-noout', '-checkhost', export['hostname']])
    if 'does match certificate' not in match:
        raise ValueError('The declared certificate does not cover the desktop UCI hostname.')
    identity.run(['openssl', 'x509', '-in', str(certificate), '-noout', '-checkend', '86400'])
    fingerprint = hashlib.sha256(certificate.read_bytes()).hexdigest()
    token = identity.login(enrollment)
    try:
        try:
            previous = identity.request(enrollment, export['record'], token)['data']['data']
        except RuntimeError as error:
            if 'returned HTTP 404' not in str(error):
                raise
            previous = {}
        if previous.get('certificate_sha256') == fingerprint:
            return
        password = secrets.token_hex(32)
        result = subprocess.run(['openssl', 'pkcs12', '-export', '-in', str(certificate), '-inkey', export['key'], '-passout', 'stdin'],
            input=(password + '\n').encode(), capture_output=True, timeout=30)
        if result.returncode:
            raise RuntimeError('The declared TLS certificate and private key could not be exported.')
        identity.request(enrollment, export['record'], token, {'data': {
            'pfx_base64': base64.b64encode(result.stdout).decode(), 'password': password,
            'certificate_sha256': fingerprint, 'hostname': export['hostname']}})
        print('Published the declared TLS certificate to its scoped desktop record.')
    finally:
        identity.request(enrollment, 'auth/token/revoke-self', token, {})


def arm_uci_delivery(c, units):
    """Ask later deliveries to re-render connector.json without a hand edit of /etc."""
    dependency = c.get('uci_setup')
    trigger = c.get('restart_trigger')
    if not isinstance(dependency, dict) or not trigger:
        return None
    declared_path = Path(dependency.get('configuration', ''))
    if not declared_path.is_file():
        return None
    declared = json.loads(declared_path.read_text())
    configuration = Path(declared['configuration_directory'])
    installed_setup = configuration / 'setup.py'
    installed_json = configuration / 'installation.json'
    if not installed_setup.is_file() or not installed_json.is_file():
        return None
    refresh_trigger = str(Path(trigger).parent / 'refresh-uci')
    if not re.fullmatch(r'/[a-zA-Z0-9_./-]+', refresh_trigger):
        raise ValueError('Invalid UCI refresh trigger.')
    service = declared['service']
    if not re.fullmatch(r'[a-z][a-z0-9-]+', service):
        raise ValueError('Invalid UCI service identity.')
    for path in (installed_setup, installed_json):
        if not re.fullmatch(r'/[a-zA-Z0-9_./-]+', str(path)):
            raise ValueError('Invalid installed UCI path: ' + str(path))
    unit = 'laplace-mono-uci-render'
    command = '/usr/bin/python3 ' + str(installed_setup) + ' --refresh ' + str(installed_json)
    identity.write(units / (unit + '.service'),
        '[Unit]\nDescription=Re-render the Laplace UCI connector after delivery\n[Service]\nType=oneshot\nExecStart=' + command + '\n', 0o644)
    identity.write(units / (unit + '.path'),
        '[Unit]\nDescription=Laplace UCI connector delivery render request\n[Path]\nPathChanged=' + refresh_trigger + '\n[Install]\nWantedBy=multi-user.target\n', 0o644)
    identity.write(configuration / 'delivery.json', json.dumps({
        'service': service + '.service', 'trigger': refresh_trigger, 'unit': unit + '.path'}), 0o644)
    return unit


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
    supplied_enrollment = Path(enrollment_path)
    if not supplied_enrollment.exists():
        enrollment_path = str(directory / 'agent/enrollment.json')
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
    if c.get('certificate_export'):
        external = Path(c['certificate_export']['enrollment'])
        retained = private / 'certificate-enrollment.json'
        if external.exists():
            identity.write(retained, external.read_bytes())
        export_certificate(c, private)
        units = Path('/etc/systemd/system')
        certificate_service = c['service'] + '-certificate'
        command = f'/usr/bin/python3 {private}/chess-host.py export-certificate --configuration {private}/installation.json'
        identity.write(units / (certificate_service + '.service'), '[Unit]\nDescription=Publish the configured renewed desktop certificate to OpenBao\n[Service]\nType=oneshot\nUMask=0077\nExecStart=' + command + '\n', 0o644)
        identity.write(units / (certificate_service + '.timer'), '[Unit]\nDescription=Check for a renewed desktop certificate\n[Timer]\nOnBootSec=5min\nOnUnitActiveSec=1h\nPersistent=true\n[Install]\nWantedBy=timers.target\n', 0o644)
        identity.run(['systemctl', 'daemon-reload'])
        identity.run(['systemctl', 'enable', '--now', certificate_service + '.timer'])
        if external.exists() and external.resolve() != retained.resolve():
            external.unlink()
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
        trigger = c.get('restart_trigger')
        if not trigger or not re.fullmatch(r'/[a-zA-Z0-9_./-]+', trigger) or not Path(trigger).parent.is_dir():
            raise ValueError('Declare a restart_trigger inside the installed runner lock directory.')
        restart = c['service'] + '-restart'
        identity.write(units / (restart + '.service'), '[Unit]\nDescription=Restart the declared monorepo chess controller after delivery\n[Service]\nType=oneshot\nExecStart=/usr/bin/systemctl restart ' + c['service'] + '.service\n', 0o644)
        identity.write(units / (restart + '.path'), '[Unit]\nDescription=Monorepo chess delivery restart request\n[Path]\nPathChanged=' + trigger + '\n[Install]\nWantedBy=multi-user.target\n', 0o644)
        identity.write(directory / 'deployment.json', json.dumps({'service': c['service'] + '.service', 'trigger': trigger}), 0o644)
    uci_render = arm_uci_delivery(c, units)
    identity.run(['systemctl', 'daemon-reload'])
    if c.get('command'):
        identity.run(['systemctl', 'enable', '--now', restart + '.path'])
    if uci_render:
        identity.run(['systemctl', 'enable', '--now', uci_render + '.path'])
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
        if c.get('match_engines'):
            identity.write(dropin / '96-chess-engines.conf', match_engine_environment(c['match_engines']), 0o644)
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


def configure_routing(c, source, routing_path):
    if os.geteuid() != 0 or c['host'] != socket.gethostname().lower().split('.')[0]:
        raise ValueError('Run routing setup as root on its declared host.')
    directory = Path(c['directory'])
    if Path(source).resolve() != (directory / 'agent/installation.json').resolve():
        raise ValueError('Routing setup requires the retained installation declaration, so later setup preserves the selected routes.')
    deployed = directory / 'controller.json'
    previous = json.loads(deployed.read_text())
    candidate = dict(previous, version=2, profiles={}, routing=json.loads(Path(routing_path).read_text()))
    temporary = directory / 'controller.pending.json'
    old_installation = Path(source).read_bytes()
    old_controller = deployed.read_bytes()
    token = Path(previous['tokenFile']).read_text().strip()
    origin = c['health_url'].rsplit('/', 1)[0]

    def request(route, body=None):
        data = None if body is None else json.dumps(body).encode()
        req = urllib.request.Request(origin + '/' + route, data=data,
            headers={'Authorization': 'Bearer ' + token, 'Content-Type': 'application/json'})
        with urllib.request.urlopen(req, timeout=10) as response:
            return json.load(response)

    def select(selected, revision):
        return request('selection', dict(profile=selected.get('profile'), readSource=selected.get('readSource'),
            recordingTarget=selected.get('recordingTarget'), expectedRevision=revision, actor='installation-routing'))

    def ready(selected):
        for _ in range(120):
            try:
                current = request('status')
                if current['selection']['revision'] != selected['selection']['revision']:
                    raise ValueError('Selection changed concurrently; preserving the operator selection.')
                if current['phase'] == 'failed':
                    raise ValueError(current.get('error') or 'Controller activation failed.')
                if current['phase'] == 'off' and selected['selection']['profile'] is None:
                    return current
                if current['phase'] == 'running' and (current.get('bot') or {}).get('connected'):
                    return current
            except (OSError, urllib.error.URLError):
                pass
            time.sleep(1)
        raise RuntimeError('Controller did not reach its selected routing state.')

    try:
        identity.write(temporary, json.dumps(candidate, indent=2), 0o644)
        environment = [k + '=' + v for k, v in c['environment'].items()]
        identity.run(['runuser', '-u', c['user'], '--', 'env', *environment, *c['command'],
            '--verify-routing', str(temporary), '--selection', previous['stateFile']])
        original = request('status')['selection']
        restored = dict(original)
        if original.get('profile') and not original.get('readSource'):
            mapping = candidate['routing'].get('legacySelections', {}).get(original['profile'])
            if not mapping:
                raise ValueError('Declare the original read/write destination before upgrading this combined profile.')
            restored.update(mapping)
        off = select({}, original['revision'])
        print('Draining the shared bot before applying independent routing...', flush=True)
        for _ in range(1800):
            status = request('status')
            if status['selection']['revision'] != off['selection']['revision']:
                raise ValueError('Selection changed during drain; no routing configuration was changed.')
            if status['phase'] == 'off':
                break
            time.sleep(1)
        else:
            select(original, off['selection']['revision'])
            raise RuntimeError('Games are still active; original selection restored and no routing configuration changed.')
        try:
            c['public_files']['controller.json'] = candidate
            identity.write(deployed, json.dumps(candidate, indent=2), 0o644)
            identity.write(Path(source), json.dumps(c, indent=2))
            identity.run(['systemctl', 'restart', c['service'] + '.service'])
            ready(off)
            selected = select(restored, off['selection']['revision'])
            status = ready(selected)
        except Exception:
            # Roll back only our own selection. A concurrent operator decision has priority.
            try:
                current = request('status')
            except (OSError, urllib.error.URLError):
                current = None
            if current and current['selection'].get('actor') != 'installation-routing':
                raise
            identity.run(['systemctl', 'stop', c['service'] + '.service'])
            identity.write(deployed, old_controller, 0o644)
            identity.write(Path(source), old_installation)
            state = Path(previous['stateFile'])
            reverted = dict(off['selection'], revision=(current or off)['selection']['revision'])
            identity.write(state, json.dumps(reverted))
            account = pwd.getpwnam(c['user'])
            os.chown(state, account.pw_uid, account.pw_gid)
            identity.run(['systemctl', 'start', c['service'] + '.service'])
            ready({'selection': reverted})
            ready(select(original, reverted['revision']))
            raise
        receipt = {'verified_utc': time.time(), 'controller': status['controller'], 'phase': status['phase'],
            'selection': status['selection'], 'routing': status['routing']}
        identity.write(Path(c['state']) / 'routing-installation.json', json.dumps(receipt, indent=2))
        print(json.dumps(receipt))
    finally:
        temporary.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=['enroll', 'install', 'render', 'export-certificate', 'configure-routing', 'configure-match-engines'])
    parser.add_argument('--configuration', required=True)
    parser.add_argument('--enrollment')
    parser.add_argument('--routing')
    parser.add_argument('--engines')
    args = parser.parse_args()
    c = config(args.configuration)
    if args.operation == 'configure-match-engines':
        if not args.engines:
            parser.error('Match-engine setup requires --engines <declaration.json>.')
        configure_match_engines(c, args.configuration, args.engines)
    elif args.operation == 'configure-routing':
        if not args.routing:
            parser.error('Routing setup requires --routing <declaration.json>.')
        configure_routing(c, args.configuration, args.routing)
    elif args.operation == 'enroll':
        if not args.enrollment:
            parser.error('Enrollment needs an output path.')
        enroll(c, args.enrollment)
    elif args.operation == 'install':
        install(c, args.configuration, args.enrollment or str(Path(c['directory']) / 'agent/enrollment.json'))
    elif args.operation == 'export-certificate':
        export_certificate(c, Path(c['directory']) / 'agent')
    else:
        render(c, Path(c['directory']) / 'agent')


if __name__ == '__main__':
    main()
