#!/usr/bin/env bash
# Import public configuration as quoted data. A directory need not be a mount point.
laplace_installation_init() {
    local reader checkout values
    reader="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/machine-env.py"
    checkout="${ROOT:-$(dirname "$(dirname "$reader")")}"
    values=$(python3 "$reader" --paths --checkout "$checkout") || return
    eval "$values"
}

laplace_installation_init
