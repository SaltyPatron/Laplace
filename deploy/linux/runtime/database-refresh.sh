#!/usr/bin/env bash
# The installed credential timer joins the same queue as deployments. The
# credential owner receives the held descriptor and keeps its own verification.
set -euo pipefail
[[ $# -ge 2 ]] || { echo 'Expected host lock and credential command' >&2; exit 2; }
lock=$1
shift
here=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
source "$here/host-lock.sh"
host_lock_hold exclusive 8 "$lock" "${LAPLACE_HOST_LOCK_WAIT_SECONDS:-wait}"
export LAPLACE_HOST_LOCK_FD=8
exec "$@"
