#!/bin/bash
# scripts/ci-init-submodules.sh
#
# Move submodule gitdir storage OUT of the GitHub Actions runner work folder.
#
# The agent treats $GITHUB_WORKSPACE as scratch. Anything that lands inside
# .git/modules/ inside the workspace is one `rm -rf` away from being gone
# forever — and historically the workflow has done exactly that, forcing
# a 300+ submodule re-clone on every push.
#
# Fix: replace $GITHUB_WORKSPACE/.git/modules with a symlink into a
# persistent host-side directory ($LAPLACE_SUBMODULE_MODULES, default
# /opt/laplace/submodule-modules — Layer-0-provisioned, owned by
# laplace-runner). All git-submodule operations then write through the
# symlink into persistent storage. The workspace can be wiped freely; the
# gitdirs survive.
#
# Per submodule the invariant is:
#
#   1. .git/modules/<path>           (lives in persistent store via symlink)
#      contains the pinned SHA from `git ls-tree HEAD <path>`.
#   2. <path>                        (workspace working tree)
#      is checked out at that SHA.
#
# Three checks enforce it:
#
#   * If the persistent module dir is behind a pin bump, fetch heads + tags
#     (and the pinned SHA explicitly as a final fallback) before update.
#   * If `git submodule update --init --force` still aborts, wipe the
#     module dir + working tree once and re-clone from upstream.
#   * Verify post-init HEAD matches the pin; mismatch is a hard error.
#
# If the pinned SHA is not reachable from the configured upstream URL
# (force-push at origin, fork URL gone, etc.) the script fails loudly
# naming the offending submodule. The pin is wrong in that case, not
# the script.
#
# Skip list — submodules deliberately NOT initialized:
#   * external/tree-sitter-grammars/tree-sitter-nqc — upstream has an
#     orphan gitlink at examples/nqc (no .gitmodules entry backing it)
#     which trips actions/checkout's recursive submodule cleanup. With
#     nqc absent from .git/modules/ and the working tree, the cleanup
#     walk never reaches the orphan.

set -euo pipefail
cd "$(dirname "$0")/.."

PERSISTENT_MODULES="${LAPLACE_SUBMODULE_MODULES:-/opt/laplace/submodule-modules}"
# Optional seed: a fully-populated .git/modules tree we can copy from
# the first time the persistent store is empty. Default is the dev
# workspace on this host; the runner user (laplace-runner) reaches it
# via /home/ahart traversal (mode 751) + the publicly-readable
# Projects/Laplace tree underneath. Override or unset to disable.
SEED_SOURCE="${LAPLACE_SUBMODULE_SEED:-/home/ahart/Projects/Laplace/.git/modules}"
SKIP_PATHS=(
    "external/tree-sitter-grammars/tree-sitter-nqc"
)

err() { printf '::error::%s\n' "$*" >&2; }

[ -f .gitmodules ] || { err "no .gitmodules at $(pwd) — run from repo root"; exit 2; }
[ -d .git ] || [ -f .git ] || { err "no .git at $(pwd)"; exit 2; }

# --- persistent gitdir store -------------------------------------------------
if [ ! -d "$PERSISTENT_MODULES" ]; then
    if ! mkdir -p "$PERSISTENT_MODULES" 2>/dev/null; then
        err "cannot create $PERSISTENT_MODULES — fix ownership of its parent in Layer 0 (scripts/bootstrap-laplace-runner.sh)"
        exit 1
    fi
fi
[ -w "$PERSISTENT_MODULES" ] || { err "$PERSISTENT_MODULES not writable by $(id -un)"; exit 1; }

# Seed the persistent store from an existing populated .git/modules tree
# (the dev workspace on this host) the first time it's empty. Avoids a
# 300+ submodule re-download from upstream on first CI run after
# Layer-0 provisioning. Best-effort: failure (perm denied, source
# missing, rsync absent) is silent and we fall through to normal init.
if [ -z "$(ls -A "$PERSISTENT_MODULES" 2>/dev/null)" ] \
   && [ -n "${SEED_SOURCE}" ] \
   && [ -d "${SEED_SOURCE}" ] \
   && [ -r "${SEED_SOURCE}" ]; then
    echo "::notice::seeding $PERSISTENT_MODULES from $SEED_SOURCE (one-time)"
    if command -v rsync >/dev/null 2>&1; then
        rsync -aH "$SEED_SOURCE/" "$PERSISTENT_MODULES/" 2>/dev/null || true
    else
        cp -aH "$SEED_SOURCE/." "$PERSISTENT_MODULES/" 2>/dev/null || true
    fi
