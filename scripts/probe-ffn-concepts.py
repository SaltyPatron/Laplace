#!/usr/bin/env python3
import json, struct, sys, os, glob
import numpy as np


def smap(model_dir):
    m = {}
    for sp in sorted(glob.glob(os.path.join(model_dir, "*.safetensors"))):
        with open(sp, "rb") as f:
            n = struct.unpack("<Q", f.read(8))[0]
            hdr = json.loads(f.read(n)); ds = 8 + n
        for k, t in hdr.items():
            if k != "__metadata__":
                m[k] = (sp, t, ds)
    return m


def load(m, name):
    sp, t, ds = m[name]; o0, o1 = t["data_offsets"]
    with open(sp, "rb") as f:
        f.seek(ds + o0); raw = f.read(o1 - o0)
    dt = t["dtype"]
    if dt == "BF16":
        a = (np.frombuffer(raw, np.uint16).astype(np.uint32) << 16).view(np.float32)
    elif dt == "F16":
        a = np.frombuffer(raw, np.float16).astype(np.float32)
    else:
        a = np.frombuffer(raw, np.float32)
    return a.reshape(t["shape"]).astype(np.float32)


def main():
    if len(sys.argv) < 2:
        sys.exit("Usage: probe-ffn-concepts.py <model_dir> [layer] [n_neurons]")
    md = sys.argv[1]
    layer = int(sys.argv[2]) if len(sys.argv) > 2 else None
    n_neur = int(sys.argv[3]) if len(sys.argv) > 3 else 12
    m = smap(md)
    cfg = json.load(open(os.path.join(md, "config.json")))
    L = cfg["num_hidden_layers"]
    vocab = json.load(open(os.path.join(md, "tokenizer.json")))["model"]["vocab"]
    inv = {i: s for s, i in vocab.items()}

    E_U = load(m, "lm_head.weight") if "lm_head.weight" in m else load(m, "model.embed_tokens.weight")
    norm = load(m, "model.norm.weight").astype(np.float32)
    layers = [layer] if layer is not None else [L // 4, L // 2, (3 * L) // 4]

    for l in layers:
        Wd = load(m, f"model.layers.{l}.mlp.down_proj.weight")
        d, interm = Wd.shape
        col_norm = np.linalg.norm(Wd, axis=0)
        pick = np.argsort(col_norm)[::-1][:n_neur]
        print(f"\n=== layer {l}: top tokens of {n_neur} highest-norm FFN value neurons ===")
        for j in pick:
            v = Wd[:, j] * norm
            logits = E_U @ v
            top = np.argsort(logits)[::-1][:8]
            toks = " ".join(inv.get(int(i), "?").replace("▁", "_") for i in top)
            print(f"  neuron {int(j):5d} |{col_norm[j]:6.2f}|  {toks}")


if __name__ == "__main__":
    main()
