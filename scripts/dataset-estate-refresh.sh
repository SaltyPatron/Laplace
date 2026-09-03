#!/usr/bin/env bash
# Stage and verify dataset-estate refreshes without mutating admitted/live data.
#
# This operator deliberately stops before promotion/removal. The governing order is
# docs/plan/DATASET_ESTATE_MODERNIZATION.md: download -> verify -> extract/reconcile ->
# explicit install -> removal receipt. A successful download is never "admitted".
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SELF="$ROOT/scripts/dataset-estate-refresh.sh"
SOURCES="${LAPLACE_DATA_REFRESH_SOURCES:-$ROOT/scripts/dataset-estate-refresh.sources.psv}"
DATA_ROOT="${LAPLACE_DATA_ROOT:-/vault/Data}"
STAGE_ROOT="${LAPLACE_DATA_REFRESH_ROOT:-$DATA_ROOT/.refresh-20260903}"
JOB_ROOT="$STAGE_ROOT/.jobs"
CACHE_ROOT="$STAGE_ROOT/.git-cache"
RECEIPT="$STAGE_ROOT/REFRESH_RECEIPT.tsv"
LOCAL_HASHES="$STAGE_ROOT/DOWNLOADS.local.sha256"
LOCAL_INVENTORY="$STAGE_ROOT/STAGING_LOCAL.tsv"
CURRENT_JOB_ID=""
VERIFY_FAILURES=0

mkdir -p "$STAGE_ROOT" "$JOB_ROOT" "$CACHE_ROOT"

usage() {
    cat <<'EOF'
Usage: scripts/dataset-estate-refresh.sh <command> [args]

Safe staging commands:
  plan [lane]                 Show executable artifacts (lane: semantic|mutable|geo|safety|all).
  start <lane>                Start every artifact in a lane as resumable background jobs.
  start all                   Start semantic + mutable + geo + safety (large estates excluded).
  start syzygy6               Opt-in complete 6-man WDL+DTZ staging, sequential and resumable.
  status                      Show background job state and log paths.
  wait                        Wait for all started jobs; fail if any job failed or lost its exit receipt.
  verify [lane|all]           Re-verify every staged manifest artifact in the selected lane(s).
  adopt-existing              Hash/inventory the existing refresh tree without changing it.
  disk                        Show active/staging sizes and free vault capacity.

Explicit transitional active-worktree command:
  refresh-clean-repos         Fast-forward only known clean Git-backed active datasets.
                              Dirty/diverged/missing worktrees are SKIPPED, never reset.

Environment:
  LAPLACE_DATA_ROOT            active data root (default /vault/Data)
  LAPLACE_DATA_REFRESH_ROOT    staging root (default /vault/Data/.refresh-20260903)
  LAPLACE_DATA_REFRESH_SOURCES executable acquisition manifest override
  LAPLACE_SYZYGY6_WDL_BASE     mirror base for 6-man .rtbw files
  LAPLACE_SYZYGY6_DTZ_BASE     mirror base for 6-man .rtbz files

There is intentionally no `promote`, `replace`, or `delete` command here.
EOF
}

die() { printf 'dataset-estate-refresh: ERROR: %s\n' "$*" >&2; exit 1; }
note() { printf 'dataset-estate-refresh: %s\n' "$*"; }

need() {
    command -v "$1" >/dev/null 2>&1 || die "required command not found: $1"
}

safe_rel() {
    local rel="$1"
    [[ -n "$rel" && "$rel" != /* && "$rel" != ".." && "$rel" != ../* && "$rel" != */../* && "$rel" != */.. ]] \
        || die "unsafe relative path: $rel"
}

