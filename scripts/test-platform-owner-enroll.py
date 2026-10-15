#!/usr/bin/env python3
import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('owners', Path(__file__).with_name('platform-owner-enroll.py'))
owners = importlib.util.module_from_spec(spec)
spec.loader.exec_module(owners)


class OwnerEnrollmentTests(unittest.TestCase):
    def setUp(self):
        self.request = {'version': 1, 'kind': 'persistent-installation-owners', 'target': 'staging',
                        'host': 'hart-server', 'origin': 'http://127.0.0.1:5187', 'nonce': 'a' * 64,
                        'role': 'administrator', 'selectors': [{'provider': 'google', 'fingerprint_sha256': 'b' * 64,
                            'client_id_sha256': hashlib.sha256(b'fixture-client').hexdigest()}]}
        self.binding = {'user_id': '11111111-1111-1111-1111-111111111111', 'provider': 'google',
                        'issuer': 'https://accounts.google.com', 'subject': 'fixture-owner',
                        'role': 'administrator', 'event_count': 1, 'verified_at': '2026-01-01T00:00:00Z'}
        self.clients = [{'provider': 'google', 'clientId': 'fixture-client'}]

    def policy(self):
        return owners.policy_from(self.request, {'google': self.binding}, self.clients)

    def test_requires_explicit_owner_authority_request(self):
        owners.validate_request(self.request)
        self.request.pop('kind')
        with self.assertRaises(owners.OwnerError):
            owners.validate_request(self.request)

    def test_production_must_select_production_host(self):
        self.request['target'] = 'production'
        with self.assertRaises(owners.OwnerError):
            owners.validate_request(self.request)
        self.request['host'] = 'hart-prod'
        owners.validate_request(self.request)

    def test_policy_preserves_verified_binding_without_local_uuid_or_session(self):
        value = self.policy()
        self.assertEqual(set(value['owners'][0]), {'provider', 'issuer', 'subject', 'client_id', 'verified_at'})
        self.assertEqual(value['owners'][0]['subject'], self.binding['subject'])
        self.assertEqual(value['owners'][0]['verified_at'], '2026-01-01T00:00:00+00:00')

    def test_requires_existing_audited_administrator(self):
        for role, count in [('operator', 1), (None, 0), ('administrator', 0)]:
            self.binding.update(role=role, event_count=count)
            with self.assertRaises(owners.OwnerError):
                self.policy()

    def test_missing_future_or_naive_authentication_time_rejected(self):
        for value in (None, 'invalid', '2999-01-01T00:00:00Z', '2026-01-01T00:00:00'):
            self.binding['verified_at'] = value
            with self.assertRaises(owners.OwnerError):
                self.policy()

    def test_ambiguous_or_absent_live_client_rejected(self):
        for clients in ([], [self.clients[0], self.clients[0]], [{'provider': 'google', 'clientId': ''}]):
            with self.assertRaises(owners.OwnerError):
                owners.policy_from(self.request, {'google': self.binding}, clients)

    def test_changed_oauth_client_cannot_relabel_historical_owner_binding(self):
        self.clients[0]['clientId'] = 'different-client'
        with self.assertRaises(owners.OwnerError):
            self.policy()

    def test_replay_preserves_original_recorded_verification(self):
        with tempfile.TemporaryDirectory() as raw:
            directory = Path(raw).resolve()
            os.chmod(directory, 0o700)
            path = directory / 'policy.json'
            policy = self.policy()
            self.assertEqual(owners.persist(path, policy), 'policy-persisted')
            before = path.read_bytes()
            policy['owners'][0]['verified_at'] = '2026-01-02T00:00:00+00:00'
            self.assertEqual(owners.persist(path, policy), 'existing-policy-verified')
            self.assertEqual(before, path.read_bytes())

    def test_existing_policy_cannot_be_silently_rebound(self):
        with tempfile.TemporaryDirectory() as raw:
            directory = Path(raw).resolve()
            os.chmod(directory, 0o700)
            path = directory / 'policy.json'
            policy = self.policy()
            owners.persist(path, policy)
            before = path.read_bytes()
            for field in ('subject', 'issuer', 'client_id'):
                changed = copy.deepcopy(policy)
                changed['owners'][0][field] += '-different'
                with self.assertRaises(owners.OwnerError):
                    owners.persist(path, changed)
                self.assertEqual(before, path.read_bytes())

    def test_replay_rejects_invalid_preserved_policy(self):
        with tempfile.TemporaryDirectory() as raw:
            directory = Path(raw).resolve()
            os.chmod(directory, 0o700)
            path = directory / 'policy.json'
            policy = self.policy()
            owners.persist(path, policy)
            changed = copy.deepcopy(policy)
            changed['owners'][0]['verified_at'] = 'invalid'
            path.write_text(json.dumps(changed))
            with self.assertRaises(owners.OwnerError):
                owners.persist(path, policy)


if __name__ == '__main__':
    unittest.main()
