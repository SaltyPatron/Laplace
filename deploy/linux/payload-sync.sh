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

# Publish each managed runtime into a NEW immutable directory. Return its absolute
# path; the caller updates stable launch links only after both copies succeed.
laplace_stage_managed_runtimes() {
  local app_dir="$1" mcp_stage="$2" lichess_stage="$3" release
  # Explicit propagation matters inside command substitution, where Bash may
  # clear errexit; a failed rsync must never be masked by the final printf.
  test -x "$mcp_stage/Laplace.Endpoints.Mcp" || return 1
  test -x "$lichess_stage/Laplace.Endpoints.Lichess" || return 1
  install -d -m 2775 "$app_dir/releases" || return 1
  release="$(mktemp -d "$app_dir/releases/runtime.XXXXXX")" || return 1
  chmod 0755 "$release" || return 1
  mkdir -m 0755 "$release/mcp" "$release/lichess" || return 1
  laplace_sync_payload "$mcp_stage" "$release/mcp" || return 1
  laplace_sync_payload "$lichess_stage" "$release/lichess" || return 1
  ln -s ../../../logs "$release/mcp/logs" || return 1
  ln -s ../../../logs "$release/lichess/logs" || return 1
  printf '%s\n' "$release"
}