safe_stage_path() {
    local path="$1"
    case "$path" in
        "$STAGE_ROOT"/*) ;;
        *) die "refusing path outside refresh root: $path" ;;
    esac
}

ensure_receipt_header() {
    if [[ ! -e "$RECEIPT" ]]; then
        printf 'observed_at_utc\tid\tstate\tbytes\tsha256\tmd5\tpath\tsource\n' > "$RECEIPT"
    fi
}

record_receipt() {
    local id="$1" state="$2" path="$3" source="$4"
    local bytes sha md5 row
    bytes="$(stat -c '%s' "$path" 2>/dev/null || printf '0')"
    sha="$(sha256sum "$path" 2>/dev/null | awk '{print $1}' || true)"
    md5="$(md5sum "$path" 2>/dev/null | awk '{print $1}' || true)"
    row="$(printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
        "$(date -u +%FT%TZ)" "$id" "$state" "$bytes" "$sha" "$md5" \
        "${path#"$DATA_ROOT"/}" "$source")"
    ensure_receipt_header
    if command -v flock >/dev/null 2>&1; then
        { flock 9; printf '%s' "$row" >&9; } 9>>"$RECEIPT"
    else
        printf '%s' "$row" >> "$RECEIPT"
    fi
}

container_check() {
    local path="$1" logical="$2"
    case "$logical" in
        *.tar.bz2|*.tbz2) need bzip2; bzip2 -t "$path" ;;
        *.tgz|*.tar.gz) need gzip; gzip -t "$path" ;;
        *.jsonl.gz|*.xml.gz|*.ttl.gz|*.gz) need gzip; gzip -t "$path" ;;
        *.tar.xz|*.xz) need xz; xz -t "$path" ;;
        *.zip) need unzip; unzip -tq "$path" >/dev/null ;;
        *.tar) need tar; tar -tf "$path" >/dev/null ;;
        *) : ;;
    esac
}

verify_file() {
    local path="$1" expected_bytes="${2:-}" expected_sha="${3:-}" expected_md5="${4:-}" logical="${5:-$1}"
    [[ -f "$path" ]] || return 2

    if [[ -n "$expected_bytes" ]]; then
        local actual_bytes
        actual_bytes="$(stat -c '%s' "$path")"
        [[ "$actual_bytes" == "$expected_bytes" ]] || {
            printf 'size mismatch: %s expected=%s actual=%s\n' "$path" "$expected_bytes" "$actual_bytes" >&2
            return 1
        }
    fi
    if [[ -n "$expected_sha" ]]; then
        printf '%s  %s\n' "$expected_sha" "$path" | sha256sum -c - >/dev/null || return 1
    fi
    if [[ -n "$expected_md5" ]]; then
        printf '%s  %s\n' "$expected_md5" "$path" | md5sum -c - >/dev/null || return 1
    fi
    container_check "$path" "$logical"
}

write_sha_sidecar() {
    local path="$1" sidecar digest
    sidecar="$path.sha256"
    digest="$(sha256sum "$path" | awk '{print $1}')"
    printf '%s  %s\n' "$digest" "$(basename "$path")" > "$sidecar"
}

verify_sha_sidecar() {
    local path="$1" sidecar
    sidecar="$path.sha256"
    [[ -f "$path" && -f "$sidecar" ]] || return 1
    (cd "$(dirname "$path")" && sha256sum -c "$(basename "$sidecar")" >/dev/null)
}

_download() {
    local id="$1" rel="$2" url="$3" expected_bytes="${4:-}" expected_sha="${5:-}" expected_md5="${6:-}"
    local target part rc
    safe_rel "$rel"
    target="$STAGE_ROOT/$rel"
    part="$target.part"
    safe_stage_path "$target"
    mkdir -p "$(dirname "$target")"

    if [[ -e "$target" ]]; then
        if verify_file "$target" "$expected_bytes" "$expected_sha" "$expected_md5" "$target" 2>/dev/null; then
            note "$id already staged and verified: $target"
            record_receipt "$id" "verified-existing" "$target" "$url"
            return 0
        fi
        die "$id existing staged artifact failed verification; preserving it for inspection: $target"
    fi

    need curl
    note "$id downloading -> $target"
    set +e
    curl -fL --retry 6 --retry-delay 5 --retry-all-errors --connect-timeout 30 \
        --continue-at - --output "$part" "$url"
    rc=$?
    set -e
    if [[ "$rc" -ne 0 ]]; then
        # Some servers do not honor byte ranges. One clean restart of the partial
        # file is safe; an already-finalized artifact above is never overwritten.
        note "$id resume failed rc=$rc; retrying partial once from byte zero"
        rm -f -- "$part"
        curl -fL --retry 6 --retry-delay 5 --retry-all-errors --connect-timeout 30 \
            --output "$part" "$url"
    fi

    verify_file "$part" "$expected_bytes" "$expected_sha" "$expected_md5" "$target" \
        || die "$id failed size/hash/container verification"
    mv -- "$part" "$target"
    record_receipt "$id" "staged-verified" "$target" "$url"
    note "$id staged and verified"
}

resolve_git_ref() {
    local repo="$1" ref="$2" sha=""
    need git
    if [[ "$ref" == "HEAD" || -z "$ref" ]]; then
        sha="$(git ls-remote "$repo" HEAD | awk 'NR==1 {print $1}')"
    elif [[ "$ref" =~ ^[0-9a-fA-F]{40}$ ]]; then
        sha="${ref,,}"
    else
        sha="$(git ls-remote "$repo" "refs/heads/$ref" | awk 'NR==1 {print $1}')"
        if [[ -z "$sha" ]]; then
            sha="$(git ls-remote "$repo" "refs/tags/$ref^{}" | awk 'NR==1 {print $1}')"
        fi
        if [[ -z "$sha" ]]; then
            sha="$(git ls-remote "$repo" "refs/tags/$ref" | awk 'NR==1 {print $1}')"
        fi
    fi
    [[ "$sha" =~ ^[0-9a-fA-F]{40}$ ]] || die "cannot resolve Git ref '$ref' from $repo"
    printf '%s\n' "${sha,,}"
}

_git_snapshot() {
    local id="$1" rel="$2" repo="$3" ref="$4"
    local sha cache dest part
    safe_rel "$rel"
    sha="$(resolve_git_ref "$repo" "$ref")"
    cache="$CACHE_ROOT/$id.git"
    dest="$STAGE_ROOT/$rel/${id}-${sha}.tar.gz"
    part="$dest.part"
    safe_stage_path "$cache"
    safe_stage_path "$dest"
    mkdir -p "$(dirname "$dest")"

    if [[ -e "$dest" ]]; then
        if verify_sha_sidecar "$dest" && container_check "$dest" "$dest"; then
            note "$id Git snapshot already present: $sha"
            record_receipt "$id" "verified-existing" "$dest" "$repo@$sha"
            return 0
        fi
        # A previous operator revision finalized the archive before attempting to
        # write its sidecar, then failed under set -u. Recover only that exact,
        # independently checkable state; every other incomplete state remains
        # preserved and fails closed.
        if [[ ! -e "$dest.sha256" && -f "$dest.commit.tsv" ]] \
            && [[ "$(awk -F '\t' 'NR == 1 { print $1 }' "$dest.commit.tsv")" == "$sha" ]] \
            && [[ "$(awk -F '\t' 'NR == 1 { print $2 }' "$dest.commit.tsv")" == "$repo" ]] \
            && container_check "$dest" "$dest"; then
            write_sha_sidecar "$dest"
            note "$id recovered verified Git snapshot sidecar: $sha"
            record_receipt "$id" "recovered-verified" "$dest" "$repo@$sha"
            return 0
        fi
        die "$id existing Git snapshot or SHA-256 sidecar is invalid; preserving: $dest"
    fi

    need git
    need gzip
    if [[ ! -d "$cache" ]]; then
        git init --bare -q "$cache"
        git -C "$cache" remote add origin "$repo"
    elif ! git -C "$cache" remote get-url origin >/dev/null 2>&1; then
        git -C "$cache" remote add origin "$repo"
    fi
    git -C "$cache" fetch -q --force --depth=1 origin "$sha"
    git -C "$cache" cat-file -e "$sha^{commit}"
    git -C "$cache" archive --format=tar --prefix="${id}-${sha}/" "$sha" | gzip -n > "$part"
    container_check "$part" "$dest"
    mv -- "$part" "$dest"
    printf '%s\t%s\t%s\n' "$sha" "$repo" "$(date -u +%FT%TZ)" > "$dest.commit.tsv"
    write_sha_sidecar "$dest"
    record_receipt "$id" "staged-verified" "$dest" "$repo@$sha"
    note "$id Git snapshot staged at commit $sha"
}

_git_lfs_snapshot() {
    local id="$1" rel="$2" repo="$3" ref="$4"
    local sha work dest tmp files_manifest
    safe_rel "$rel"
    need git
    git lfs version >/dev/null 2>&1 || die "$id requires git-lfs"
    sha="$(resolve_git_ref "$repo" "$ref")"
    work="$CACHE_ROOT/$id.work"
    dest="$STAGE_ROOT/$rel/${id}-${sha}"
    tmp="$dest.part.$$"
    files_manifest="$dest.files.sha256"
    safe_stage_path "$work"
    safe_stage_path "$dest"
    safe_stage_path "$tmp"
    mkdir -p "$(dirname "$dest")"

    if [[ -e "$dest" || -e "$files_manifest" ]]; then
        if [[ -d "$dest" && -f "$files_manifest" ]] \
            && (cd "$dest" && sha256sum -c "$files_manifest" >/dev/null); then
            note "$id LFS snapshot already present: $sha"
            record_receipt "$id" "verified-existing" "$files_manifest" "$repo@$sha"
            return 0
        fi
        die "$id existing LFS snapshot/manifest is incomplete or invalid; preserving: $dest"
    fi

    rm -rf -- "$work" "$tmp"
    GIT_LFS_SKIP_SMUDGE=1 git clone -q --no-checkout "$repo" "$work"
    git -C "$work" fetch -q --force --depth=1 origin "$sha"
    git -C "$work" checkout -q --detach "$sha"
    git -C "$work" lfs pull
    git -C "$work" lfs fsck

    mkdir -p "$tmp"
    if command -v rsync >/dev/null 2>&1; then
        rsync -a --exclude='.git' "$work/" "$tmp/"
    else
        (cd "$work" && tar --exclude='./.git' -cf - .) | (cd "$tmp" && tar -xf -)
    fi
    [[ ! -e "$tmp/.git" ]] || die "$id staged LFS tree leaked .git metadata"
    mv -- "$tmp" "$dest"
    (cd "$dest" && find . -type f -print0 | LC_ALL=C sort -z | xargs -0 -r sha256sum) > "$files_manifest"
    printf '%s\t%s\t%s\n' "$sha" "$repo" "$(date -u +%FT%TZ)" > "$dest.commit.tsv"
    rm -rf -- "$work"
    record_receipt "$id" "staged-verified" "$files_manifest" "$repo@$sha"
    note "$id LFS snapshot staged at commit $sha"
}

row_worker() {
    local lane="$1" id="$2" kind="$3" rel="$4" source="$5" ref="$6" bytes="$7" sha="$8" md5="$9"
    case "$kind" in
        url) _download "$id" "$rel" "$source" "$bytes" "$sha" "$md5" ;;
        git) _git_snapshot "$id" "$rel" "$source" "$ref" ;;
        git-lfs) _git_lfs_snapshot "$id" "$rel" "$source" "$ref" ;;
        *) die "unknown acquisition kind '$kind' for $id ($lane)" ;;
    esac
}

job_alive() {
    local id="$1" pid_file pid
    pid_file="$JOB_ROOT/$id.pid"
    [[ -f "$pid_file" ]] || return 1
    pid="$(cat "$pid_file" 2>/dev/null || true)"
    [[ "$pid" =~ ^[0-9]+$ ]] || return 1
    kill -0 "$pid" 2>/dev/null
}

start_job() {
    local id="$1"; shift
    [[ "$id" =~ ^[A-Za-z0-9._-]+$ ]] || die "unsafe job id: $id"
    if job_alive "$id"; then
        note "$id already running pid=$(cat "$JOB_ROOT/$id.pid")"
        return 0
    fi
    rm -f -- "$JOB_ROOT/$id.rc"
    nohup "$SELF" _job "$id" "$@" >"$JOB_ROOT/$id.log" 2>&1 </dev/null &
    printf '%s\n' "$!" > "$JOB_ROOT/$id.pid"
    note "started $id pid=$! log=$JOB_ROOT/$id.log"
}

write_job_result_on_exit() {
    local rc=$?
    trap - EXIT
    if [[ -n "$CURRENT_JOB_ID" ]]; then
        printf '%s\n' "$rc" > "$JOB_ROOT/$CURRENT_JOB_ID.rc.tmp.$$"
        mv -f -- "$JOB_ROOT/$CURRENT_JOB_ID.rc.tmp.$$" "$JOB_ROOT/$CURRENT_JOB_ID.rc"
    fi
    exit "$rc"
}

_job() {
    CURRENT_JOB_ID="$1"
    shift
    [[ "$CURRENT_JOB_ID" =~ ^[A-Za-z0-9._-]+$ ]] || die "unsafe job id: $CURRENT_JOB_ID"
    trap write_job_result_on_exit EXIT
    "$@"
}

for_rows() {
    local wanted="$1" callback="$2"
    local lane id kind rel source ref bytes sha md5
    [[ -f "$SOURCES" ]] || die "source manifest missing: $SOURCES"
    while IFS='|' read -r lane id kind rel source ref bytes sha md5; do
        [[ -z "$lane" || "$lane" == \#* ]] && continue
        if [[ "$wanted" != "all" && "$lane" != "$wanted" ]]; then
            continue
        fi
        "$callback" "$lane" "$id" "$kind" "$rel" "$source" "$ref" "$bytes" "$sha" "$md5"
    done < "$SOURCES"
}

print_row() {
    local lane="$1" id="$2" kind="$3" rel="$4" source="$5" ref="$6" bytes="$7" sha="$8" md5="$9"
    printf '%-9s %-28s %-7s %s\n' "$lane" "$id" "$kind" "$rel"
    printf '           source=%s%s%s%s\n' "$source" \
        "${ref:+ ref=$ref}" "${bytes:+ bytes=$bytes}" "${sha:+ sha256=$sha}${md5:+ md5=$md5}"
}

start_row() {
    local lane="$1" id="$2" kind="$3" rel="$4" source="$5" ref="$6" bytes="$7" sha="$8" md5="$9"
    start_job "$id" row_worker "$lane" "$id" "$kind" "$rel" "$source" "$ref" "$bytes" "$sha" "$md5"
}

verify_row() {
    local lane="$1" id="$2" kind="$3" rel="$4" source="$5" ref="$6" bytes="$7" sha="$8" md5="$9"
    local target status="BAD/MISSING"
    case "$kind" in
        url)
            target="$STAGE_ROOT/$rel"
            if verify_file "$target" "$bytes" "$sha" "$md5" "$target" 2>/dev/null; then status="OK"; fi
            ;;
        git)
            target="$(find "$STAGE_ROOT/$rel" -maxdepth 1 -type f -name "${id}-*.tar.gz" 2>/dev/null | LC_ALL=C sort | tail -1 || true)"
            if [[ -n "$target" ]] && verify_sha_sidecar "$target" && container_check "$target" "$target"; then status="OK"; fi
            ;;
        git-lfs)
            target="$(find "$STAGE_ROOT/$rel" -maxdepth 1 -type d -name "${id}-*" 2>/dev/null | LC_ALL=C sort | tail -1 || true)"
            if [[ -n "$target" && -f "$target.files.sha256" ]] \
                && (cd "$target" && sha256sum -c "$target.files.sha256" >/dev/null 2>&1); then status="OK"; fi
            ;;
    esac
    printf '%-9s %-28s %s\n' "$lane" "$id" "$status"
    if [[ "$status" != "OK" ]]; then
        VERIFY_FAILURES=1
    fi
    return 0
}

status_jobs() {
    local any=0 pid_file id pid rc state
    shopt -s nullglob
    for pid_file in "$JOB_ROOT"/*.pid; do
        any=1
        id="$(basename "$pid_file" .pid)"
        pid="$(cat "$pid_file" 2>/dev/null || true)"
        if [[ "$pid" =~ ^[0-9]+$ ]] && kill -0 "$pid" 2>/dev/null; then
            state="RUNNING"
        elif [[ -f "$JOB_ROOT/$id.rc" ]]; then
            rc="$(cat "$JOB_ROOT/$id.rc")"
            if [[ "$rc" == "0" ]]; then state="DONE"; else state="FAILED(rc=$rc)"; fi
        else
            state="ENDED(no receipt)"
        fi
        printf '%-30s %-18s pid=%-8s log=%s\n' "$id" "$state" "${pid:-?}" "$JOB_ROOT/$id.log"
    done
    shopt -u nullglob
    [[ "$any" -eq 1 ]] || note "no refresh jobs have been started"
}

wait_jobs() {
    local failed=0 running=1
    while [[ "$running" -eq 1 ]]; do
        running=0
        local pid_file id
        shopt -s nullglob
        for pid_file in "$JOB_ROOT"/*.pid; do
            id="$(basename "$pid_file" .pid)"
            if job_alive "$id"; then running=1; fi
        done
        shopt -u nullglob
        [[ "$running" -eq 0 ]] || sleep 5
    done

    local pid_file id rc
    shopt -s nullglob
    for pid_file in "$JOB_ROOT"/*.pid; do
        id="$(basename "$pid_file" .pid)"
        if [[ ! -f "$JOB_ROOT/$id.rc" ]]; then
            failed=1
            continue
        fi
        rc="$(cat "$JOB_ROOT/$id.rc")"
        [[ "$rc" == "0" ]] || failed=1
    done
    shopt -u nullglob
    status_jobs
    [[ "$failed" -eq 0 ]]
}

adopt_existing() {
    need sha256sum
    need stat
    local tmp_hash tmp_inv rel path bytes sha
    tmp_hash="$LOCAL_HASHES.tmp.$$"
    tmp_inv="$LOCAL_INVENTORY.tmp.$$"
    : > "$tmp_hash"
    printf 'relative_path\tbytes\tsha256\n' > "$tmp_inv"

    while IFS= read -r -d '' path; do
        rel="${path#"$STAGE_ROOT"/}"
        case "$rel" in
            .jobs/*|.git-cache/*|*.part|*.part.*|DOWNLOADS.local.sha256|STAGING_LOCAL.tsv|REFRESH_RECEIPT.tsv) continue ;;
        esac
        bytes="$(stat -c '%s' "$path")"
        sha="$(sha256sum "$path" | awk '{print $1}')"
        printf '%s  %s\n' "$sha" "$rel" >> "$tmp_hash"
        printf '%s\t%s\t%s\n' "$rel" "$bytes" "$sha" >> "$tmp_inv"
    done < <(find "$STAGE_ROOT" -type f -print0 | LC_ALL=C sort -z)

    mv -f -- "$tmp_hash" "$LOCAL_HASHES"
    mv -f -- "$tmp_inv" "$LOCAL_INVENTORY"
    note "existing staging inventory written: $LOCAL_INVENTORY"
    note "existing staging hashes written:    $LOCAL_HASHES"
    note "this records byte identity only; it does NOT mark artifacts admitted"
}

refresh_clean_repos() {
    need git
    # Transitional convenience only. The final admitted estate is immutable and
    # contains no .git. These paths are known existing Git-backed sources; dirty or
    # diverged trees are evidence and must not be reset by an update helper.
    local -a specs=(
        "CILI|$DATA_ROOT/CILI|https://github.com/globalwordnet/cili.git|a895d7ecb18019dda3443f98901e59d81ce8722b"
        "SemLink|$DATA_ROOT/SemLink|https://github.com/cu-clear/semlink.git|HEAD"
        "VerbNet|$DATA_ROOT/VerbNet|https://github.com/cu-clear/verbnet.git|HEAD"
        "PropBank|$DATA_ROOT/PropBank|https://github.com/propbank/propbank-frames.git|HEAD"
        "LichessOpenings|$DATA_ROOT/Games/Chess/lichess-openings|https://github.com/lichess-org/chess-openings.git|4b8622759e7ae6f93f011cc6c83a3823401ab45e"
    )
    local spec id path repo ref target before after
    for spec in "${specs[@]}"; do
        IFS='|' read -r id path repo ref <<< "$spec"
        if [[ ! -d "$path/.git" ]]; then
            printf '%-18s SKIP no Git worktree: %s\n' "$id" "$path"
            continue
        fi
        if [[ -n "$(git -C "$path" status --porcelain=v1 --untracked-files=all)" ]]; then
            printf '%-18s SKIP dirty worktree: %s\n' "$id" "$path"
            continue
        fi
        target="$(resolve_git_ref "$repo" "$ref")"
        before="$(git -C "$path" rev-parse HEAD)"
        git -C "$path" fetch -q "$repo" "$target"
        git -C "$path" cat-file -e "$target^{commit}"
        if ! git -C "$path" merge-base --is-ancestor "$before" "$target"; then
            printf '%-18s SKIP current HEAD is not ancestor of target %s\n' "$id" "$target"
            continue
        fi
        git -C "$path" merge -q --ff-only "$target"
        after="$(git -C "$path" rev-parse HEAD)"
        printf '%-18s FF %s -> %s\n' "$id" "${before:0:12}" "${after:0:12}"
    done
}

syzygy6_worker() {
    # Full six-men is intentionally opt-in. Download one file at a time so this
    # does not hammer a public mirror. Official filename->MD5 lists drive both the
    # artifact roster and verification. The roster source itself is commit-pinned.
    local wdl_base="${LAPLACE_SYZYGY6_WDL_BASE:-https://tablebase.lichess.ovh/tables/standard/6-wdl/}"
    local dtz_base="${LAPLACE_SYZYGY6_DTZ_BASE:-https://tablebase.lichess.ovh/tables/standard/6-dtz/}"
    local root checks
    root="$STAGE_ROOT/Syzygy-6-men"
    checks="$root/checksums"
    local roster_commit="0bb8aeee525f364bb750f96df312a1a7c9b54398"
    local free_bytes min_free=$((180 * 1024 * 1024 * 1024))
    mkdir -p "$root/WDL" "$root/DTZ" "$checks"
    free_bytes="$(df -PB1 "$STAGE_ROOT" | awk 'NR==2 {print $4}')"
    (( free_bytes >= min_free )) || die "Syzygy 6-man staging requires at least 180 GiB free; have $free_bytes bytes"

    _download "syzygy6-wdl-checksums" "Syzygy-6-men/checksums/wdl6.txt" \
        "https://raw.githubusercontent.com/syzygy1/tb/$roster_commit/checksums/wdl6.txt" "" "" ""
    _download "syzygy6-dtz-checksums" "Syzygy-6-men/checksums/dtz6.txt" \
        "https://raw.githubusercontent.com/syzygy1/tb/$roster_commit/checksums/dtz6.txt" "" "" ""

    local list kind base file expected target part actual count=0
    for kind in WDL DTZ; do
        if [[ "$kind" == "WDL" ]]; then list="$checks/wdl6.txt"; base="$wdl_base"; else list="$checks/dtz6.txt"; base="$dtz_base"; fi
        while IFS=':' read -r file expected; do
            file="${file//[$'\r\n\t ']/}"
            expected="${expected//[$'\r\n\t ']/}"
            [[ -n "$file" && "$expected" =~ ^[0-9a-fA-F]{32}$ ]] || continue
            target="$root/$kind/$file"
            part="$target.part"
            if [[ -e "$target" ]]; then
                if [[ -f "$target" ]] && [[ "$(md5sum "$target" | awk '{print $1}')" == "${expected,,}" ]]; then
                    ((count+=1))
                    continue
                fi
                die "existing Syzygy artifact failed MD5; preserving it for inspection: $target"
            fi
            note "Syzygy6 $kind $file"
            set +e
            curl -fL --retry 8 --retry-delay 8 --retry-all-errors --continue-at - -o "$part" "$base$file"
            actual=$?
            set -e
            if [[ "$actual" -ne 0 ]]; then
                rm -f -- "$part"
                curl -fL --retry 8 --retry-delay 8 --retry-all-errors -o "$part" "$base$file"
            fi
            [[ "$(md5sum "$part" | awk '{print $1}')" == "${expected,,}" ]] || die "Syzygy checksum mismatch: $file"
            mv -- "$part" "$target"
            ((count+=1))
        done < "$list"
    done
    [[ "$count" -eq 730 ]] || die "Syzygy 6-man roster incomplete: expected 730 verified files, saw $count"
    note "Syzygy 6-man staged: 730/730 WDL+DTZ files verified against upstream MD5 lists"
}

show_disk() {
    printf 'Active root:  %s\n' "$DATA_ROOT"
    printf 'Stage root:   %s\n' "$STAGE_ROOT"
    du -sh "$STAGE_ROOT" 2>/dev/null || true
    df -h "$DATA_ROOT"
}

cmd="${1:-help}"
shift || true
case "$cmd" in
    help|-h|--help) usage ;;
    plan)
        lane="${1:-all}"
        [[ "$lane" =~ ^(semantic|mutable|geo|safety|all)$ ]] || die "unknown lane: $lane"
        for_rows "$lane" print_row
        ;;
    start)
        lane="${1:-}"
        [[ -n "$lane" ]] || die "start requires a lane"
        case "$lane" in
            semantic|mutable|geo|safety) for_rows "$lane" start_row ;;
            all)
                for_rows semantic start_row
                for_rows mutable start_row
                for_rows geo start_row
                for_rows safety start_row
                ;;
            syzygy6) start_job syzygy6 syzygy6_worker ;;
            *) die "unknown start lane: $lane" ;;
        esac
        ;;
    status) status_jobs ;;
    wait) wait_jobs || die "one or more refresh jobs failed or ended without an exit receipt" ;;
    verify)
        lane="${1:-all}"
        [[ "$lane" =~ ^(semantic|mutable|geo|safety|all)$ ]] || die "unknown verify lane: $lane"
        VERIFY_FAILURES=0
        for_rows "$lane" verify_row
        [[ "$VERIFY_FAILURES" -eq 0 ]] || die "one or more staged artifacts failed verification"
        ;;
    adopt-existing) adopt_existing ;;
    refresh-clean-repos) refresh_clean_repos ;;
    disk) show_disk ;;
    _job) _job "$@" ;;
    row_worker) row_worker "$@" ;;
    syzygy6_worker) syzygy6_worker ;;
    *) usage >&2; die "unknown command: $cmd" ;;
esac
