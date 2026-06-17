#!/usr/bin/env python3
"""Foundry cast acceptance: does the poured GGUF carry the substrate's testimony?

"Finite, non-flat logits" is vacuous — broken wiring also emits finite varied
numbers. This probe measures testimony transfer, matched to the WITNESS CLASS:

  1. BASIS: cosine similarity (content dims, bias channel excluded) of
     consensus-related word pairs vs a random-pair baseline. If the Laplacian-
     eigenmaps basis carries the SIMILAR_TO consensus geometry, related pairs
     must separate from the baseline.
  2. WITNESS FIDELITY (when the source snapshot is present): Spearman agreement
     between the source model's own embedding cosines and the cast's content-dim
     cosines over random word pairs. THE test for an embedding-model witness —
     its testimony IS similarity geometry. Expected-continuation ranks are an
     LLM-witness test and only apply to substrates with sequence testimony.
  3. READOUT: full-vocab logit relief from the oracle forward pass, plus
     depth-resolved ranks (k=0..L) — the wiring diagnostic that localizes
     scale/orientation defects to a layer.

Usage: foundry-probe.py <gguf> <tokenizer_dir> [prompt] [expected,tokens]
"""
import importlib.util
import os
import sys

import numpy as np



try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


def load_oracle():
    here = os.path.dirname(os.path.abspath(__file__))
    spec = importlib.util.spec_from_file_location(
        "oracle", os.path.join(here, "model-forward-oracle.py"))
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


RELATED_PAIRS = [
    ("france", "paris"), ("england", "london"), ("germany", "berlin"),
    ("king", "queen"), ("man", "woman"), ("good", "great"),
    ("water", "rain"), ("dog", "cat"), ("two", "three"),
    ("monday", "tuesday"), ("red", "blue"), ("walk", "run"),
]


