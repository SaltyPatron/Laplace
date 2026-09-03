#!/usr/bin/env python3
"""Static contract tests for the dataset-estate staging operator."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = (ROOT / "scripts" / "dataset-estate-refresh.sh").read_text()
SOURCES = (ROOT / "scripts" / "dataset-estate-refresh.sources.psv").read_text().splitlines()


def require(fragment: str) -> None:
    assert fragment in SCRIPT, f"missing refresh-operator contract: {fragment!r}"


def main() -> int:
    require('STAGE_ROOT="${LAPLACE_DATA_REFRESH_ROOT:-$DATA_ROOT/.refresh-20260903}"')
    require('start syzygy6')
    require('adopt-existing')
    require('refresh-clean-repos')
    require('git -C "$path" merge -q --ff-only "$target"')
    require('status_jobs')
    require('wait_jobs')
    require('verify_file')
    require('730')
    require('There is intentionally no `promote`, `replace`, or `delete` command here.')

    # The normal acquisition path must target the refresh root, never an active dataset.
    assert 'target="$STAGE_ROOT/$rel"' in SCRIPT
    assert 'safe_stage_path "$target"' in SCRIPT
    assert 'rm -rf -- "$DATA_ROOT' not in SCRIPT
    assert 'rm -f -- "$DATA_ROOT' not in SCRIPT

    ids: set[str] = set()
    rows = 0
    for line in SOURCES:
        if not line or line.startswith("#"):
            continue
        parts = line.split("|")
        assert len(parts) == 9, f"bad source manifest width ({len(parts)}): {line}"
        lane, ident, kind, rel, source, ref, expected_bytes, sha256, md5 = parts
        assert lane in {"semantic", "mutable", "geo", "safety"}
        assert kind in {"url", "git", "git-lfs", "ud218"}
        assert ident and ident not in ids, f"duplicate id: {ident}"
        ids.add(ident)
        assert rel and not rel.startswith("/") and ".." not in Path(rel).parts
        assert source.startswith("https://")
        assert not re.search(r"(^|/)7(?:-men)?(?:/|$)", rel, re.I), "7-man Syzygy must stay excluded"
        if expected_bytes:
            assert expected_bytes.isdigit() and int(expected_bytes) > 0
        if sha256:
            assert re.fullmatch(r"[0-9a-f]{64}", sha256)
        if md5:
            assert re.fullmatch(r"[0-9a-f]{32}", md5)
        rows += 1

    assert rows >= 40, f"executable estate unexpectedly small: {rows} rows"
    print(f"DATASET_ESTATE_REFRESH_CONTRACT_OK rows={rows} unique_ids={len(ids)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
