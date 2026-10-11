"""Enroll Google web clients in per-environment OpenBao records through an operator session.

The input is one JSON object on stdin: configuration and clients (downloaded web objects).
This command runs on the operator's authenticated OpenBao host. Secrets never enter argv or output.
"""
import json
import os
import re
import subprocess
import sys


def bao(arguments, payload=None, missing=False):
    result = subprocess.run(['bao', *arguments], input=None if payload is None else
                            (payload if isinstance(payload, str) else json.dumps(payload)),
                            text=True, capture_output=True, timeout=30)
    if result.returncode:
        if missing and 'No value found at ' in result.stderr:
            return None
        raise RuntimeError('OpenBao operation failed: ' + ' '.join(arguments[:2]))
    return json.loads(result.stdout) if '-format=json' in arguments else result.stdout


def main():
    request = json.load(sys.stdin)
    config, clients = request['configuration'], request['clients']
    os.environ['BAO_ADDR'] = config['bao_address']
    selected = []
    for environment in config['environments']:
        if not all(re.fullmatch(r'[a-zA-Z0-9_/-]+', environment[k]) for k in ('record', 'role')):
            raise ValueError('Invalid environment identity reference')
        matches = [c for c in clients if set(c.get('redirect_uris', [])) == set(environment['callbacks'])]
        if len(matches) != 1:
            raise ValueError('Select one client with the exact callback set for ' + environment['name'])
        client = matches[0]
        if client.get('project_id') != config['google_project']:
            raise ValueError('Client belongs to a different project')
        if not re.fullmatch(r'[a-zA-Z0-9_.-]+\.apps\.googleusercontent\.com', client.get('client_id', '')):
            raise ValueError('Invalid Google client identifier')
        if not re.fullmatch(r'[a-zA-Z0-9_-]+', client.get('client_secret', '')):
            raise ValueError('Missing or invalid Google client secret')
        selected.append((environment, client))
    # Read the existing Microsoft registration without changing its credential.
    shared = bao(['kv', 'get', '-format=json', '-mount=kv', 'shared/laplace-auth'])['data']['data']
    results = []
    for environment, client in selected:
        path = environment['record']
        previous = bao(['kv', 'get', '-format=json', '-mount=kv', path], missing=True)
        record = dict(previous['data']['data']) if previous else {
            k: v for k, v in shared.items() if k.startswith('microsoft_')}
        record.update(google_client_id=client['client_id'], google_client_secret=client['client_secret'],
                      google_project_id=client['project_id'], implementation='laplace-mono',
                      environment=environment['name'], redirect_uris=environment['callbacks'])
        version = previous['data']['metadata']['version'] if previous else 0
        changed = previous is None or record != previous['data']['data']
        if changed:
            bao(['kv', 'put', '-format=json', '-mount=kv', '-cas=' + str(version), path, '-'], record)
        observed = bao(['kv', 'get', '-format=json', '-mount=kv', path])['data']
        if observed['data'] != record:
            raise RuntimeError('Stored identity differs from the selected client')
        results.append({'environment': environment['name'], 'record': 'kv/' + path,
                        'version': observed['metadata']['version'], 'changed': changed,
                        'client_id': client['client_id'], 'callbacks': environment['callbacks']})
    print(json.dumps({'stored_and_read_back': results}, indent=2))


if __name__ == '__main__':
    try:
        main()
    except (ValueError, KeyError, OSError, RuntimeError, subprocess.TimeoutExpired) as error:
        raise SystemExit('Google client enrollment failed: ' + str(error)) from None