def main():
    if len(sys.argv) < 3:
        sys.exit("usage: foundry-probe.py <gguf> <tokenizer_dir> [prompt] [expected,tokens]")
    gguf_path, tok_dir = sys.argv[1], sys.argv[2]
    prompt = sys.argv[3] if len(sys.argv) > 3 else "the capital of france"
    expected = (sys.argv[4] if len(sys.argv) > 4 else "paris,france,city,is").split(",")

    oracle = load_oracle()
    kv, T = oracle._read_gguf(gguf_path)
    vocab = oracle.gguf_vocab(tok_dir)
    inv = {i: s for s, i in vocab.items()}

    E = T["token_embd.weight"].astype(np.float64)
    nvocab, d = E.shape
    C = E[:, :d - 1]                       
    norms = np.linalg.norm(C, axis=1)
    norms[norms == 0] = 1.0
    Cn = C / norms[:, None]

    def tok_id(w):
        for key in ("▁" + w, w, w.lower()):
            if key in vocab:
                return vocab[key]
        return None

    print(f"== basis probe: content-dim cosine, {nvocab} x {d} ==")
    rel = []
    for a, b in RELATED_PAIRS:
        ia, ib = tok_id(a), tok_id(b)
        if ia is None or ib is None:
            print(f"  {a:>10s} ~ {b:<10s}  (not single tokens — skipped)")
            continue
        c = float(Cn[ia] @ Cn[ib])
        rel.append(c)
        print(f"  {a:>10s} ~ {b:<10s}  cos={c:+.4f}")

    rng = np.random.default_rng(0)
    ra = rng.integers(0, nvocab, 4000)
    rb = rng.integers(0, nvocab, 4000)
    keep = ra != rb
    rand = np.einsum("ij,ij->i", Cn[ra[keep]], Cn[rb[keep]])
    rel = np.array(rel)
    sep = (rel.mean() - rand.mean()) / rand.std() if len(rel) else float("nan")
    print(f"  related   n={len(rel)}  mean={rel.mean():+.4f}")
    print(f"  random    n={keep.sum()}  mean={rand.mean():+.4f}  std={rand.std():.4f}")
    print(f"  separation = {sep:+.2f} sigma "
          f"({'CARRIES consensus geometry' if sep > 3 else 'NO consensus geometry'})")

    
    
    import glob
    if glob.glob(os.path.join(tok_dir, "*.safetensors")):
        smap = oracle._shard_map(tok_dir)
        src_name = next((n for n in (
            "embeddings.word_embeddings.weight",
            "bert.embeddings.word_embeddings.weight",
            "model.embed_tokens.weight") if n in smap), None)
        if src_name is not None:
            S = oracle._load(smap, src_name)
            sn = np.linalg.norm(S, axis=1)
            sn[sn == 0] = 1.0
            Sn = S / sn[:, None]
            m = min(len(Sn), nvocab)
            print(f"\n== witness fidelity: source {src_name} vs cast ==")
            
            
            
            pa = rng.integers(0, m, 3000)
            pb = rng.integers(0, m, 3000)
            pk = pa != pb
            src_cos = np.einsum("ij,ij->i", Sn[pa[pk]], Sn[pb[pk]])
            cast_cos = np.einsum("ij,ij->i", Cn[pa[pk]], Cn[pb[pk]])
            sr = np.argsort(np.argsort(src_cos)).astype(np.float64)
            cr = np.argsort(np.argsort(cast_cos)).astype(np.float64)
            rho = float(np.corrcoef(sr, cr)[0, 1])
            print(f"  all-pairs spearman (context, noise included) = {rho:+.4f}")
            
            
            
            probes = rng.choice(m, 300, replace=False)
            sims_src = Sn[probes] @ Sn.T
            sims_cast = Cn[probes] @ Cn[:m].T
            hits, total = 0, 0
            for r in range(len(probes)):
                s = sims_src[r].copy(); s[probes[r]] = -np.inf
                c = sims_cast[r].copy(); c[probes[r]] = -np.inf
                top_src = np.argpartition(s, -10)[-10:]
                top_cast = set(np.argpartition(c, -100)[-100:].tolist())
                hits += sum(1 for t in top_src if int(t) in top_cast)
                total += 10
            rec = hits / total
            print(f"  neighbor recall: source top-10 in cast top-100 = {rec:.1%} "
                  f"(chance {100 / m:.2%}) "
                  f"({'cast RENDERS the witness geometry' if rec > 0.2 else 'weak/no rendering'})")

    print(f'\n== readout probe: full-vocab logits for "{prompt}" ==')
    ids = oracle.tokenize_words(vocab, prompt)
    logits = oracle.gguf_forward(kv, T, ids)
    order = np.argsort(logits)[::-1]
    rank = {int(i): r for r, i in enumerate(order, 1)}
    print(f"  spread: min={logits.min():+.3f} max={logits.max():+.3f} "
          f"std={logits.std():.4f} top1-median={logits.max() - np.median(logits):+.3f}")
    for w in expected:
        i = tok_id(w.strip())
        if i is None:
            print(f"  expected {w!r}: not a single token")
        else:
            print(f"  expected {w!r}: rank {rank[i]}/{nvocab} logit={logits[i]:+.3f}")
    in_prompt = [inv.get(i, "?") for i in ids[1:]]
    print(f"  prompt-token ranks: " + ", ".join(
        f"{w}={rank[vocab[w]]}" for w in in_prompt if w in vocab))
    
    topk = [int(i) for i in order[:15]]
    print("  top-15 next: " + ", ".join(
        f"{inv.get(i, '?')}({logits[i]:+.2f})" for i in topk))

    print("\n== depth probe: expected-token rank after k layers ==")
    probe_ids = [tok_id(w.strip()) for w in expected]
    nlayers = kv["llama.block_count"]
    for k in range(nlayers + 1):
        lg = oracle.gguf_forward(kv, T, ids, layers=k)
        od = np.argsort(lg)[::-1]
        rk = {int(i): r for r, i in enumerate(od, 1)}
        marks = ", ".join(f"{w.strip()}={rk[i]}" for w, i in zip(expected, probe_ids) if i is not None)
        print(f"  k={k}: std={lg.std():.3f}  {marks}")


if __name__ == "__main__":
    main()
