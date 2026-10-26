# shellcheck shell=bash
# The shared host lock (/run/lock/laplace/host-resource.lock), taken with writer preference as Laplace-Operations'
# operation-lock.sh and modular.py take it (Laplace-Operations#133, #1899). flock has none: a new shared holder is
# granted past a waiting exclusive one, so a stream of overlapping ingests and verifications could hold a deployment off
# until its deadline. Every taker that may wait first passes the turnstile, the lock's path with .turnstile, held
# exclusively only while it waits for the lock: a waiting deployment holds it, so a later reader queues behind it, and
# the readers already in drain. A descriptor that already holds the lock (a nested command of its holder, which
# inherited it) takes it again without the turnstile, and so never waits behind a deployment that waits for it.
# Descriptor 7 holds the turnstile, and is closed before this returns.
#   source deploy/linux/host-lock.sh
#   host_lock_hold shared|exclusive FD PATH [WAIT]
#     FD the descriptor that holds the lock after (opened on PATH unless it is already); WAIT seconds, 0 for an
#     immediate refusal, wait (or none) to wait as long as it takes. 0 held; 3 the deadline expired, nothing held; 2 misused.
host_lock_hold() {
  local mode=$1 fd=$2 path=$3 wait=${4:-} flag mask start said=
  case "$mode" in shared) flag=-s ;; exclusive) flag=-x ;; *) echo "host_lock_hold: invalid host lock mode: $mode" >&2; return 2 ;; esac
  [[ "$fd" =~ ^[0-9]+$ && "$fd" != 7 ]] || { echo "host_lock_hold: FD is a descriptor number, not 7 (the turnstile's)" >&2; return 2; }
  [[ "$wait" != wait ]] || wait=
  [[ -z "$wait" || "$wait" =~ ^[0-9]+$ ]] || { echo "host_lock_hold: WAIT must be a nonnegative integer or wait" >&2; return 2; }
  if [[ /proc/self/fd/$fd -ef "$path" ]]; then flock "$flag" -n "$fd" && return 0
  else
    mask=$(umask); umask 002
    if ! eval "exec $fd>>\"\$path\""; then
      umask "$mask"
      echo "cannot open host lock: $path (check ownership and group write permission)" >&2
      return 1
    fi
    umask "$mask"
  fi
  mask=$(umask); umask 002
  if ! exec 7>>"$path.turnstile"; then
    umask "$mask"
    echo "cannot open host lock turnstile: $path.turnstile" >&2
    return 1
  fi
  umask "$mask"
  start=$SECONDS
  if ! flock -x -n 7; then
    echo "Waiting ${wait:+up to ${wait}s }for $path ($mode)" >&2; said=1
    if [[ -n "$wait" ]]; then flock -x -w "$wait" 7 || { exec 7>&-; echo "host lock deadline expired: $path" >&2; return 3; }
    else flock -x 7; fi
  fi
  if ! flock "$flag" -n "$fd"; then
    [[ -n "$said" ]] || echo "Waiting ${wait:+up to ${wait}s }for $path ($mode)" >&2
    if [[ -n "$wait" ]]; then local left=$(( wait - (SECONDS - start) )); (( left > 0 )) || left=0
      flock "$flag" -w "$left" "$fd" || { exec 7>&-; echo "host lock deadline expired: $path" >&2; return 3; }
    else flock "$flag" "$fd"; fi
  fi
  exec 7>&-
  return 0
}
