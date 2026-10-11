#!/usr/bin/env python3
"""Check public Google client/callback registration without completing a user login."""
import argparse
import base64
from datetime import datetime, timezone
import json
from pathlib import Path
import urllib.parse
import urllib.request
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('enrollment_receipt', help='Public metadata produced by import-google-clients.py')
    args = parser.parse_args()
    receipt = json.loads(Path(args.enrollment_receipt).read_text(encoding='utf-8-sig'))
    checks = []
    for item in receipt['stored_and_read_back']:
        for callback in item['callbacks']:
            query = urllib.parse.urlencode({'client_id': item['client_id'], 'redirect_uri': callback,
                'response_type': 'code', 'scope': 'openid email profile', 'state': uuid.uuid4().hex,
                'code_challenge': 'oia3MTNt81nqu-M39KTy0GudNz2TKmdIwDRdPJNKphI', 'code_challenge_method': 'S256'})
            with urllib.request.urlopen('https://accounts.google.com/o/oauth2/v2/auth?' + query, timeout=20) as response:
                target = urllib.parse.urlsplit(response.url)
                body = response.read().decode('utf-8', 'replace')
                error = urllib.parse.parse_qs(target.query).get('authError', [''])[0]
                if error:
                    error = base64.urlsafe_b64decode(error + '=' * (-len(error) % 4)).decode('utf-8', 'replace')
                checks.append({'environment': item['environment'], 'callback': callback, 'client_id': item['client_id'],
                    'http_status': response.status, 'response_host': target.hostname, 'response_path': target.path,
                    'redirect_uri_mismatch': 'redirect_uri_mismatch' in error or 'redirect_uri_mismatch' in body,
                    'oauth_error_page': '/error' in target.path})
    print(json.dumps({'observed_utc': datetime.now(timezone.utc).isoformat(),
        'scope': 'Google authorization request only; does not complete a user login or verify the client secret.',
        'checks': checks}, indent=2))
    return int(any(c['redirect_uri_mismatch'] or c['oauth_error_page'] for c in checks))


if __name__ == '__main__':
    raise SystemExit(main())
