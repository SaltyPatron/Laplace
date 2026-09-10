#!/usr/bin/env bash

# Synchronize a built payload into a host directory without allowing artifact
# staging metadata to redefine that directory. Host roots are installed and
# repaired by bootstrap; deploy owns the entries beneath them.
laplace_sync_payload() {
  local source_dir="$1"
  local destination_dir="$2"
  shift 2

  if [[ ! -d "$source_dir" ]]; then
    echo "::error::payload source missing: $source_dir"
    return 1
  fi
  if [[ ! -d "$destination_dir" ]]; then
    echo "::error::payload destination missing: $destination_dir"
    return 1
  fi

  # -a normally copies the source directory's permissions, owner, and group to
  # the destination root. Build staging comes from mktemp (0700), while the
  # destination is a persistent bootstrap-managed host directory (2775).
  # Preserve payload shape, timestamps, symlinks, and executable intent, but
  # leave host metadata under bootstrap ownership.
  rsync -a --delete \
    --no-perms --no-owner --no-group --executability \
    "$@" \
    "$source_dir/" "$destination_dir/"
}

# Managed publish backups are rollback state only while a publish receipt owns them.
# Completed/rolled-back transactions must not become an append-only archive on the
# application LV. Delete only bootstrap-shaped managed.* directories under the exact
# backup root, optionally preserving one active receipt path.
laplace_prune_managed_backups() {
  local root="$1" protected="${2:-}" candidate reclaimed=0
  [[ -d "$root" && ! -L "$root" ]] || return 0

  if [[ -n "$protected" ]]; then
    case "$protected" in
      "$root"/managed.*) ;;
      *) echo "::error::refusing invalid managed-backup protection path: $protected" >&2; return 1 ;;
    esac
  fi

  while IFS= read -r -d '' candidate; do
    [[ -n "$protected" && "$candidate" == "$protected" ]] && continue
    find "$candidate" -xdev -depth -delete || return 1
    echo "reclaimed completed application backup: $candidate"
    reclaimed=$((reclaimed + 1))
  done < <(find "$root" -mindepth 1 -maxdepth 1 -xdev -type d -name 'managed.*' -print0)
  echo "application backup retention: reclaimed=$reclaimed"
}

# Resolve the immutable runtime directory currently selected by one stable app link.
# The link points at the executable inside releases/runtime.*/<service>; dirname is
# therefore the safe --link-dest root. Anything outside that exact release shape is
# ignored rather than trusted as a deduplication reference.
laplace_current_runtime_dir() {
  local app_dir="$1" service="$2" link="$3" target runtime
  [[ -L "$link" ]] || return 1
  target="$(readlink -f "$link" 2>/dev/null || true)"
  [[ -n "$target" ]] || return 1
  runtime="$(dirname "$target")"
  case "$runtime" in
    "$app_dir"/releases/runtime.*/"$service") printf '%s\n' "$runtime" ;;
    *) return 1 ;;
  esac
}

