"""Install the declared private HTTPS browser route using the existing certificate."""
import argparse
import ipaddress
import json
import os
import re
import socket
import subprocess
import time
import urllib.parse
from pathlib import Path


def render(config):
    origin = urllib.parse.urlsplit(config['browser_origin'])
    if origin.scheme != 'https' or origin.path or origin.query or origin.fragment or origin.username or not re.fullmatch(r'[a-z0-9.-]+', origin.hostname or ''):
        raise ValueError('The browser origin must be a named HTTPS origin')
    addresses = [str(ipaddress.ip_address(value)) for value in config['listen_addresses']]
    if not addresses or not config['allowed_networks']:
        raise ValueError('Declare private listen addresses and allowed client networks')
    listens = '\n    '.join('listen ' + ('[' + value + ']' if ':' in value else value) + ':' + str(origin.port or 443) + ' ssl http2;' for value in addresses)
    allows = '\n    '.join('allow ' + str(ipaddress.ip_network(n, strict=False)) + ';' for n in config['allowed_networks'])
    for key in ('client_max_body_size', 'mcp_max_body_size'):
        if not re.fullmatch(r'[1-9][0-9]*[kKmMgG]?', config[key]):
            raise ValueError('Invalid request body limit: ' + key)
    timeout = config['proxy_timeout_seconds']
    if not isinstance(timeout, int) or isinstance(timeout, bool) or not 1 <= timeout <= 86400:
        raise ValueError('Invalid proxy timeout')
    activation_timeout = config.get('activation_timeout_seconds', 30)
    if not isinstance(activation_timeout, int) or isinstance(activation_timeout, bool) or not 1 <= activation_timeout <= 300:
        raise ValueError('Invalid route activation timeout')
    for name in ('certificate', 'certificate_key'):
        if not re.fullmatch(r'/[A-Za-z0-9_./-]+', config[name]) or '..' in Path(config[name]).parts:
            raise ValueError('Invalid certificate path')
    locations = []
    for location, key in (('/', 'api_upstream'), ('= /mcp', 'mcp_upstream')):
        upstream = urllib.parse.urlsplit(config[key])
        if upstream.scheme != 'http' or upstream.hostname not in ('127.0.0.1', '::1') or not upstream.port or upstream.path or upstream.query or upstream.fragment or upstream.username:
            raise ValueError('A private browser route must use the declared loopback service')
        limit = config['mcp_max_body_size'] if key == 'mcp_upstream' else config['client_max_body_size']
        locations.append(f'''    location {location} {{
        proxy_pass {config[key]};
        proxy_http_version 1.1;
        proxy_set_header Host $http_host;
        proxy_set_header X-Forwarded-Host $http_host;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $laplace_mono_browser_connection;
        client_max_body_size {limit};
        proxy_buffering off;
        proxy_read_timeout {timeout}s;
        proxy_send_timeout {timeout}s;
    }}''')
    return f'''# Managed by Laplace deploy/linux/configure-nginx.py.
map $http_upgrade $laplace_mono_browser_connection {{
    default upgrade;
    '' '';
}}
server {{
    {listens}
    server_name {origin.hostname};
    ssl_certificate {config['certificate']};
    ssl_certificate_key {config['certificate_key']};
    ssl_protocols TLSv1.2 TLSv1.3;
    {allows}
    deny all;
    include /etc/nginx/laplace-mono/*.location.conf;
{chr(10).join(locations)}
}}
'''


def verify_certificate(config):
    hostname = urllib.parse.urlsplit(config['browser_origin']).hostname
    # Use verification's exit status, not x509's human-readable match message.
    # The supplied intermediates build the chain; trust still comes from the OS.
    result = subprocess.run(['openssl', 'verify', '-purpose', 'sslserver',
                             '-verify_hostname', hostname, '-untrusted', config['certificate'],
                             config['certificate']], capture_output=True, text=True)
    if result.returncode:
        detail = (result.stderr or result.stdout).strip()
        raise SystemExit(f'Certificate verification failed for {hostname}:\n{detail}')
    subprocess.run(['openssl', 'x509', '-in', config['certificate'], '-noout', '-checkend', '86400'], check=True)


