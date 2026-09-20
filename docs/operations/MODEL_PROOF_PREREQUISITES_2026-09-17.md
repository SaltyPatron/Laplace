# Model proof prerequisites observed on 2026-09-17

> **Dated-file authority notice.** This file records a scoped observation, repair, audit, acceptance campaign, or plan at the date in its name. It is not current invention authority, a global scheduling order, or proof that the measured implementation/runtime state still exists. Current interpretation begins with `docs/INVENTION.md`, `docs/CAPABILITIES.md`, `docs/INVENTIONS.md`, the binding specs, and `AGENTS.md`; re-verify current code, issues, deployment and live receipts before treating dated present-tense claims as current.


This report records an actual prerequisite inventory. Whole-product model synthesis and behavioral acceptance remain unfinished. These findings are separate from chess activation, complete recorded-game throughput, and chess cache acceptance.

## Actual host observation

The read-only inventory completed in [run 35213009386, job 105174775546](https://github.com/SaltyPatron/Laplace/actions/runs/35213009386/job/105174775546), in the actual runner environment.

- Probe blob: 77c65c35a0bfe63e9caf7567cc40b6cdf87f1c7e.
- Operator blob: 5de657c1970ee972f4d669ad60651da12e0f4bcb.
- Workflow blob: e4ac495a559ae26f22308c282b2208842bb43c9d.
- Artifact: 10493996029.
- Artifact SHA-256: 1cde2e4c930ab35c7d083a76483ee738626b21b5ff785cded1979d0428495fb6.

The probe reads metadata, bounded weight-file samples, representative Parquet footers, and executable help. It performs no ingestion, checkout, installation, database mutation, or file creation. “Structurally complete” describes inspected metadata, shard references, and tensor byte ranges; it does not establish complete numerical model correctness or native content identity.

| Input | Actual result |
|---|---|
| TinyLlama snapshot | /vault/models/models--TinyLlama--TinyLlama-1.1B-Chat-v1.0/snapshots/fe8a4ea1ffedaf415f4da2f062534de366a451e6; 2,200,119,864 weight bytes and 201 tensors. Bounded structural checks passed. |
| Qwen snapshot | /vault/models/models--Qwen--Qwen2.5-Coder-3B-Instruct/snapshots/488639f1ff808d1d3d0ba301aef8c11461451ec5; 6,171,927,000 weight bytes and 434 tensors in two shards. Bounded structural checks passed. |
| Distinct artifacts | Config, tokenizer, and sampled weight identities differ. Native full source IDs were not computed. Ordinary model admission and corroboration verify those IDs. |
| First llama candidate | /data/archive/llama-workspace/llama.cpp/build/bin/llama-completion failed startup: missing libllama.so.0, exit 127. |
| CPU llama candidate | /data/archive/llama-workspace/llama.cpp/build-cpu/bin/llama-completion --help returned 0 in approximately 0.043 seconds. Loading a future synthesized GGUF remains untested. |
| Ingest root | /Data/Ingest and source-relative ../../Data/Ingest were absent. /vault/Data was selected. |
| TinyCodes | Fallback /vault/models/tiny-codes; nine top-level shards. Three representative schemas contain prompt, response, and programming_language. No full admission was performed. |
| Stack v2 | Fallback /vault/models/stack-v2; 28 shards. Three representative schemas contain references and provenance but lack content. |

The sampled Stack shards were:

| Shard | Metadata row count | Content column |
|---|---:|---|
| data/C++/train-00000-of-00007.parquet | 9,048,860 | Absent |
| data/Python/train-00000-of-00009.parquet | 8,960,411 | Absent |
| data/TypeScript/train-00004-of-00005.parquet | 7,456,927 | Absent |

These observations establish that the selected full Stack admission encounters reference-only input that its current reader rejects. They do not claim that every row or footer was inspected.

## Source corrections and acceptance

The proof now runs the bounded probe in a strict preliminary mode before its first database command. It passes the exact selected primary and second checkpoint paths; alternative available snapshots cannot replace either selection. The mode requires both inspected snapshots and a llama executable that passes bounded startup. When the existing code-corpus switch enables corpora, an observed missing or unreadable mandatory schema causes immediate failure before Unicode, TinyCodes, Stack, or model admission. The observed metadata-only Stack selection would therefore be rejected early.

The preliminary check remains limited: three representative schemas do not establish whole-corpus validity, and bounded weight samples do not establish native full model identities. All ordinary full readers and later proof gates still run after successful preliminary checks. If the existing corpus switch disables code corpora, the preliminary mode skips their inspection and requirement as well. Runtime selection has moved from after GGUF synthesis to this early probe; the selected runnable binary is passed to the unchanged behavioral verifier.

The former replay gate searched for “already ingested.” The real retained-completion branch emits “Safetensor snapshot already deposited — source ...”. The correction requires that anchored diagnostic prefix and preserves a failing command's exit status. Three replay controls passed in hosted run 35212649983.

The separately proposed orchestration admits and replays two complete checkpoints, invokes the existing model-corroborate command, then runs the unchanged evidence and export gates. The existing single-model contraction scores only already-existing exact model-kind claims; it does not bootstrap those claims on a fresh database.

The joint owner nominates endpoint pairs from existing non-target graph evidence, verifies distinct model content identities, and uses native corroboration to admit matching non-draw outcomes from both checkpoints. It does not turn relation names into fabricated target claims.

Both snapshot admissions retain their replay checks. The four model-evidence minima remain 1,000 each. Complete configured code-corpus admission, SQL readback, GGUF size and synthesis checks, and the external behavioral threshold remain unchanged. Source and fixture controls do not establish that a full model proof passes.

## Executed source controls

The latest [run 35215794045, job 105183847377](https://github.com/SaltyPatron/Laplace/actions/runs/35215794045/job/105183847377) authenticated all six exact source/test leaves and passed Bash syntax plus all 27 controls, with no skips: three replay, six two-checkpoint orchestration, ten bounded-probe, and eight early-prerequisite controls. The eight added controls execute the real proof shell and real probe against finite model/Parquet fixtures. They verify rejection before the first database command for metadata-only Stack, missing TinyCodes columns, an invalid explicitly selected second checkpoint, and a failed llama startup. They also verify the existing disabled-corpus switch, exact model/runtime selection, required strict arguments, and rejection of a malformed success report. These fixture results do not establish full model or corpus acceptance.

The four suites took 0.034, 0.095, 0.010, and 0.631 seconds. Artifact 10495535899 has SHA-256 2304ec9ad49c3d44015f52b6b519a6ebe85a25b9abf5f1d5695911c5d975ee26. The hosted operator was fe08d473d7dd1b7694bbb91a970f4a8cd2eb16dc with workflow b17b82cb7a4024e4a8a202538e0ad333c8fb1a7f. No product-host work was performed by this qualification.

[Run 35213788581, job 105177323338](https://github.com/SaltyPatron/Laplace/actions/runs/35213788581/job/105177323338) authenticated the exact five source/test leaves and passed Bash syntax plus all 19 controls: three replay controls, six two-checkpoint orchestration controls, and ten prerequisite-probe controls. They ran in 0.033, 0.095, and 0.010 seconds respectively. The tests exercise shell command ordering and failures, actual fixture executable startup, bounded footer parsing, and model metadata/sample validation. They perform no real model ingestion or semantic proof.

Retained artifact 10493847564 has SHA-256 83397f8e1770dc95a0b320682882b7b9a2061bc681f755d281a7a543fab98858. The hosted operator was 22076a81ce6f85dba9daba5ba3c1265ddcf664c9 with workflow 1104fb6c4269536682c1b1d53e8f5768067e9fa2; the source-control job did not run on the product host.

## Stack payload resolution remains blocked

The [official Stack-v2 dataset card](https://huggingface.co/datasets/bigcode/the-stack-v2) documents that the shards distribute identifiers separately from file bodies. Its payload route is s3://softwareheritage/content/{blob_id}: decompress gzip, decode each row's src_encoding, and retain source text with its provenance. Bulk access requires the provider's Software Heritage/INRIA agreement and AWS credentials. The current card lists v2.2.0, including removals through July 29, 2026. Unversioned local downloads do not establish that revision.

The existing scripts/python/download_code_data.py downloads metadata only and contains no payload resolver. Its handling of existing files and individual download failures does not prove a complete usable selection. A bounded repository search found no Software Heritage/S3 acquisition owner in either Laplace repository. No retained host observation establishes an alternate resolved-content archive or authorized S3 access. The mixed /vault/models/code-corpus inventory is not proof that it resolves these Stack references.

Future completion requires:

1. Establish the exact selected dataset revision and shard identities, and whether existing authorized S3 access is available. Report credential presence and access outcomes only.
2. Measure selected row counts, distinct blob references, length_bytes, and destination free space. Metadata storage size cannot substitute for a payload estimate.
3. Materialize a resumable, separate content-bearing Parquet derivative, preserving source columns and original metadata. Record shard identity and resolved, missing, and failed counts. Reuse downloaded bytes by blob identity while preserving every source occurrence.
4. Run the ordinary complete Stack admission on that derivative and then the unchanged whole-product model proof.

No bulk acquisition, credential request, contact with third parties, or substitute dataset was performed for this work.

## Runtime scope

The previous activation driver allowed 14,400 seconds for the model phase while managed services were stopped. The behavioral verifier permits up to 16 independent 180-second completion calls, excluding ingestion, contraction, export, and SQL. These are configured ceilings, not measured performance.

The required external proof uses llama explicitly. Successful executable help establishes startup only; it does not establish GGUF loading, semantic behavior, or whole-product readiness.

Whole-product model acceptance remains blocked by the observed Stack inputs. Chess deployment, recorded-game benchmarks, and cache acceptance retain their own separate measured results.
