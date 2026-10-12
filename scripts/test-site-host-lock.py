#!/usr/bin/env python3
"""Exercise the site's actual flock function with competing Linux processes."""
import fcntl
import os
from pathlib import Path
import subprocess
import tempfile
import time

source = (Path(__file__).resolve().parents[1] / 'deploy/linux/site.sh').read_text()
function = source[source.index('host_lock() {'):source.index('\nrevision() {')]

with tempfile.TemporaryDirectory(prefix='laplace-host-lock-', dir=os.environ.get('TMPDIR')) as work:
    path = Path(work) / 'host-resource.lock'
    environment = dict(os.environ, LAPLACE_LOCKS=work, LAPLACE_HOST_LOCK=str(path))
    def start(wait, mode='shared'):
        environment['LAPLACE_HOST_LOCK_WAIT_SECONDS'] = str(wait)
        return subprocess.Popen(['bash', '-c', 'set -eu\nfail() { echo "$*" >&2; exit 1; }\n' + function + '\nhost_lock ' + mode],
                                env=environment, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    with path.open('a') as holder:
        fcntl.flock(holder, fcntl.LOCK_EX)
        contender = start(3)
        time.sleep(0.2)
        assert contender.poll() is None, 'contender bypassed the held exclusive lock'
        fcntl.flock(holder, fcntl.LOCK_UN)
        _, error = contender.communicate(timeout=5)
        assert contender.returncode == 0, error

        fcntl.flock(holder, fcntl.LOCK_EX)
        contender = start(0)
        _, error = contender.communicate(timeout=2)
        assert contender.returncode != 0 and 'deadline expired' in error, error
        fcntl.flock(holder, fcntl.LOCK_SH)
        contender = start(0)
        _, error = contender.communicate(timeout=2)
        assert contender.returncode == 0, error
        contender = start(0, 'exclusive')
        _, error = contender.communicate(timeout=2)
        assert contender.returncode != 0 and 'deadline expired' in error, error
        fcntl.flock(holder, fcntl.LOCK_UN)

    contender = start('invalid')
    _, error = contender.communicate(timeout=2)
    assert contender.returncode != 0 and 'nonnegative integer' in error, error
print('PASS: real lock contention waits, times out, preserves shared/exclusive semantics and rejects an invalid deadline')