fi

# Ensure .git/modules is a symlink to the persistent store. Three cases:
#   * already correct symlink   → no-op
#   * wrong-target symlink      → relink
#   * real directory            → migrate contents into persistent store, then relink
#   * absent                    → create symlink
ws_modules=".git/modules"
target_canon=$(readlink -f "$PERSISTENT_MODULES")
if [ -L "$ws_modules" ]; then
    if [ "$(readlink -f "$ws_modules")" != "$target_canon" ]; then
        rm -- "$ws_modules"
        ln -s "$PERSISTENT_MODULES" "$ws_modules"
    fi
elif [ -d "$ws_modules" ]; then
    # One-time migration. rsync preserves perms + hardlinks; --ignore-existing
    # keeps anything already on the persistent side (e.g. from another workspace).
    rsync -aH --ignore-existing "$ws_modules/" "$PERSISTENT_MODULES/" >/dev/null
    rm -rf -- "$ws_modules"
    ln -s "$PERSISTENT_MODULES" "$ws_modules"
elif [ -e "$ws_modules" ]; then
    err "$ws_modules exists and is neither a directory nor a symlink — refusing to touch it"
    exit 1
else
    ln -s "$PERSISTENT_MODULES" "$ws_modules"
fi

# --- helpers -----------------------------------------------------------------
is_skipped() {
    local p="$1" s
    for s in "${SKIP_PATHS[@]}"; do [ "$p" = "$s" ] && return 0; done
    return 1
}

init_one() {
    local name="$1"
    local path url pinned mod_git actual
    path=$(git config -f .gitmodules "submodule.${name}.path")
    url=$(git config -f .gitmodules  "submodule.${name}.url")

    if is_skipped "$path"; then
        rm -rf -- ".git/modules/$path" "$path"
        return 0
    fi

    pinned=$(git ls-tree HEAD "$path" 2>/dev/null | awk '{print $3}')
    if [ -z "$pinned" ]; then
        err "$path: no gitlink at HEAD"
        return 1
    fi

    mod_git=".git/modules/$path"   # → persistent store via symlink

    # If the persistent module dir is behind a pin bump, fetch before update.
    if [ -d "$mod_git" ]; then
        if ! git --git-dir="$mod_git" cat-file -e "${pinned}^{commit}" 2>/dev/null; then
            git --git-dir="$mod_git" fetch --quiet --tags origin \
                '+refs/heads/*:refs/remotes/origin/*' \
                '+refs/tags/*:refs/tags/*' >/dev/null 2>&1 \
                || git --git-dir="$mod_git" fetch --quiet origin "$pinned" >/dev/null 2>&1 \
                || true
        fi
    fi

    # Update. `--force` tolerates a dirty working tree from a partial prior run
    # (workspace can be wiped between runs — submodule working trees included).
    if ! git submodule update --init --force -- "$path" 2>/dev/null; then
        # Last resort — wipe persistent module dir + working tree and reclone.
        rm -rf -- "$mod_git" "$path"
        git submodule update --init -- "$path" || {
            err "$path: re-clone failed after wipe (pinned $pinned from $url)"
            return 1
        }
    fi

    actual=$(git -C "$path" rev-parse HEAD 2>/dev/null || true)
    if [ "$actual" != "$pinned" ]; then
        err "$path: post-init HEAD=$actual pinned=$pinned"
        return 1
    fi
    return 0
}

# --- main --------------------------------------------------------------------
mapfile -t names < <(
    git config -f .gitmodules --get-regexp '^submodule\..*\.path$' \
    | awk '{print $1}' \
    | sed 's/^submodule\.//; s/\.path$//'
)
total=${#names[@]}
[ "$total" -gt 0 ] || { echo "no submodules in .gitmodules"; exit 0; }

ok=0
fail=0
skipped=0
for name in "${names[@]}"; do
    path=$(git config -f .gitmodules "submodule.${name}.path")
    if is_skipped "$path"; then
        if init_one "$name"; then skipped=$((skipped + 1)); else fail=$((fail + 1)); fi
        continue
    fi
    if init_one "$name"; then
        ok=$((ok + 1))
    else
        fail=$((fail + 1))
    fi
done

# CRLF-fixture local overrides for the 5 grammars that fight git's
# text=lf normalization. Idempotent.
scripts/normalize-submodule-attributes.sh

printf 'ci-init-submodules: ok=%d skipped=%d fail=%d total=%d store=%s\n' \
       "$ok" "$skipped" "$fail" "$total" "$PERSISTENT_MODULES"
[ "$fail" -eq 0 ]
