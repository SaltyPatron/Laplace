#!/usr/bin/env python3
"""Greedy decode from a substrate-synthesized GGUF — the actual continuation test.
Usage: decode-probe.py <gguf> <tokenizer_dir> <prompt> [steps]"""
import importlib.util, os, sys
import numpy as np

def load_oracle():
    here = os.path.dirname(os.path.abspath(__file__))
    spec = importlib.util.spec_from_file_location("oracle", os.path.join(here, "model-forward-oracle.py"))
    m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m); return m

def main():
    gguf, tok_dir, prompt = sys.argv[1], sys.argv[2], sys.argv[3]
    steps = int(sys.argv[4]) if len(sys.argv) > 4 else 12
    o = load_oracle()
    kv, T = o._read_gguf(gguf)
    vocab = o.gguf_vocab(tok_dir)
    inv = {i: s for s, i in vocab.items()}
    mode = sys.argv[5] if len(sys.argv) > 5 else "greedy"
    temp = 0.8; topk = 20
    rng = np.random.default_rng(1234)
    ids = list(o.tokenize_words(vocab, prompt))
    print(f"prompt: {prompt!r}  ids={ids}  mode={mode}")
    out = []
    recent = {}
    for _ in range(steps):
        logits = o.gguf_forward(kv, T, ids).astype(np.float64)
        for tid, c in recent.items():
            logits[tid] -= 3.0 * c
        if mode == "sample":
            top = np.argpartition(logits, -topk)[-topk:]
            p = np.exp((logits[top] - logits[top].max()) / temp); p /= p.sum()
            nxt = int(top[rng.choice(len(top), p=p)])
        else:
            nxt = int(np.argmax(logits))
        ids.append(nxt); out.append(inv.get(nxt, f"<{nxt}>"))
        recent[nxt] = recent.get(nxt, 0) + 1
    text = "".join(s.replace("▁", " ") for s in out)
    print("greedy continuation:", repr(text))
    print("tokens:", " ".join(out))

if __name__ == "__main__":
    main()
