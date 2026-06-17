#!/usr/bin/env python3
"""
Download code training datasets for Laplace ingest.

Usage:
    python download_code_data.py tiny-codes [--dest DIR]
    python download_code_data.py stack-v2   [--dest DIR] [--langs py,cpp,rs,...] [--shards N]

Defaults:
    tiny-codes dest:  D:/Data/Ingest/tiny-codes
    stack-v2 dest:    D:/Data/Ingest/stack-v2
    stack-v2 langs:   python,c,cpp,javascript,typescript,rust,go,c-sharp,java,ruby,julia,kotlin,swift,php,bash,sql
    stack-v2 shards:  5   (parquet files per language; each ~300-600 MB)
"""

import sys
import os
import argparse
from pathlib import Path

DEFAULT_TINY_DEST  = Path("D:/Data/Ingest/tiny-codes")
DEFAULT_STACK_DEST = Path("D:/Data/Ingest/stack-v2")



GRAMMAR_TO_STACK_LANG = {
    "python":      "Python",
    "c":           "C",
    "cpp":         "C++",
    "javascript":  "JavaScript",
    "typescript":  "TypeScript",
    "rust":        "Rust",
    "go":          "Go",
    "c-sharp":     "C#",
    "java":        "Java",
    "ruby":        "Ruby",
    "julia":       "Julia",
    "kotlin":      "Kotlin",
    "swift":       "Swift",
    "php":         "PHP",
    "bash":        "Shell",
    "sql":         "SQL",
}

ALL_GRAMMAR_IDS = list(GRAMMAR_TO_STACK_LANG.keys())


def download_tiny_codes(dest: Path):
    from huggingface_hub import snapshot_download
    print(f"[tiny-codes] downloading nampdn-ai/tiny-codes -> {dest}")
    snapshot_download(
        repo_id="nampdn-ai/tiny-codes",
        repo_type="dataset",
        local_dir=str(dest),
        ignore_patterns=["*.json", "*.md", "*.txt", ".gitattributes"],
    )
    parquets = list(dest.glob("**/*.parquet"))
    print(f"[tiny-codes] done — {len(parquets)} parquet file(s) at {dest}")


def download_stack_v2(dest: Path, grammar_ids: list[str], shards_per_lang: int):
    from huggingface_hub import list_repo_files, hf_hub_download

    stack_langs = [GRAMMAR_TO_STACK_LANG[g] for g in grammar_ids if g in GRAMMAR_TO_STACK_LANG]
    if not stack_langs:
        print("[stack-v2] no valid languages specified", file=sys.stderr)
        sys.exit(1)

    print(f"[stack-v2] scanning bigcode/the-stack-v2 for languages: {', '.join(stack_langs)}")
    print(f"[stack-v2] shards per language: {shards_per_lang}")
    print(f"[stack-v2] destination: {dest}")
    print()

    
    
    try:
        all_files = list(list_repo_files("bigcode/the-stack-v2", repo_type="dataset"))
    except Exception as e:
        print(f"[stack-v2] ERROR listing repo files: {e}", file=sys.stderr)
        print("[stack-v2] NOTE: bigcode/the-stack-v2 is gated — you must accept the terms at:", file=sys.stderr)
        print("           https://huggingface.co/datasets/bigcode/the-stack-v2", file=sys.stderr)
        print("           then run:  huggingface-cli login", file=sys.stderr)
        sys.exit(1)

    
    from collections import defaultdict
    import re
    by_lang: dict[str, list[str]] = defaultdict(list)
    for f in all_files:
        if not f.endswith(".parquet"):
            continue
        
        parts = f.replace("\\", "/").split("/")
        if len(parts) < 3 or parts[0] != "data":
            continue
        lang = parts[1]
        by_lang[lang].append(f)

    for lang in sorted(by_lang):
        by_lang[lang].sort()

    total_downloaded = 0
    for stack_lang in stack_langs:
        files = by_lang.get(stack_lang, [])
        if not files:
            print(f"  [{stack_lang}] no shards found in repo listing — skipping")
            continue

        selected = files[:shards_per_lang]
        print(f"  [{stack_lang}] {len(files)} shard(s) available, downloading {len(selected)}")

        for repo_path in selected:
            local_path = dest / repo_path.replace("\\", "/")
            if local_path.exists():
                print(f"    skip (exists): {local_path.name}")
                continue
            local_path.parent.mkdir(parents=True, exist_ok=True)
            try:
                hf_hub_download(
                    repo_id="bigcode/the-stack-v2",
                    repo_type="dataset",
                    filename=repo_path,
                    local_dir=str(dest),
                )
                size_mb = local_path.stat().st_size / 1_048_576
                print(f"    {local_path.name}  ({size_mb:.0f} MB)")
                total_downloaded += 1
            except Exception as e:
                print(f"    ERROR downloading {repo_path}: {e}", file=sys.stderr)

    print()
    print(f"[stack-v2] done — {total_downloaded} shard(s) downloaded to {dest}")


def main():
    parser = argparse.ArgumentParser(description="Download code datasets for Laplace ingest")
    parser.add_argument("dataset", choices=["tiny-codes", "stack-v2"])
    parser.add_argument("--dest", type=Path, help="Destination directory")
    parser.add_argument("--langs", default=",".join(ALL_GRAMMAR_IDS),
                        help=f"Comma-separated grammar IDs (stack-v2 only). Default: all supported")
    parser.add_argument("--shards", type=int, default=5,
                        help="Parquet shards per language (stack-v2 only, default 5)")
    args = parser.parse_args()

    if args.dataset == "tiny-codes":
        dest = args.dest or DEFAULT_TINY_DEST
        dest.mkdir(parents=True, exist_ok=True)
        download_tiny_codes(dest)
    else:
        dest = args.dest or DEFAULT_STACK_DEST
        dest.mkdir(parents=True, exist_ok=True)
        grammar_ids = [g.strip() for g in args.langs.split(",") if g.strip()]
        download_stack_v2(dest, grammar_ids, args.shards)


if __name__ == "__main__":
    main()
