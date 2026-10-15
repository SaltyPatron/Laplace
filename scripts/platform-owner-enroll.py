#!/usr/bin/env python3
"""Persist explicitly selected, previously authenticated installation owners privately."""
import argparse
import hashlib
import hmac
from datetime import datetime, timezone
import importlib.util
import json
import os
from pathlib import Path
import socket
import re
import sys
import tempfile

spec = importlib.util.spec_from_file_location('initial_enrollment', Path(__file__).with_name('platform-access-enroll.py'))
initial = importlib.util.module_from_spec(spec)
spec.loader.exec_module(initial)
spec = importlib.util.spec_from_file_location('owner_transport', Path(__file__).resolve().parents[1] / 'deploy/linux/platform-owner-policy.py')
transport = importlib.util.module_from_spec(spec)
spec.loader.exec_module(transport)
OwnerError = initial.EnrollmentError
HOSTS = {'staging': 'hart-server', 'production': 'hart-prod'}


def validate_request(value):
    if not isinstance(value, dict) or value.get('kind') != 'persistent-installation-owners':
        raise OwnerError('An explicit persistent installation owner request is required.')
    target = value.get('target')
    if target not in HOSTS or value.get('host') != HOSTS[target]:
        raise OwnerError('The owner request must select its declared application host.')
    # The selector contract is shared; its fingerprint still includes the actual target.
    checked = dict(value)
    del checked['kind']
    if not isinstance(checked.get('selectors'), list):
        raise OwnerError('The owner request must select verified provider bindings and OAuth clients.')
    for selector in checked['selectors']:
        if (not isinstance(selector, dict) or set(selector) != {'provider', 'fingerprint_sha256', 'client_id_sha256'}
                or not isinstance(selector['client_id_sha256'], str) or not re.fullmatch('[0-9a-f]{64}', selector['client_id_sha256'])):
            raise OwnerError('Each owner selector must bind its independently reviewed OAuth client.')
    checked['selectors'] = [{k: v for k, v in s.items() if k != 'client_id_sha256'} for s in checked['selectors']]
    checked.update(target='staging', host='hart-server')
    initial.validate_request(checked)
    return value


def policy_from(request, bindings, clients):
    owners = []
    for provider, row in bindings.items():
        matches = [c for c in clients if c.get('provider') == provider]
        if (len(matches) != 1 or not isinstance(matches[0].get('clientId'), str)
                or not matches[0]['clientId'] or len(matches[0]['clientId']) > 4096):
            raise OwnerError('The serving OAuth client context is missing or ambiguous.')
        expected = next(s['client_id_sha256'] for s in request['selectors'] if s['provider'] == provider)
        if not hmac.compare_digest(expected, hashlib.sha256(matches[0]['clientId'].encode()).hexdigest()):
            raise OwnerError('The serving OAuth client differs from the reviewed owner declaration.')
        if row.get('role') != 'administrator' or row.get('event_count', 0) < 1:
            raise OwnerError('The selected owner must have an existing audited administrator grant.')
        verified = row.get('verified_at')
        try:
            stamp = datetime.fromisoformat(verified.replace('Z', '+00:00'))
            if stamp.tzinfo is None or stamp > datetime.now(timezone.utc):
                raise ValueError()
        except (AttributeError, TypeError, ValueError):
            raise OwnerError('The selected binding lacks a recorded authentication time.') from None
        owners.append({'provider': provider, 'issuer': row['issuer'], 'subject': row['subject'],
                       'client_id': matches[0]['clientId'], 'verified_at': stamp.isoformat()})
    policy = {'version': 1, 'owners': owners}
    validate_policy(policy)
    return policy


def validate_policy(policy):
    try:
        record = {'platform_owner_policy': policy}
        for owner in policy['owners']:
            key = owner['provider'] + '_client_id'
            if key in record and record[key] != owner['client_id']:
                raise ValueError()
            record[key] = owner['client_id']
        transport.environment(record)
    except (KeyError, TypeError, ValueError):
        raise OwnerError('The installation owner policy does not satisfy the runtime contract.') from None


