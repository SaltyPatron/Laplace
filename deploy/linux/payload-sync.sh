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

# Enumerate content references for a new immutable runtime. --link-dest verifies
# identical bytes before sharing an inode, so an incomplete old release is still a
# valid content donor even though it is not a valid execution donor. This distinction
# is what lets a nearly-full application filesystem migrate without requiring a full
# second copy of framework/native dependencies before the stable pointer can move.
#
# Priority is the currently selected immutable runtime, then the exact bootstrap-owned
# legacy MCP runtime, then newest retained immutable/partial releases. Rsync accepts at
# most 20 --link-dest directories; duplicates are removed before that bound is applied.
laplace_runtime_reference_dirs() {
  local app_dir="$1" service="$2" stable_link="$3"
  local target="" runtime="" candidate="" resolved="" stamp="" count=0
  local releases="$app_dir/releases"
  declare -A seen=()

  runtime="$(laplace_current_runtime_dir "$app_dir" "$service" "$stable_link" 2>/dev/null || true)"
  if [[ -n "$runtime" && -d "$runtime" && ! -L "$runtime" ]]; then
    seen["$runtime"]=1
    printf '%s\n' "$runtime"
    count=$((count + 1))
  fi

  # The pre-managed MCP deployment is itself a complete runtime closure in the
  # bootstrap-owned mcp-runtime directory. Current deploy never mutates that
  # directory. Accept it only when the stable link resolves to its exact apphost;
  # a similarly named directory or arbitrary symlink is not a migration source.
  if [[ "$service" == "mcp" && "$count" -lt 20 && -L "$stable_link" \
     && -d "$app_dir/mcp-runtime" && ! -L "$app_dir/mcp-runtime" ]]; then
    target="$(readlink -f "$stable_link" 2>/dev/null || true)"
    resolved="$(readlink -f "$app_dir/mcp-runtime/Laplace.Endpoints.Mcp" 2>/dev/null || true)"
    if [[ -n "$target" && "$target" == "$resolved" \
       && "$target" == "$app_dir/mcp-runtime/Laplace.Endpoints.Mcp" \
       && -z "${seen[$app_dir/mcp-runtime]+x}" ]]; then
      seen["$app_dir/mcp-runtime"]=1
      printf '%s\n' "$app_dir/mcp-runtime"
      count=$((count + 1))
    fi
  fi

  [[ -d "$releases" && ! -L "$releases" ]] || return 0
  while IFS=$'\t' read -r -d '' stamp candidate; do
    ((${#stamp} > 0)) || continue
    [[ "$count" -lt 20 ]] || break
    runtime="$candidate/$service"
    [[ -d "$runtime" && ! -L "$runtime" ]] || continue
    resolved="$(readlink -f "$runtime" 2>/dev/null || true)"
    case "$resolved" in
      "$app_dir"/releases/runtime.*/"$service") ;;
      *) continue ;;
    esac
    [[ -z "${seen[$resolved]+x}" ]] || continue
    seen["$resolved"]=1
    printf '%s\n' "$resolved"
    count=$((count + 1))
  done < <(find "$releases" -mindepth 1 -maxdepth 1 -xdev -type d \
             -name 'runtime.*' -printf '%T@\t%p\0' | sort -z -nr)
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

# A user-level publish may only recursively delete a release when every directory
# in that tree is writable by the publishing identity. Preflighting the complete
# directory set prevents `find -delete` from partially dismantling an older
# root/service-owned release before discovering one inaccessible subtree.
laplace_release_tree_writable() {
  local candidate="$1" directory
  [[ -d "$candidate" && ! -L "$candidate" && -w "$(dirname "$candidate")" ]] || return 1
  while IFS= read -r -d '' directory; do
    [[ -w "$directory" ]] || return 1
  done < <(find "$candidate" -xdev -type d -print0)
  return 0
}

# Reclaim only release directories whose liveness is mechanically decidable.
# Current stable pointers always win. Lease-aware releases are collected under an
# exclusive lock, so another user's running process keeps its closure alive. Complete
# pre-lease releases are retained because their process ownership cannot be proved by
# an unprivileged runner. Incomplete pre-lease directories are reclaimed only when the
# publishing identity can delete the entire tree atomically enough to avoid damaging
# an older root/service-owned layout. Inaccessible legacy debris is retained but does
# not abort scanning later releases, allowing runner-owned failed stages to be reclaimed.
laplace_prune_unreferenced_releases() {
  local app_dir="$1" releases="$1/releases" candidate reclaimed=0 retained=0
  [[ -d "$releases" && ! -L "$releases" ]] || return 0
  while IFS= read -r -d '' candidate; do
    laplace_release_in_use "$app_dir" "$candidate" && {
      retained=$((retained + 1))
      continue
    }

    if [[ -f "$candidate/.runtime-lease" && ! -L "$candidate/.runtime-lease" ]]; then
      if ! laplace_release_tree_writable "$candidate"; then
        echo "retaining inaccessible leased application release: $candidate"
        retained=$((retained + 1))
        continue
      fi
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
      if ! laplace_release_tree_writable "$candidate"; then
        echo "retaining inaccessible incomplete application release: $candidate"
        retained=$((retained + 1))
        continue
      fi
      if find "$candidate" -xdev -depth -delete; then
        echo "reclaimed incomplete application release: $candidate"
        reclaimed=$((reclaimed + 1))
      else
        echo "retaining incomplete application release after failed reclaim: $candidate"
        retained=$((retained + 1))
      fi
    else
      retained=$((retained + 1))
    fi
  done < <(find "$releases" -mindepth 1 -maxdepth 1 -xdev -type d -name 'runtime.*' -print0)
  echo "application release retention: reclaimed=$reclaimed retained=$retained"
}

# Seed a new release service directory from an older runtime using filesystem
# copy-on-write. Unlike a hardlink, the new file has its own inode: rsync --inplace
# can therefore rewrite changed build-form ELF blocks without mutating the running
# donor. On XFS this shares unchanged extents and allocates only changed blocks,
# avoiding the full-second-copy requirement that deadlocks a nearly-full app LV.
# A filesystem without reflink support simply returns false and uses normal rsync.
laplace_reflink_seed_runtime() {
  local app_dir="$1" service="$2" reference="$3" destination="$4"
  local reference_device destination_device resolved_destination
  [[ -d "$reference" && ! -L "$reference" && -d "$destination" && ! -L "$destination" ]] || return 1
  [[ "$reference" != "$destination" ]] || return 1
  resolved_destination="$(readlink -f "$destination" 2>/dev/null || true)"
  case "$resolved_destination" in
    "$app_dir"/releases/runtime.*/"$service") ;;
    *) echo "::error::refusing reflink seed outside managed release: $destination" >&2; return 1 ;;
  esac
  reference_device="$(stat -c '%d' "$reference" 2>/dev/null || true)"
  destination_device="$(stat -c '%d' "$destination" 2>/dev/null || true)"
  [[ -n "$reference_device" && "$reference_device" == "$destination_device" ]] || return 1

  if cp -R --reflink=always --preserve=mode,timestamps,links --no-preserve=ownership \
      "$reference/." "$destination/" 2>/dev/null; then
    echo "copy-on-write seeded $service runtime from $reference" >&2
    return 0
  fi

  # cp may have cloned a prefix before encountering an unsupported inode. This is
  # the unpublished destination owned by the current transaction; empty it before
  # another donor or ordinary rsync is attempted.
  find "$destination" -mindepth 1 -xdev -depth -delete || return 1
  return 1
}

