"""Converge this installation's self-signed PostgreSQL server certificate and scoped trust."""
from contextlib import contextmanager
import hashlib
import ipaddress
from pathlib import Path
import re
import socket
import ssl
import struct
import subprocess
import tempfile


def command(arguments):
    result = subprocess.run(arguments, text=True, capture_output=True, timeout=30)
    if result.returncode:
        raise ValueError('PostgreSQL certificate operation failed: ' + arguments[1])
    return result.stdout.strip()


def validate(policy):
    if policy['mode'] != 'self-signed-local':
        raise ValueError('This operation only manages the declared local self-signed certificate')
    if not re.fullmatch(r'[A-Za-z0-9 ._-]+', policy['subject']):
        raise ValueError('Invalid PostgreSQL certificate subject')
    names = policy['dns_names']
    if not names or not all(re.fullmatch(r'[A-Za-z0-9.-]+', name) for name in names):
        raise ValueError('Declare PostgreSQL certificate DNS names')
    for address in policy['ip_addresses']:
        ipaddress.ip_address(address)
    if not 0 < int(policy['renew_before_days']) < int(policy['valid_days']) <= 825:
        raise ValueError('Invalid PostgreSQL certificate validity/renewal window')
    root = Path(policy['root_certificate'])
    if not root.is_absolute() or root == Path('/') or root.is_symlink():
        raise ValueError('Declare an absolute regular runtime trust file')


def desired(certificate, key, policy):
    if not certificate.is_file():
        return False
    try:
        output = command(['openssl', 'x509', '-in', str(certificate), '-noout', '-subject', '-nameopt', 'RFC2253'])
        if output != 'subject=CN=' + policy['subject']:
            return False
        command(['openssl', 'x509', '-in', str(certificate), '-noout', '-checkend', str(86400 * policy['renew_before_days'])])
        for name in policy['dns_names']:
            command(['openssl', 'verify', '-CAfile', str(certificate), '-verify_hostname', name, str(certificate)])
        for address in policy['ip_addresses']:
            command(['openssl', 'verify', '-CAfile', str(certificate), '-verify_ip', address, str(certificate)])
        public = command(['openssl', 'x509', '-in', str(certificate), '-noout', '-pubkey'])
        return public == command(['openssl', 'pkey', '-in', str(key), '-pubout'])
    except ValueError:
        return False


def generate(target, key, policy):
    names = ['DNS:' + name for name in policy['dns_names']] + ['IP:' + value for value in policy['ip_addresses']]
    command(['openssl', 'req', '-new', '-x509', '-sha256', '-days', str(policy['valid_days']),
        '-key', str(key), '-out', str(target), '-subj', '/CN=' + policy['subject'],
        '-addext', 'subjectAltName=' + ','.join(names), '-addext', 'basicConstraints=critical,CA:FALSE',
        '-addext', 'keyUsage=critical,digitalSignature,keyEncipherment', '-addext', 'extendedKeyUsage=serverAuth'])
    if not desired(target, key, policy):
        raise ValueError('Generated PostgreSQL certificate failed identity/key verification')


def verify_endpoint(host, port, root):
    context = ssl.create_default_context(cafile=str(root))
    with socket.create_connection((host, port), timeout=10) as connection:
        connection.sendall(struct.pack('!II', 8, 80877103))
        if connection.recv(1) != b'S':
            raise ValueError('PostgreSQL refused TLS negotiation')
        with context.wrap_socket(connection, server_hostname=host) as secured:
            return hashlib.sha256(secured.getpeercert(binary_form=True)).hexdigest()


@contextmanager
def configured(config, sql, sync):
    policy = config['tls']
    validate(policy)
    data = Path(config['database_data'])
    certificate, key = (Path(sql('SHOW ' + setting)) for setting in ('ssl_cert_file', 'ssl_key_file'))
    certificate = certificate if certificate.is_absolute() else data / certificate
    key = key if key.is_absolute() else data / key
    if certificate.is_symlink() or key.is_symlink() or not certificate.is_file() or not key.is_file():
        raise ValueError('Declared cluster must have regular certificate/key files')
    # Preserve and verify the existing key, and refuse to replace a CA-issued certificate.
    identity = command(['openssl', 'x509', '-in', str(certificate), '-noout', '-subject', '-issuer', '-nameopt', 'RFC2253']).splitlines()
    if len(identity) != 2 or identity[0].partition('=')[2] != identity[1].partition('=')[2]:
        raise ValueError('Refusing to replace a CA-issued PostgreSQL certificate with a local certificate')
    trust = Path(policy['root_certificate'])
    if not trust.parent.is_dir():
        raise ValueError('Runtime trust directory must be installed first')
    stat = certificate.stat()
    def reload():
        if sql('SELECT pg_reload_conf()') != 't':
            raise ValueError('PostgreSQL certificate reload failed')
    with sync.configuration_transaction([certificate, trust], reload):
        if not desired(certificate, key, policy):
            backup = Path(config['work']) / ('postgres-certificate-' + hashlib.sha256(certificate.read_bytes()).hexdigest() + '.pem')
            if not backup.exists():
                sync.atomic(backup, certificate.read_text(), 0o644, 0, 0)
            with tempfile.TemporaryDirectory(prefix='laplace-tls-') as temporary:
                candidate = Path(temporary) / 'server.crt'
                generate(candidate, key, policy)
                sync.atomic(certificate, candidate.read_text(), stat.st_mode & 0o777, stat.st_uid, stat.st_gid)
        sync.atomic(trust, certificate.read_text(), 0o644, 0, 0)
        reload()
        # A reload signal is asynchronous; wait only for the explicitly declared certificate.
        import time
        last_error = None
        for _ in range(30):
            try:
                fingerprint = verify_endpoint(config['database_host'], config['port'], trust)
                break
            except (OSError, ValueError) as error:
                last_error = error
                time.sleep(0.1)
        else:
            raise ValueError('PostgreSQL TLS identity failed verification after reload') from last_error
        yield {'verified': True, 'sha256': fingerprint, 'root_certificate': str(trust), 'private_key_preserved': True}