def same_bindings(left, right):
    def keys(value):
        if not isinstance(value, dict) or value.get('version') != 1 or not isinstance(value.get('owners'), list):
            raise OwnerError('The existing private owner policy is invalid.')
        return sorted(tuple(o[k] for k in ('provider', 'issuer', 'subject', 'client_id')) for o in value['owners'])
    try:
        return keys(left) == keys(right)
    except (KeyError, TypeError):
        raise OwnerError('The existing private owner policy is invalid.') from None


def persist(path, policy):
    validate_policy(policy)
    initial.private_path(path.parent, directory=True)
    if path.exists() or path.is_symlink():
        initial.private_path(path)
        try:
            if path.stat().st_size > 65536:
                raise ValueError()
            previous = json.loads(path.read_text())
        except (OSError, ValueError):
            raise OwnerError('The existing private owner policy could not be read.') from None
        validate_policy(previous)
        if not same_bindings(previous, policy):
            raise OwnerError('An existing owner policy selects different bindings; it was preserved.')
        return 'existing-policy-verified'
    filename = None
    try:
        descriptor, filename = tempfile.mkstemp(prefix='.owners-', dir=path.parent)
        with os.fdopen(descriptor, 'w', encoding='utf-8') as output:
            os.chmod(filename, 0o600)
            json.dump(policy, output, separators=(',', ':'))
            output.write('\n')
            output.flush()
            os.fsync(output.fileno())
        # The workflow holds the host's exclusive maintenance reservation.
        os.replace(filename, path)
        if os.name == 'posix':
            descriptor = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(descriptor)
            finally:
                os.close(descriptor)
    except OSError:
        raise OwnerError('The private installation owner policy could not be persisted.') from None
    finally:
        if filename and os.path.exists(filename):
            os.unlink(filename)
    return 'policy-persisted'


def enroll(request, path, verify=False):
    validate_request(request)
    if socket.gethostname().split('.')[0].lower() != request['host']:
        raise OwnerError('The reviewed owner request belongs to another host.')
    api = initial.Api(request['origin'])
    status, capabilities = api.call('GET', '/v1/admin/access/capabilities')
    if status != 200 or not isinstance(capabilities, dict) or capabilities.get('persistentInstallationOwners') is not True:
        raise OwnerError('Deploy the runtime with persistent installation owner support first.')
    bindings = initial.selected_bindings(request, initial.binding_rows())
    status, document = initial.access.read_route(api.opener, api.base, 'clients', api.token)
    if status != 200:
        raise OwnerError('The serving OAuth client configuration could not be verified.')
    clients = initial.access.rows(document, 'clients', 100)
    for row in bindings.values():
        api.person(row)
    policy = policy_from(request, bindings, clients)
    if verify:
        initial.private_path(path)
        existing = json.loads(path.read_text())
        validate_policy(existing)
        if not same_bindings(existing, policy):
            raise OwnerError('The persisted owner bindings differ from the reviewed request.')
        for row in bindings.values():
            status, document = api.call('GET', '/v1/admin/access/people?search=' + row['user_id'])
            if status != 200 or not isinstance(document, dict) or not isinstance(document.get('people'), list):
                raise OwnerError('The serving owner authority lookup did not complete.')
            matched = [p for p in document.get('people', []) if p.get('userId') == row['user_id']]
            if len(matched) != 1 or matched[0].get('installationOwner') is not True:
                raise OwnerError('The serving application has not loaded the persisted owner authority.')
        disposition = 'runtime-owner-authority-verified'
    else:
        disposition = persist(path, policy)
    return {'passed': True, 'target': request['target'], 'providers': sorted(bindings),
            'result': disposition, 'checked_utc': datetime.now(timezone.utc).isoformat(),
            'sessions_created': 0}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--request', type=Path, required=True)
    parser.add_argument('--policy', type=Path, required=True)
    parser.add_argument('--state-file', type=Path, required=True)
    parser.add_argument('--verify', action='store_true')
    args = parser.parse_args()
    try:
        report = enroll(json.loads(args.request.read_text()), args.policy, args.verify)
    except (OwnerError, initial.access.AuditError, OSError, ValueError) as error:
        # Never publish exception text from raw JSON, private files, or HTTP bodies.
        report = {'passed': False, 'error': str(error) if isinstance(error, OwnerError) else 'Owner enrollment failed before verified completion.'}
    args.state_file.write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(report, indent=2))
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    sys.exit(main())