def verify_route(config):
    origin = urllib.parse.urlsplit(config['browser_origin'])
    timeout = config.get('activation_timeout_seconds', 30)
    if not isinstance(timeout, int) or isinstance(timeout, bool) or not 1 <= timeout <= 300:
        raise ValueError('Invalid route activation timeout')
    for address in config['listen_addresses']:
        probe_address = '[' + address + ']' if ':' in address else address
        deadline = time.monotonic() + timeout
        last_error = 'no response'
        # Reload signals nginx; old workers can still answer until it starts
        # the replacement workers. Open a new TLS connection for each probe.
        while time.monotonic() < deadline:
            remaining = max(0.1, deadline - time.monotonic())
            response = subprocess.run(['curl', '--silent', '--show-error',
                         '--max-time', str(min(5, remaining)), '--noproxy', '*',
                         '--resolve', f'{origin.hostname}:{origin.port or 443}:{probe_address}',
                         '--write-out', '\\n%{http_code}', config['browser_origin'] + '/health'],
                         capture_output=True, text=True)
            body, _, status = response.stdout.rpartition('\n')
            if response.returncode:
                last_error = f'curl exit {response.returncode}: {response.stderr.strip()}'
            elif status != '200':
                last_error = 'HTTP ' + status
            else:
                try:
                    if json.loads(body).get('status') == 'ok':
                        break
                    last_error = 'application health status is not ok'
                except (ValueError, AttributeError):
                    last_error = 'HTTP 200 did not contain application health JSON'
            time.sleep(min(0.25, max(0, deadline - time.monotonic())))
        else:
            raise RuntimeError(f'{config["browser_origin"]}/health via {address} did not become ready '
                               f'within {timeout}s: {last_error}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--configuration', type=Path, default=Path(__file__).parent.parent / 'private-origins.json')
    parser.add_argument('--environment')
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--apply', action='store_true')
    mode.add_argument('--check-certificate', action='store_true', help='Verify the declared certificate without changing the installation')
    mode.add_argument('--print-origin', action='store_true', help='Print the selected declared HTTPS origin without changing the installation')
    args = parser.parse_args()
    environments = json.loads(args.configuration.read_text(encoding='utf-8'))['environments']
    matches = [e for e in environments if (e['name'] == args.environment if args.environment else
               e['host'].lower() == socket.gethostname().split('.')[0].lower() and e['hosting'] == 'nginx')]
    if not matches and not args.environment:
        if args.print_origin:
            raise SystemExit('No private nginx browser route declared for this host.')
        print('No private nginx browser route declared for this host.')
        return
    if len(matches) != 1:
        raise SystemExit('Select exactly one declared nginx environment.')
    config = matches[0]
    if config['hosting'] != 'nginx':
        raise SystemExit('This installation operation is for a declared nginx environment.')
    content = render(config)
    if args.print_origin:
        print(config['browser_origin'])
        return
    if args.check_certificate:
        verify_certificate(config)
        print('Certificate verified for ' + config['browser_origin'])
        return
    if not args.apply:
        print(content, end='')
        return
    if os.geteuid() != 0 or socket.gethostname().split('.')[0].lower() != config['host'].lower():
        raise SystemExit('Apply requires root on the declared installation host.')
    verify_certificate(config)
    target = Path('/etc/nginx/sites-available/laplace-mono-browser')
    enabled = Path('/etc/nginx/sites-enabled/laplace-mono-browser')
    if enabled.is_symlink() and enabled.resolve() != target:
        raise SystemExit('The browser site link belongs to another configuration.')
    if enabled.exists() and not enabled.is_symlink():
        raise SystemExit('The browser site entry is not an installer-owned symlink.')
    if target.is_symlink() or (target.exists() and not target.read_text(encoding='utf-8').startswith('# Managed by Laplace deploy/linux/configure-nginx.py.')):
        raise SystemExit('The browser configuration belongs to another installer.')
    before = target.read_bytes() if target.exists() else None
    was_enabled = enabled.is_symlink()
    try:
        target.write_text(content, encoding='utf-8')
        target.chmod(0o644)
        if not was_enabled:
            enabled.symlink_to(target)
        subprocess.run(['nginx', '-t'], check=True)
        subprocess.run(['systemctl', 'reload', 'nginx'], check=True)
        verify_route(config)
    except Exception:
        if before is None:
            target.unlink(missing_ok=True)
        else:
            target.write_bytes(before)
        if not was_enabled:
            enabled.unlink(missing_ok=True)
        subprocess.run(['nginx', '-t'], check=True)
        subprocess.run(['systemctl', 'reload', 'nginx'], check=True)
        raise
    print('\nInstalled and reached ' + config['browser_origin'])


if __name__ == '__main__':
    main()
