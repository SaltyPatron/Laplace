#!/usr/bin/env bash
# Discover the non-platform loader closure of the actual release libraries and
# executables. A missing dependency is a failed release, not an empty result.
native_runtime_libraries() {
  local result
  if ! result=$(ldd "$@" 2>&1); then
    printf '%s\n' "$result" >&2
    return 1
  fi
  if [[ "$result" == *'not found'* ]]; then
    printf '%s\n' "$result" >&2
    return 1
  fi
  awk '$2 == "=>" && $3 ~ "^/" {
    name=$3; sub(/^.*\//, "", name)
    if (name !~ /^(lib(c|m|dl|pthread|rt|gcc_s|stdc\+\+|resolv|util|anl)|ld-linux)[-.]/) print $3
  }' <<< "$result" | sort -u
}