laplace_release_in_use() {
  local app_dir="$1" candidate="$2" link target
  for link in "$app_dir"/laplace-lichess "$app_dir"/laplace-mcp "$app_dir"/laplace-uci; do
    [[ -L "$link" ]] || continue
    target="$(readlink -f "$link" 2>/dev/null || true)"
    [[ "$target" == "$candidate"/* ]] && return 0
  done
  return 1
}

# A pre-lease release can only be called complete if all three managed apphosts and
# the UCI closure exist. This is deliberately stricter than "directory exists": a
# failed rsync that filled the filesystem during the first service copy is provably
# not a usable legacy release and must not become immortal just because it predates
# the runtime-lease marker.
laplace_legacy_release_complete() {
  local candidate="$1" suffix
  [[ -x "$candidate/mcp/Laplace.Endpoints.Mcp" ]] || return 1
  [[ -x "$candidate/lichess/Laplace.Endpoints.Lichess" ]] || return 1
  [[ -x "$candidate/uci/laplace-uci" ]] || return 1
  for suffix in dll deps.json runtimeconfig.json; do
    [[ -s "$candidate/uci/laplace-uci.$suffix" ]] || return 1
  done
}

# Reclaim only release directories whose liveness is mechanically decidable.
# Current stable pointers always win. Lease-aware releases are collected under an
# exclusive lock, so another user's running process keeps its closure alive. Complete
# pre-lease releases are retained because their process ownership cannot be proved by
# an unprivileged runner. Incomplete pre-lease directories, however, cannot be valid
# runtimes and are safe to remove; this also recovers ENOSPC debris from a failed
# staging transaction before the next publish allocates anything.
laplace_prune_unreferenced_releases() {
  local app_dir="$1" releases="$1/releases" candidate reclaimed=0 retained=0
  [[ -d "$releases" && ! -L "$releases" ]] || return 0
  while IFS= read -r -d '' candidate; do
    laplace_release_in_use "$app_dir" "$candidate" && {
      retained=$((retained + 1))
      continue
    }

    if [[ -f "$candidate/.runtime-lease" && ! -L "$candidate/.runtime-lease" ]]; then
      if (
        exec 9<"$candidate/.runtime-lease" || exit 1
        flock -n -x 9 || exit 1
        find "$candidate" -xdev -depth -delete
      ); then
        echo "reclaimed unreferenced application release: $candidate"
        reclaimed=$((reclaimed + 1))
      else
        retained=$((retained + 1))
      fi
      continue
    fi

    if ! laplace_legacy_release_complete "$candidate"; then
      find "$candidate" -xdev -depth -delete || return 1
      echo "reclaimed incomplete application release: $candidate"
      reclaimed=$((reclaimed + 1))
    else
      retained=$((retained + 1))
    fi
  done < <(find "$releases" -mindepth 1 -maxdepth 1 -xdev -type d -name 'runtime.*' -print0)
  echo "application release retention: reclaimed=$reclaimed retained=$retained"
}

# Stage one immutable service closure. A release is never mutated after publication,
# so unchanged files may safely be hardlinked from the currently selected release.
# Build timestamps are not identity: deterministic rebuilds can produce byte-identical
# dependencies with fresh mtimes. --checksum --no-times makes content, not timestamp,
# decide whether --link-dest can reuse the immutable inode. Changed bytes are copied.
laplace_stage_runtime_payload() {
  local app_dir="$1" service="$2" stable_link="$3" source_dir="$4" destination_dir="$5"
  local reference=""
  reference="$(laplace_current_runtime_dir "$app_dir" "$service" "$stable_link" 2>/dev/null || true)"
  if [[ -n "$reference" ]]; then
    laplace_sync_payload "$source_dir" "$destination_dir" \
      --checksum --no-times --link-dest="$reference"
  else
    laplace_sync_payload "$source_dir" "$destination_dir"
  fi
}

# Keep the whole immutable closure reachable for the complete process lifetime,
# including dependencies that .NET loads lazily. The lock is acquired before
# the apphost starts and its descriptor survives exec. It works across UIDs
# without inspecting private process mappings or requiring privileged GC.
laplace_reuse_runtime_file() {
  local reference="$1" destination="$2"
  [[ -f "$reference" && ! -L "$reference" && -f "$destination" && ! -L "$destination" ]] || return 0
  [[ "$(stat -c '%a' "$reference")" == "$(stat -c '%a' "$destination")" ]] || return 0
  cmp -s "$reference" "$destination" || return 0
  # A cross-device reference is still valid content, but cannot share an inode.
  # Keep the staged file until a replacement link has actually been created.
  if ln "$reference" "$destination.reuse" 2>/dev/null; then
    mv -f "$destination.reuse" "$destination" || return 1
  fi
}

laplace_wrap_runtime_lease() {
  local executable="$1" reference_dir="${2:-}" name
  name="${executable##*/}"
  mv "$executable" "$executable.native" || return 1
  cat > "$executable" <<'LAUNCHER' || return 1
#!/usr/bin/env bash
set -euo pipefail
runtime_executable="$(readlink -f "${BASH_SOURCE[0]}")"
exec 9<"$(dirname "$runtime_executable")/../.runtime-lease"
flock -s 9
exec "$runtime_executable.native" "$@"
LAUNCHER
  chmod 0755 "$executable" || return 1
  if [[ -n "$reference_dir" ]]; then
    laplace_reuse_runtime_file "$reference_dir/$name.native" "$executable.native" || return 1
    laplace_reuse_runtime_file "$reference_dir/$name" "$executable" || return 1
  fi
}

# Publish each managed runtime into a NEW immutable directory. Return its absolute
# path; the caller updates stable launch links only after all copies succeed. A failed
# stage is deleted before returning failure, so ENOSPC cannot strand another partial
# runtime that makes the next retry even less likely to fit.
laplace_stage_managed_runtimes() {
  local app_dir="$1" mcp_stage="$2" lichess_stage="$3" uci_stage="$4" release suffix
  test -x "$mcp_stage/Laplace.Endpoints.Mcp" || return 1
  test -x "$lichess_stage/Laplace.Endpoints.Lichess" || return 1
  test -x "$uci_stage/laplace-uci" || return 1
  for suffix in dll deps.json runtimeconfig.json; do
    test -s "$uci_stage/laplace-uci.$suffix" || return 1
  done
  install -d -m 2775 "$app_dir/releases" || return 1
  release="$(mktemp -d "$app_dir/releases/runtime.XXXXXX")" || return 1

  if ! (
    set -e
    chmod 0755 "$release"
    mkdir -m 0755 "$release/mcp" "$release/lichess" "$release/uci"
    laplace_stage_runtime_payload "$app_dir" mcp "$app_dir/laplace-mcp" \
      "$mcp_stage" "$release/mcp"
    laplace_stage_runtime_payload "$app_dir" lichess "$app_dir/laplace-lichess" \
      "$lichess_stage" "$release/lichess"
    laplace_stage_runtime_payload "$app_dir" uci "$app_dir/laplace-uci" \
      "$uci_stage" "$release/uci"
    install -m 0644 /dev/null "$release/.runtime-lease"
    laplace_wrap_runtime_lease "$release/mcp/Laplace.Endpoints.Mcp" \
      "$(laplace_current_runtime_dir "$app_dir" mcp "$app_dir/laplace-mcp" || true)"
    laplace_wrap_runtime_lease "$release/lichess/Laplace.Endpoints.Lichess" \
      "$(laplace_current_runtime_dir "$app_dir" lichess "$app_dir/laplace-lichess" || true)"
    laplace_wrap_runtime_lease "$release/uci/laplace-uci" \
      "$(laplace_current_runtime_dir "$app_dir" uci "$app_dir/laplace-uci" || true)"
    ln -s ../../../logs "$release/mcp/logs"
    ln -s ../../../logs "$release/lichess/logs"
    ln -s ../../../logs "$release/uci/logs"
  ); then
    find "$release" -xdev -depth -delete || true
    return 1
  fi

  printf '%s\n' "$release"
}