# Stage one immutable service closure. Exact files can still be hardlinked from any
# retained content donor. When the target filesystem supports reflink, first clone an
# older build-form runtime and update that private COW inode in place. This matters for
# changed native ELFs: their installed form has a different RPATH, so /opt/laplace/lib
# is not byte identity and cannot be a --link-dest donor for the app-local build form.
laplace_stage_runtime_payload() {
  local app_dir="$1" service="$2" stable_link="$3" source_dir="$4" destination_dir="$5"
  local reference seeded=0
  local -a references=() link_dest=() transfer_options=(--checksum --no-times)

  while IFS= read -r reference; do
    [[ -n "$reference" && "$reference" != "$destination_dir" ]] || continue
    references+=("$reference")
    link_dest+=("--link-dest=$reference")
  done < <(laplace_runtime_reference_dirs "$app_dir" "$service" "$stable_link")

  for reference in "${references[@]}"; do
    if laplace_reflink_seed_runtime "$app_dir" "$service" "$reference" "$destination_dir"; then
      seeded=1
      break
    fi
  done
  if ((seeded)); then
    transfer_options+=(--inplace)
  fi

  if ((${#link_dest[@]} > 0)); then
    laplace_sync_payload "$source_dir" "$destination_dir" \
      "${transfer_options[@]}" "${link_dest[@]}"
  else
    laplace_sync_payload "$source_dir" "$destination_dir" "${transfer_options[@]}"
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
  # Rsync/link-dest may already have produced the desired inode identity. Treat
  # that state as success instead of creating another link and asking mv to
  # replace a pathname with the same inode (which GNU mv rejects as an error).
  [[ "$(stat -c '%d:%i' "$reference")" != "$(stat -c '%d:%i' "$destination")" ]] || return 0
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
    chmod 0755 "$release" || exit $?
    mkdir -m 0755 "$release/mcp" "$release/lichess" "$release/uci" || exit $?
    laplace_stage_runtime_payload "$app_dir" mcp "$app_dir/laplace-mcp" \
      "$mcp_stage" "$release/mcp" || exit $?
    laplace_stage_runtime_payload "$app_dir" lichess "$app_dir/laplace-lichess" \
      "$lichess_stage" "$release/lichess" || exit $?
    laplace_stage_runtime_payload "$app_dir" uci "$app_dir/laplace-uci" \
      "$uci_stage" "$release/uci" || exit $?
    install -m 0644 /dev/null "$release/.runtime-lease" || exit $?
    laplace_wrap_runtime_lease "$release/mcp/Laplace.Endpoints.Mcp" \
      "$(laplace_current_runtime_dir "$app_dir" mcp "$app_dir/laplace-mcp" || true)" || exit $?
    laplace_wrap_runtime_lease "$release/lichess/Laplace.Endpoints.Lichess" \
      "$(laplace_current_runtime_dir "$app_dir" lichess "$app_dir/laplace-lichess" || true)" || exit $?
    laplace_wrap_runtime_lease "$release/uci/laplace-uci" \
      "$(laplace_current_runtime_dir "$app_dir" uci "$app_dir/laplace-uci" || true)" || exit $?
    ln -s ../../../logs "$release/mcp/logs" || exit $?
    ln -s ../../../logs "$release/lichess/logs" || exit $?
    ln -s ../../../logs "$release/uci/logs" || exit $?
  ); then
    find "$release" -xdev -depth -delete || true
    return 1
  fi

  printf '%s\n' "$release"
}
