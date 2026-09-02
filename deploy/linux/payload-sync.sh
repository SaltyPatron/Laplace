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

# Stage one immutable service closure. A release is never mutated after publication,
# so unchanged files may safely be hardlinked from the currently selected release.
# rsync --link-dest compares the staged source byte/metadata contract and links only
# matching files; changed files are copied normally. This preserves old clients while
# avoiding the impossible requirement that the application LV hold two complete copies
# of every unchanged .NET/native dependency during each atomic cutover.
laplace_stage_runtime_payload() {
  local app_dir="$1" service="$2" stable_link="$3" source_dir="$4" destination_dir="$5"
  local reference=""
  reference="$(laplace_current_runtime_dir "$app_dir" "$service" "$stable_link" 2>/dev/null || true)"
  if [[ -n "$reference" ]]; then
    laplace_sync_payload "$source_dir" "$destination_dir" --link-dest="$reference"
  else
    laplace_sync_payload "$source_dir" "$destination_dir"
  fi
}

# Publish each managed runtime into a NEW immutable directory. Return its absolute
# path; the caller updates stable launch links only after all copies succeed.
laplace_stage_managed_runtimes() {
  local app_dir="$1" mcp_stage="$2" lichess_stage="$3" uci_stage="$4" release suffix
  # Explicit propagation matters inside command substitution, where Bash may
  # clear errexit; a failed rsync must never be masked by the final printf.
  test -x "$mcp_stage/Laplace.Endpoints.Mcp" || return 1
  test -x "$lichess_stage/Laplace.Endpoints.Lichess" || return 1
  test -x "$uci_stage/laplace-uci" || return 1
  for suffix in dll deps.json runtimeconfig.json; do
    test -s "$uci_stage/laplace-uci.$suffix" || return 1
  done
  install -d -m 2775 "$app_dir/releases" || return 1
  release="$(mktemp -d "$app_dir/releases/runtime.XXXXXX")" || return 1
  chmod 0755 "$release" || return 1
  mkdir -m 0755 "$release/mcp" "$release/lichess" "$release/uci" || return 1
  laplace_stage_runtime_payload "$app_dir" mcp "$app_dir/laplace-mcp" \
    "$mcp_stage" "$release/mcp" || return 1
  laplace_stage_runtime_payload "$app_dir" lichess "$app_dir/laplace-lichess" \
    "$lichess_stage" "$release/lichess" || return 1
  # A .NET apphost is not a standalone executable: preserve its entire publish
  # closure, isolated from the API's differently named runtime/dependency files.
  laplace_stage_runtime_payload "$app_dir" uci "$app_dir/laplace-uci" \
    "$uci_stage" "$release/uci" || return 1
  ln -s ../../../logs "$release/mcp/logs" || return 1
  ln -s ../../../logs "$release/lichess/logs" || return 1
  ln -s ../../../logs "$release/uci/logs" || return 1
  printf '%s\n' "$release"
}
