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
