# Content fingerprints for change-aware phase skipping (sourced, not executed).
#
# A fingerprint is a sha256 over git's index blob ids for a pathspec plus live
# hashes of any dirty/untracked files under it — pure content, no mtimes, so
# touch(1) and checkout churn never invalidate anything. Stamps live under
# build/.stamps/<name> and are written ONLY after the guarded action succeeds:
# a failed or cancelled run leaves the stamp behind the content, so the work
# re-runs. `pipeline.sh clean` wipes build/ and with it every stamp.
#
# Escape hatch: LAPLACE_FORCE_ALL=1 makes every fp_check miss (CI: the
# force_all workflow_dispatch input).
#
# Contract: caller defines ROOT (repo root) before sourcing.

FP_STAMP_DIR="$ROOT/build/.stamps"

# The one definition of the native input domain: everything whose change means
# the engine/extension must rebuild, reinstall, and re-prove (ctest/regress).
FP_NATIVE_PATHS=(
  engine
  extension
  cmake
  CMakeLists.txt
  scripts/codegen-attestation-law.py
)

fp_compute() {
  # sha256 of the content state of the given pathspecs (repo-relative).
  # Index blobs cover tracked+staged state; unstaged edits and untracked
  # (non-ignored) files are hashed live; deletions leave a marker. Gitignored
  # files (build output, generated codegen) never enter the fingerprint.
  local f h
  {
    # Tooling self-hash: a bug fix here must bust every stamp it guards.
    sha256sum "$ROOT/scripts/lib/fp.sh" 2>/dev/null || echo "fp-lib-missing"
    git -C "$ROOT" ls-files -s -- "$@" 2>/dev/null || echo "no-git"
    while IFS= read -r f; do
      [[ -z "$f" ]] && continue
      if [[ -f "$ROOT/$f" ]]; then
        h=$(sha256sum <"$ROOT/$f" 2>/dev/null) || h="unreadable"
        printf 'dirty %s %s\n' "$f" "${h%% *}"
      else
        printf 'gone %s\n' "$f"
      fi
    done < <(git -C "$ROOT" diff --name-only -- "$@" 2>/dev/null)
    while IFS= read -r f; do
      [[ -z "$f" ]] && continue
      h=$(sha256sum <"$ROOT/$f" 2>/dev/null) || h="unreadable"
      printf 'new %s %s\n' "$f" "${h%% *}"
    done < <(git -C "$ROOT" ls-files --others --exclude-standard -- "$@" 2>/dev/null)
  } | LC_ALL=C sort | sha256sum | cut -d' ' -f1
}

fp_native() {
  fp_compute "${FP_NATIVE_PATHS[@]}"
}

fp_runtime() {
  # Salt for dotnet test staleness: app tests exercise the native .so, the
  # installed extension, and the migrated schema — any of those moving must
  # re-run tests even when no C# changed.
  fp_compute "${FP_NATIVE_PATHS[@]}" app/Laplace.Migrations
}

fp_check() {
  # fp_check <stamp-name> <fp> — 0 (skip is safe) iff stamp matches and no force.
  [[ "${LAPLACE_FORCE_ALL:-}" == "1" ]] && return 1
  local f="$FP_STAMP_DIR/$1"
  [[ -f "$f" && "$(cat "$f" 2>/dev/null)" == "$2" ]]
}

fp_record() {
  # fp_record <stamp-name> <fp> — call only after the guarded action succeeded.
  mkdir -p "$FP_STAMP_DIR"
  printf '%s' "$2" >"$FP_STAMP_DIR/$1"
}
