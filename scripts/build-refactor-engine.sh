#!/usr/bin/env bash
set -euo pipefail

# The legacy product is a transport/storage compatibility shell during the
# convergence. Cognition has one semantic owner: SaltyPatron/Laplace-Refactor.
# Pin exact source identities so a normal setup-host/build never depends on a
# moving branch or on an operator pre-populating a checkout.
REF_REPO="https://github.com/SaltyPatron/Laplace-Refactor.git"
REF_REVISION="1f803d0cc6cea5fd5a9f83c8f29dcb81f5ff8a2b"
BLAKE3_REPO="https://github.com/BLAKE3-team/BLAKE3.git"
BLAKE3_REVISION="f3149ec5bb5449af877ba20377a11008ff499fa2"
TREE_SITTER_REPO="https://github.com/tree-sitter/tree-sitter.git"
TREE_SITTER_REVISION="d97971e24500218865c05ed1febdee2acf41bae1"

EXTERNAL_ROOT="${LAPLACE_EXTERNAL:-/build/external}"
WORK_ROOT="${LAPLACE_WORK_ROOT:-/opt/laplace/work}"
INSTALL_ROOT="${LAPLACE_REFACTOR_ENGINE_PREFIX:-/opt/laplace/refactor-engine}"
SOURCE_ROOT="$EXTERNAL_ROOT/laplace-refactor-engine"
BLAKE3_ROOT="$EXTERNAL_ROOT/laplace-refactor-blake3"
TREE_SITTER_ROOT="$EXTERNAL_ROOT/laplace-refactor-tree-sitter"
BUILD_ROOT="$WORK_ROOT/laplace-refactor-engine-build"
STAMP="$INSTALL_ROOT/.laplace-refactor-engine-revision"

clone_exact() {
    local repo="$1" revision="$2" destination="$3"
    if [ -d "$destination/.git" ]; then
        local actual
        actual="$(git -C "$destination" rev-parse HEAD 2>/dev/null || true)"
        if [ "$actual" = "$revision" ]; then
            return 0
        fi
    else
        rm -rf "$destination"
        mkdir -p "$destination"
        git -C "$destination" init -q
        git -C "$destination" remote add origin "$repo"
    fi

    # Fetch only the immutable object we need. The checkout is detached on
    # purpose; build identity is the commit, never a local branch name.
    git -C "$destination" fetch -q --depth 1 origin "$revision"
    git -C "$destination" checkout -q --detach FETCH_HEAD
    local actual
    actual="$(git -C "$destination" rev-parse HEAD)"
    if [ "$actual" != "$revision" ]; then
        echo "refactor-engine: expected $revision, got $actual in $destination" >&2
        exit 1
    fi
}

if [ -f "$STAMP" ] \
   && [ "$(cat "$STAMP")" = "$REF_REVISION" ] \
   && [ -f "$INSTALL_ROOT/lib/liblaplace_engine.so" ] \
   && [ -f "$INSTALL_ROOT/include/laplace/cognition_observation_request.h" ]; then
    echo "refactor-engine: $REF_REVISION already installed"
    exit 0
fi

mkdir -p "$EXTERNAL_ROOT" "$WORK_ROOT" "$INSTALL_ROOT"
clone_exact "$REF_REPO" "$REF_REVISION" "$SOURCE_ROOT"
clone_exact "$BLAKE3_REPO" "$BLAKE3_REVISION" "$BLAKE3_ROOT"
clone_exact "$TREE_SITTER_REPO" "$TREE_SITTER_REVISION" "$TREE_SITTER_ROOT"

rm -rf "$BUILD_ROOT"
cmake -S "$SOURCE_ROOT" -B "$BUILD_ROOT" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$INSTALL_ROOT" \
    -DBUILD_TESTING=OFF \
    -DLAPLACE_OUTPUT_ROOT="$BUILD_ROOT/output" \
    -DLAPLACE_BLAKE3_SOURCE="$BLAKE3_ROOT" \
    -DLAPLACE_TREE_SITTER_SOURCE="$TREE_SITTER_ROOT"
cmake --build "$BUILD_ROOT" --target laplace_engine laplace_unicode_numeric --parallel "${LAPLACE_BUILD_JOBS:-2}"
cmake --install "$BUILD_ROOT"

if [ ! -f "$INSTALL_ROOT/lib/liblaplace_engine.so" ] \
   || [ ! -f "$INSTALL_ROOT/include/laplace/cognition_observation_request.h" ]; then
    echo "refactor-engine: install did not produce canonical engine/header" >&2
    exit 1
fi
printf '%s\n' "$REF_REVISION" > "$STAMP"
echo "refactor-engine: installed $REF_REVISION at $INSTALL_ROOT"
