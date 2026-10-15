#!/usr/bin/env python3
"""Transport private owner declarations to the application and migration process."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import tempfile
import unicodedata
import urllib.parse

POLICY_ENV = 'LAPLACE_PLATFORM_OWNER_POLICY'
CLIENT_ENV = {p: 'LAPLACE_AUTH_' + p.upper() + '_CLIENT_ID' for p in ('google', 'microsoft')}
MAX_BYTES = 65536


def has_control(value):
    return any(unicodedata.category(c) == 'Cc' for c in value)


def valid_issuer(provider, value):
    if provider == 'google':
        return value in ('accounts.google.com', 'https://accounts.google.com')
    try:
        parsed = urllib.parse.urlsplit(value)
        return (parsed.scheme == 'https' and parsed.hostname == 'login.microsoftonline.com'
                and parsed.username is None and parsed.password is None and parsed.port in (None, 443)
                and '?' not in value and '#' not in value
                and re.fullmatch(r'/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/v2\.0', parsed.path) is not None)
    except ValueError:
        return False


def verified_timestamp(value):
    # PostgreSQL JSON retains 0-6 fractional digits. Python 3.10 fromisoformat
    # accepts only 0, 3 or 6, whereas strptime's %f preserves every supported width.
    if not isinstance(value, str) or len(value) > 64 or not re.fullmatch(
            r'[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}'
            r'(?:\.[0-9]{1,6})?(?:Z|[+-][0-9]{2}:[0-9]{2})', value):
        raise ValueError('Invalid owner policy verification timestamp')
    try:
        pattern = '%Y-%m-%dT%H:%M:%S' + ('.%f' if '.' in value else '') + '%z'
        verified = datetime.strptime(value, pattern)
        if (abs(verified.utcoffset().total_seconds()) > 14 * 3600
                or verified <= datetime(1970, 1, 1, tzinfo=timezone.utc)
                or verified > datetime.now(timezone.utc)):
            raise ValueError()
        return verified
    except ValueError:
        raise ValueError('Invalid owner policy verification timestamp') from None


def environment(record):
    result = {}
    for provider, key in CLIENT_ENV.items():
        value = record.get(provider + '_client_id', '')
        if not isinstance(value, str) or has_control(value):
            raise ValueError('Invalid owner policy client configuration')
        result[key] = value
    policy = record.get('platform_owner_policy')
    if policy is None:
        return result
    if (not isinstance(policy, dict) or type(policy.get('version')) is not int
            or policy['version'] != 1 or not isinstance(policy.get('owners'), list)
            or not 0 <= len(policy['owners']) <= 16 or set(policy) != {'version', 'owners'}):
        raise ValueError('Invalid owner policy contract')
    seen = set()
    for owner in policy['owners']:
        if (not isinstance(owner, dict) or owner.get('provider') not in CLIENT_ENV
                or set(owner) - {'provider', 'issuer', 'subject', 'client_id', 'verified_at', 'display_name', 'email'}):
            raise ValueError('Invalid owner policy provider')
        for key in ('issuer', 'subject', 'client_id', 'verified_at'):
            value = owner.get(key)
            limit = 2048 if key == 'issuer' else 1024
            if not isinstance(value, str) or not value.strip() or value.strip() != value or len(value) > limit or has_control(value):
                raise ValueError('Invalid owner policy binding')
        if not valid_issuer(owner['provider'], owner['issuer']):
            raise ValueError('Invalid owner policy issuer')
        if owner['client_id'] != result[CLIENT_ENV[owner['provider']]]:
            raise ValueError('Owner policy belongs to a different provider client')
        verified_timestamp(owner['verified_at'])
        binding = tuple(owner[k] for k in ('provider', 'issuer', 'subject'))
        if binding in seen:
            raise ValueError('Duplicate owner policy binding')
        seen.add(binding)
        for key in ('display_name', 'email'):
            value = owner.get(key)
            if value is not None and (not isinstance(value, str) or not value.strip() or value.strip() != value
                    or len(value) > (512 if key == 'display_name' else 320) or has_control(value)):
                raise ValueError('Invalid owner policy profile')
    raw = json.dumps(policy, ensure_ascii=True, separators=(',', ':'))
    if len(raw.encode()) > MAX_BYTES:
        raise ValueError('Owner policy exceeds its size limit')
    result[POLICY_ENV] = raw
    return result


def policy_path():
    configured = os.environ.get('LAPLACE_PLATFORM_OWNER_POLICY_FILE')
    if configured:
        if not Path(configured).is_absolute():
            raise ValueError('Owner policy path must be absolute')
        return Path(configured)
    work = os.environ.get('LAPLACE_WORK_ROOT')
    if not work and os.environ.get('LAPLACE_WORK'):
        work = str(Path(os.environ['LAPLACE_WORK']) / 'laplace-mono')
    return Path(work) / 'platform-owners/policy.json' if work else None


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('Duplicate owner policy field')
        result[key] = value
    return result


def load_policy(path, missing_ok=False):
    if path is None:
        return {}
    path = Path(path)
    try:
        info = path.lstat()
    except FileNotFoundError:
        if missing_ok:
            return {}
        raise ValueError('The explicitly selected owner policy file is missing') from None
    if (not stat.S_ISREG(info.st_mode) or info.st_size > MAX_BYTES
            or (os.name != 'nt' and (stat.S_IMODE(info.st_mode) != 0o600
                                    or info.st_uid not in (0, os.geteuid())))):
        raise ValueError('Owner policy file is not private or has an invalid size')
    value = json.loads(path.read_text(encoding='utf-8'), object_pairs_hook=unique_object)
    if not isinstance(value, dict) or not isinstance(value.get('owners'), list):
        raise ValueError('Invalid owner policy contract')
    record = {'platform_owner_policy': value}
    for owner in value['owners']:
        if not isinstance(owner, dict) or owner.get('provider') not in CLIENT_ENV:
            raise ValueError('Invalid owner policy binding')
        key = owner['provider'] + '_client_id'
        if key in record and record[key] != owner.get('client_id'):
            raise ValueError('Owner policy has conflicting provider clients')
        record[key] = owner.get('client_id')
    return environment(record)


def apply_api_environment(path, values):
    path = Path(path)
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode):
        raise ValueError('API environment must be a regular file')
    # systemd decodes the outer quoting; the result remains a JSON string.
    lines = [line for line in path.read_text().splitlines(keepends=True)
             if line.partition('=')[0].strip() != POLICY_ENV]
    if lines and not lines[-1].endswith('\n'):
        lines[-1] += '\n'
    if POLICY_ENV in values:
        lines.append(POLICY_ENV + '=' + json.dumps(values[POLICY_ENV]) + '\n')
    fd, temporary = tempfile.mkstemp(prefix='.owner-policy-', dir=path.parent)
    try:
        with os.fdopen(fd, 'wb') as stream:
            if os.name != 'nt':
                os.fchmod(stream.fileno(), 0o600)
                current = os.fstat(stream.fileno())
                if (current.st_uid, current.st_gid) != (info.st_uid, info.st_gid):
                    os.fchown(stream.fileno(), info.st_uid, info.st_gid)
            stream.write(''.join(lines).encode())
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def run(path, command, missing_ok=False):
    values = load_policy(path, missing_ok)
    child = dict(os.environ)
    for key in (POLICY_ENV, *CLIENT_ENV.values()):
        child.pop(key, None)
    child.update(values)
    print('Platform owner policy: ' + ('configured' if POLICY_ENV in values else 'no-owner-config'), flush=True)
    return subprocess.run(command, env=child, check=False).returncode


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--policy-file')
    parser.add_argument('--apply-api-env')
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ['--'] else args.command
    if not command and not args.apply_api_env:
        parser.error('a migration command is required')
    try:
        path = Path(args.policy_file) if args.policy_file else policy_path()
        missing_ok = not (args.policy_file or os.environ.get('LAPLACE_PLATFORM_OWNER_POLICY_FILE'))
        if args.apply_api_env:
            values = load_policy(path, missing_ok)
            apply_api_environment(args.apply_api_env, values)
            print('Platform owner policy: ' + ('configured' if POLICY_ENV in values else 'no-owner-config'))
        else:
            sys.exit(run(path, command, missing_ok))
    except (OSError, ValueError, TypeError):
        raise SystemExit('Owner policy transport failed; private configuration was not printed.') from None
