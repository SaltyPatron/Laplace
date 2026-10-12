#!/usr/bin/env bash
# Database commands use the installed host identity, without a build requirement.
source "$(dirname "${BASH_SOURCE[0]}")/installation.sh"
laplace_database_init() {
    local values reader
    reader="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/machine-env.py"
    values=$(python3 "$reader" --database) || return
    eval "$values"
    export PATH="${LAPLACE_PG_PREFIX:?Declare LAPLACE_PG_DIR or LAPLACE_PG_PREFIX}/bin:$PATH"
}
laplace_database_init
