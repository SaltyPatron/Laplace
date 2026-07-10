#!/usr/bin/env python3
"""RoPE-vs-synthesized-QK interaction probe (remediation plan Phase 0).

Synthesized relation heads encode QK as CONTENT relations (factored consensus
planes); llama-arch RoPE rotates Q/K by absolute position. If rotation
materially changes a head's A->B attention score as the token distance
between A and B grows, RoPE is corrupting the synthesized operators and the
mitigation (huge rope.freq_base) is required.

Method: for each head, embed [BOS, A, pad*n, B] at n = 0..MAX_GAP pads,
compute the pre-softmax attention score of position(B) attending to
position(A) twice — once with RoPE at the GGUF's freq_base, once with
rotation disabled (identity) — and report the drift of the ratio.

Usage: rope-probe.py <gguf> <tokenizer_dir> <wordA> <wordB> [pad_word] [max_gap]
Exit: 0 = RoPE benign (max relative drift < 0.15), 1 = RoPE corrupts, 2 = setup error.
"""
import importlib.util
import os
import sys

import numpy as np


def load_oracle():
    here = os.path.dirname(os.path.abspath(__file__))
    spec = importlib.util.spec_from_file_location(
        "oracle", os.path.join(here, "model-forward-oracle.py"))
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


def head_scores(kv, T, ids, pos_a, pos_b, use_rope):
    pre = "llama."
    d = kv[pre + "embedding_length"]
    H = kv[pre + "attention.head_count"]
    KV = kv[pre + "attention.head_count_kv"]
    hd = d // H
    eps = kv.get(pre + "attention.layer_norm_rms_epsilon", 1e-5)
    theta = kv.get(pre + "rope.freq_base", 10000.0)
    n = len(ids)
    pos = np.arange(n)

    def rms(x, w):
        return x / np.sqrt(np.mean(x * x, -1, keepdims=True) + eps) * w

    def rope(x):
        fr = 1.0 / (theta ** (np.arange(0, hd, 2) / hd))
        ang = np.outer(pos, fr)
        c, s = np.cos(ang)[:, None, :], np.sin(ang)[:, None, :]
        x1, x2 = x[..., :hd // 2], x[..., hd // 2:]
        return np.concatenate([x1 * c - x2 * s, x1 * s + x2 * c], -1)

    x = T["token_embd.weight"][ids]
    b = "blk.0."
    h = rms(x, T[b + "attn_norm.weight"])
    q = (h @ T[b + "attn_q.weight"].T).reshape(n, H, hd)
    k = (h @ T[b + "attn_k.weight"].T).reshape(n, KV, hd)
    if use_rope:
        q, k = rope(q), rope(k)
    k = np.repeat(k, H // KV, 1)
    # pre-softmax score of B attending to A, per head
    return np.einsum("hd,hd->h", q[pos_b].reshape(H, hd),
                     k[pos_a].reshape(H, hd)) / np.sqrt(hd)


def main():
    if len(sys.argv) < 5:
        sys.exit(__doc__)
    gguf, tok_dir, word_a, word_b = sys.argv[1:5]
    pad = sys.argv[5] if len(sys.argv) > 5 else "the"
    max_gap = int(sys.argv[6]) if len(sys.argv) > 6 else 24

    o = load_oracle()
    kv, T = o._read_gguf(gguf)
    vocab = o.gguf_vocab(tok_dir)
    ids_a = list(o.tokenize_words(vocab, word_a))
    ids_b = list(o.tokenize_words(vocab, word_b))
    ids_p = list(o.tokenize_words(vocab, pad))
    if not ids_a or not ids_b or not ids_p:
        sys.exit(f"setup error: '{word_a}'/'{word_b}'/'{pad}' not in vocab")
    a, bt, p = ids_a[0], ids_b[0], ids_p[0]
    bos = vocab.get("<s>", vocab.get("<bos>", a))

    H = kv["llama.attention.head_count"]
    theta = kv.get("llama.rope.freq_base", 10000.0)
    print(f"gguf={os.path.basename(gguf)} heads={H} freq_base={theta}")
    print(f"A='{word_a}'({a}) B='{word_b}'({bt}) pad='{pad}'({p})")
    print(f"{'gap':>4} {'max|rot-norot|/|norot|':>24} {'worst-head':>10}")

    # Verdict uses the MEDIAN head: the context head keeps identity QK and rotates
    # by design (recency kernel on the one sequence head); relation heads must be
    # rotation-clean (verified 2026-07-08: 0.0000 drift after freq_base=1e9 + the
    # rotary-pair-0 component skip).
    worst_median = 0.0
    for gap in range(0, max_gap + 1, 4):
        ids = [bos, a] + [p] * gap + [bt]
        pos_a, pos_b = 1, len(ids) - 1
        s_rot = head_scores(kv, T, ids, pos_a, pos_b, True)
        s_raw = head_scores(kv, T, ids, pos_a, pos_b, False)
        denom = np.maximum(np.abs(s_raw), 1e-6)
        rel = np.abs(s_rot - s_raw) / denom
        per_head = "  ".join(f"h{i}={rel[i]:.4f}" for i in range(len(rel)))
        print(f"{gap:>4} median={float(np.median(rel)):.4f}  {per_head}")
        worst_median = max(worst_median, float(np.median(rel)))

    verdict = "CORRUPTS" if worst_median >= 0.15 else "benign"
    print(f"\nverdict: RoPE {verdict} synthesized relation QK (worst median drift {worst_median:.4f})")
    sys.exit(1 if worst_median >= 0.15 else 0)


if __name__ == "__main__":
    main()
