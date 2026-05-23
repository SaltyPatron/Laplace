#!/bin/bash
# scripts/ci-init-submodules.sh
#
# Submodule init for self-hosted CI. Two rules:
#
#   1. .git/modules lives in /opt/laplace/submodule-modules, not in the
#      runner work folder. The work folder is scratch; the persistent
#      store survives wipes.
#
#   2. The persistent store is seeded from /home/ahart/Projects/Laplace
#      the first time it's empty. The runner already has every object on
#      disk via the dev workspace — no upstream re-download.
#
# After that, per submodule:
#   * fetch only if the persistent store's local clone is behind the
#     parent's pinned SHA;
#   * git submodule update --init --force;
#   * verify post-init HEAD == pinned, else fail naming the submodule.
#
# Skip list: external/tree-sitter-grammars/tree-sitter-nqc (upstream
# orphan gitlink at examples/nqc trips actions/checkout's recursive
# submodule cleanup).

set -euo pipefail
cd "$(dirname "$0")/.."

PERSISTENT_MODULES="${LAPLACE_SUBMODULE_MODULES:-/opt/laplace/submodule-modules}"
SEED_SOURCE="${LAPLACE_SUBMODULE_SEED:-/home/ahart/Projects/Laplace/.git/modules}"
SKIP=("external/tree-sitter-grammars/tree-sitter-nqc")

err() { printf '::error::%s\n' "$*" >&2; }

[ -f .gitmodules ] || { err "no .gitmodules at $(pwd)"; exit 2; }

mkdir -p "$PERSISTENT_MODULES"
[ -w "$PERSISTENT_MODULES" ] || { err "$PERSISTENT_MODULES not writable by $(id -un)"; exit 1; }

# Seed the persistent store from the dev workspace on first use.
if [ -z "$(ls -A "$PERSISTENT_MODULES" 2>/dev/null)" ] && [ -r "$SEED_SOURCE" ]; then
    rsync -aH "$SEED_SOURCE/" "$PERSISTENT_MODULES/"
fi

# .git/modules is always a symlink into the persistent store.
mkdir -p .git
rm -rf -- .git/modules
ln -s "$PERSISTENT_MODULES" .git/modules

is_skip() {
    local p="$1" s
    for s in "${SKIP[@]}"; do [ "$p" = "$s" ] && return 0; done
    return 1
}

mapfile -t names < <(
    git config -f .gitmodules --get-regexp '^submodule\..*\.path$' \
    | awk '{print $1}' | sed 's/^submodule\.//; s/\.path$//'
)

ok=0; skipped=0; fail=0
for name in "${names[@]}"; do
    path=$(git config -f .gitmodules "submodule.${name}.path")

    if is_skip "$path"; then
        rm -rf -- ".git/modules/$path" "$path"
        skipped=$((skipped + 1))
        continue
    fi

    pinned=$(git ls-tree HEAD "$path" 2>/dev/null | awk '{print $3}')
    if [ -z "$pinned" ]; then
        err "$path: no gitlink at HEAD"
        fail=$((fail + 1))
        continue
    fi

    mod_git=".git/modules/$path"

    # Fetch only if the local clone is behind the pinned SHA.
    if [ -d "$mod_git" ] && ! git --git-dir="$mod_git" cat-file -e "${pinned}^{commit}" 2>/dev/null; then
        git --git-dir="$mod_git" fetch --quiet --tags origin \
            '+refs/heads/*:refs/remotes/origin/*' >/dev/null
    fi

    if ! git submodule update --init --force -- "$path"; then
        err "$path: update failed (pinned $pinned, url $(git config -f .gitmodules "submodule.${name}.url"))"
        fail=$((fail + 1))
        continue
    fi

    actual=$(git -C "$path" rev-parse HEAD 2>/dev/null || true)
    if [ "$actual" != "$pinned" ]; then
        err "$path: post-init HEAD=$actual pinned=$pinned"
        fail=$((fail + 1))
        continue
    fi
    ok=$((ok + 1))
done

scripts/normalize-submodule-attributes.sh

printf 'ci-init-submodules: ok=%d skipped=%d fail=%d total=%d store=%s\n' \
       "$ok" "$skipped" "$fail" "${#names[@]}" "$PERSISTENT_MODULES"
[ "$fail" -eq 0 ]
