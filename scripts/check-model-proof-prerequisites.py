#!/usr/bin/env python3
"""Bounded, read-only model-proof input inventory; stdout is the retained JSON."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import struct
import subprocess
import time

MAX_META = 32 * 1024 * 1024


class PreflightDeadline(BaseException):
    pass


def read_bounded(path, limit=MAX_META):
    with open(path, "rb") as f:
        data = f.read(limit + 1)
    if len(data) > limit:
        raise ValueError("metadata exceeds bounded inspection limit")
    return data


def sha(data):
    return hashlib.sha256(data).hexdigest()


class Compact:
    """Read only bounded Parquet footer metadata using Thrift compact types."""
    def __init__(self, data):
        self.data, self.pos, self.items = data, 0, 0

    def take(self, n):
        if n < 0 or self.pos + n > len(self.data):
            raise ValueError("truncated compact metadata")
        out = self.data[self.pos:self.pos+n]
        self.pos += n
        return out

    def byte(self):
        return self.take(1)[0]

    def varint(self):
        value = 0
        for shift in range(0, 70, 7):
            b = self.byte()
            value |= (b & 127) << shift
            if not b & 128:
                return value
        raise ValueError("overlong compact integer")

    def integer(self):
        value = self.varint()
        return (value >> 1) ^ -(value & 1)

    def value(self, kind, depth=0):
        self.items += 1
        if self.items > 200000 or depth > 32:
            raise ValueError("compact metadata inspection bound exceeded")
        if kind in (1, 2):
            return kind == 1
        if kind == 3:
            return self.byte()
        if kind in (4, 5, 6):
            return self.integer()
        if kind == 7:
            return struct.unpack("<d", self.take(8))[0]
        if kind == 8:
            return self.take(self.varint()).decode("utf-8", "replace")
        if kind in (9, 10):
            head = self.byte()
            count = head >> 4
            if count == 15:
                count = self.varint()
            if count > 100000:
                raise ValueError("compact collection inspection bound exceeded")
            return [self.value(head & 15, depth + 1) for _ in range(count)]
        if kind == 11:
            count = self.varint()
            if count > 100000:
                raise ValueError("compact map inspection bound exceeded")
            if not count:
                return []
            kinds = self.byte()
            return [(self.value(kinds >> 4, depth + 1),
                     self.value(kinds & 15, depth + 1)) for _ in range(count)]
        if kind == 12:
            return dict(self.fields(depth + 1))
        raise ValueError("unsupported compact metadata type")

    def fields(self, depth=0):
        field = 0
        while True:
            head = self.byte()
            if not head:
                return
            delta = head >> 4
            field = field + delta if delta else self.integer()
            yield field, self.value(head & 15, depth)


def parquet_schema(path):
    size = path.stat().st_size
    with path.open("rb") as f:
        if size < 12 or f.read(4) != b"PAR1":
            raise ValueError("not a plain Parquet file")
        f.seek(-8, 2)
        trailer = f.read(8)
        count = struct.unpack("<I", trailer[:4])[0]
        if trailer[4:] != b"PAR1" or count > MAX_META or count > size - 12:
            raise ValueError("invalid or oversized Parquet footer")
        f.seek(size - 8 - count)
        parser = Compact(f.read(count))
    schema, rows = None, None
    for field, value in parser.fields():
        if field == 2:
            schema = value
        elif field == 3:
            rows = value
        if schema is not None and rows is not None:
            break
    if not isinstance(schema, list) or not schema:
        raise ValueError("Parquet schema absent")
    fields = []
    index = 1
    for _ in range(schema[0].get(5, 0)):
        element = schema[index]
        fields.append({"name": element.get(4), "physical_type": element.get(1),
                       "children": element.get(5, 0)})
        pending = 1
        while pending:
            item = schema[index]
            index += 1
            pending += item.get(5, 0) - 1
    return {"rows": rows, "fields": fields, "footer_bytes": count}


def model_info(path):
    result = {"path": str(path), "structurally_complete": False}
    try:
        config_bytes = read_bounded(path / "config.json", 2 * 1024 * 1024)
        tokenizer_bytes = read_bounded(path / "tokenizer.json")
        config = json.loads(config_bytes)
        json.loads(tokenizer_bytes)
        weights = sorted(path.glob("*.safetensors"))
        if not weights or len(weights) > 256:
            raise ValueError("no weights or more than 256 shards")
        names = {p.name for p in weights}
        index_path = path / "model.safetensors.index.json"
        if index_path.exists():
            index = json.loads(read_bounded(index_path))
            missing = sorted(set(index.get("weight_map", {}).values()) - names)
            if missing:
                raise ValueError("indexed weight shards missing: " + ", ".join(missing[:8]))
        descriptors = []
        tensor_count = 0
        for weight in weights:
            with weight.open("rb") as f:
                st = os.fstat(f.fileno())
                header_length = struct.unpack("<Q", f.read(8))[0]
                if header_length > MAX_META or header_length > st.st_size - 8:
                    raise ValueError("invalid or oversized safetensors header: " + weight.name)
                raw_header = f.read(header_length)
                header = json.loads(raw_header)
                tensors = {k: v for k, v in header.items() if k != "__metadata__"}
                if not tensors:
                    raise ValueError("empty safetensors shard: " + weight.name)
                payload = st.st_size - 8 - header_length
                for tensor in tensors.values():
                    start, end = tensor["data_offsets"]
                    if not isinstance(start, int) or not isinstance(end, int) or not 0 <= start <= end <= payload:
                        raise ValueError("tensor range outside weight file: " + weight.name)
                f.seek(8 + header_length)
                first = f.read(min(65536, payload))
                f.seek(max(8 + header_length, st.st_size - 65536))
                last = f.read(65536)
                descriptors.append({"name": weight.name, "bytes": st.st_size,
                                    "header_sha256": sha(raw_header),
                                    "sample_sha256": sha(first + last)})
                tensor_count += len(tensors)
        result.update(structurally_complete=True, model_type=config.get("model_type"),
                      config_sha256=sha(config_bytes), tokenizer_sha256=sha(tokenizer_bytes),
                      tensors=tensor_count, weights=descriptors,
                      weight_bytes=sum(w["bytes"] for w in descriptors),
                      full_weight_hashes_checked=False, canonical_source_id_computed=False)
    except Exception as exc:
        result["error"] = type(exc).__name__ + ": " + str(exc)
    return result


def latest_candidates(hub, family):
    root = hub / family / "snapshots"
    if not root.is_dir():
        return []
    paths = [p for p in root.iterdir() if p.is_dir()]
    paths.sort(key=lambda p: p.stat().st_mtime_ns, reverse=True)
    return [p for p in paths if (p / "config.json").is_file()
            and (p / "tokenizer.json").is_file() and any(p.glob("*.safetensors"))][:3]


def model_signature(model):
    if not model.get("structurally_complete"):
        return None
    return (model["config_sha256"], model["tokenizer_sha256"],
            tuple((w["bytes"], w["header_sha256"], w["sample_sha256"])
                  for w in model["weights"]))


def inspect_llama(path):
    item = {"path": str(path), "runnable": False}
    if not path.is_file() or not os.access(path, os.X_OK):
        item["error"] = "missing or not executable"
        return item
    process = None
    try:
        item["resolved_path"] = str(path.resolve())
        item["bytes"] = path.stat().st_size
        process = subprocess.Popen([str(path), "--help"], stdin=subprocess.DEVNULL,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   start_new_session=True)
        begin = time.monotonic()
        try:
            stdout, stderr = process.communicate(timeout=15)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            stdout, stderr = process.communicate()
            item["error"] = "help exceeded 15 seconds"
        item.update(exit_code=process.returncode, elapsed_seconds=time.monotonic()-begin,
                    stdout_bytes=len(stdout), stderr_bytes=len(stderr),
                    stdout_sha256=sha(stdout), stderr_sha256=sha(stderr))
        item["runnable"] = process.returncode == 0 and "error" not in item
        # Keep only loader/setup diagnostics, never dump the inherited environment.
        if not item["runnable"]:
            item["stderr_tail"] = stderr[-2000:].decode("utf-8", "replace")
    except Exception as exc:
        item["error"] = type(exc).__name__ + ": " + str(exc)
    finally:
        if process is not None and process.poll() is None:
            os.killpg(process.pid, signal.SIGKILL)
            process.communicate()
    return item


def corpus_info(root, name):
    primary = root / name
    fallback = root.parent / "models" / name
    selected = fallback if not primary.exists() and fallback.is_dir() else primary
    item = {"primary": str(primary), "fallback": str(fallback), "selected": str(selected),
            "exists": selected.exists(), "representative_schemas": [],
            "full_corpus_scanned": False}
    try:
        paths = []
        truncated = False
        if selected.is_file():
            paths = [selected] if selected.suffix.lower() == ".parquet" else []
        elif selected.is_dir():
            iterator = selected.glob("*.parquet") if name == "tiny-codes" else selected.rglob("*.parquet")
            for path in iterator:
                paths.append(path)
                if len(paths) >= 10001:
                    truncated = True
                    break
        paths.sort(key=str)
        item.update(shards_observed=len(paths), shard_inventory_truncated=truncated,
                    empty_primary_masks_fallback=primary.is_dir() and not paths and fallback.is_dir())
        indexes = sorted({0, len(paths)//2, len(paths)-1}) if paths else []
        for index in indexes:
            path = paths[index]
            observed = {"path": str(path)}
            try:
                observed.update(parquet_schema(path))
                fields = {f["name"].lower(): f for f in observed["fields"] if isinstance(f["name"], str)}
                if name == "tiny-codes":
                    required = ["prompt", "response"]
                    one_of = ("task_id", "programming_language")
                else:
                    required = ["content"]
                    one_of = ("language", "lang")
                observed["required_columns_present"] = (
                    all(key in fields for key in required) and any(key in fields for key in one_of))
                observed["blob_reference_without_content"] = "blob_id" in fields and "content" not in fields
            except Exception as exc:
                observed["error"] = type(exc).__name__ + ": " + str(exc)
            item["representative_schemas"].append(observed)
    except Exception as exc:
        item["error"] = type(exc).__name__ + ": " + str(exc)
    return item


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", type=Path, required=True)
    args = ap.parse_args()
    report = {"schema": "laplace-model-proof-prerequisites-v1", "read_only": True,
              "models": [], "llama": [], "corpora": [], "complete": False}
    def deadline(_signum, _frame):
        raise PreflightDeadline("90-second preflight inspection deadline")
    signal.signal(signal.SIGALRM, deadline)
    signal.alarm(90)
    try:
        repo = args.repo.resolve()
        candidates = []
        primary_explicit = next((os.environ.get(k) for k in (
            "LAPLACE_MODEL_PROOF_DIR", "LAPLACE_QWEN25_CODER_DIR", "LAPLACE_TINYLLAMA_DIR")
            if os.environ.get(k)), None)
        if primary_explicit:
            candidates.append(Path(primary_explicit))
        second_explicit = os.environ.get("LAPLACE_MODEL_CORROBORATION_DIR")
        if second_explicit:
            candidates.append(Path(second_explicit))
        hub = Path(os.environ.get("LAPLACE_MODEL_HUB", "/vault/models"))
        for family in ("models--Qwen--Qwen2.5-Coder-3B-Instruct",
                       "models--TinyLlama--TinyLlama-1.1B-Chat-v1.0"):
            candidates.extend(latest_candidates(hub, family))
        seen = set()
        for path in candidates[:8]:
            key = str(path.resolve())
            if key not in seen:
                seen.add(key)
                report["models"].append(model_info(path))
        eligible = [m for m in report["models"] if m.get("structurally_complete")]
        first = eligible[0] if eligible else None
        second = next((m for m in eligible[1:] if model_signature(m) != model_signature(first)), None)
        report["suggested_pair"] = None if first is None or second is None else {
            "first": first["path"], "second": second["path"],
            "distinct_inspected_artifact_bytes": True,
            "canonical_model_source_ids_verified": False}
        report["selection_note"] = "Suggested metadata/sample-distinct pair; actual native admission verifies full content identities."
        explicit_llama = os.environ.get("LAPLACE_LLAMA_BIN")
        runtime_candidates = [explicit_llama] if explicit_llama else [
            "/data/archive/llama-workspace/llama.cpp/build/bin/llama-completion",
            "/data/archive/llama-workspace/llama.cpp/build-cpu/bin/llama-completion",
            shutil.which("llama-completion")]
        for candidate in runtime_candidates:
            if candidate and candidate not in {r["path"] for r in report["llama"]}:
                item = inspect_llama(Path(candidate))
                report["llama"].append(item)
                if item["runnable"]:
                    report["selected_llama"] = item["path"]
                    break
        root_choices = [Path(repo.anchor) / "Data" / "Ingest",
                        (repo / ".." / ".." / "Data" / "Ingest").resolve(),
                        Path("/vault/Data")]
        root = next((p for p in root_choices if p.is_dir()), None)
        report["ingest_root_candidates"] = [{"path": str(p), "exists": p.is_dir()} for p in root_choices]
        report["selected_ingest_root"] = str(root) if root else None
        if root is not None:
            report["corpora"] = [corpus_info(root, name) for name in ("tiny-codes", "stack-v2")]
        report["complete"] = True
    except (Exception, PreflightDeadline) as exc:
        report["inspection_error"] = type(exc).__name__ + ": " + str(exc)
    finally:
        signal.alarm(0)
    print("MODEL_PROOF_PREFLIGHT " + json.dumps(report, sort_keys=True, allow_nan=False), flush=True)
    return 0 if report["complete"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
