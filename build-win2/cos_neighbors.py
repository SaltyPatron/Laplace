import sys, importlib.util
import numpy as np
spec = importlib.util.spec_from_file_location("o", r"D:\Repositories\Laplace\scripts\model-forward-oracle.py")
o = importlib.util.module_from_spec(spec); spec.loader.exec_module(o)

md = sys.argv[1]
smap = o._shard_map(md); vocab, inv = o._vocab(md)
E = o._load(smap, "model.embed_tokens.weight")
En = E / (np.linalg.norm(E, axis=1, keepdims=True) + 1e-9)

for surf in sys.argv[2:]:
    key = surf if surf in vocab else ("▁" + surf)
    if key not in vocab:
        print(f"[{surf}] not a single token"); continue
    tid = vocab[key]
    cos = En @ En[tid]
    order = np.argsort(cos)[::-1]
    print(f"=== tug '{surf}'  -> strands that tug back (cosine, order by intensity desc) ===")
    for r, i in enumerate([j for j in order if j != tid][:12], 1):
        print(f"{r:3d} {inv.get(int(i), '?'):>16s}  cos={cos[i]:+.3f}")
    print()
