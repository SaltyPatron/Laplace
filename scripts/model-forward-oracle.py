#!/usr/bin/env python3
"""model-forward-oracle.py — the EXPORT VALIDATION GATE.

The re-export (substrate consensus → GGUF) is only correct if the resulting
model produces the source model's behaviour. This is the independent reference
it must reproduce: an exact f64 forward pass of a HuggingFace llama-family
safetensors model, computed straight from the raw weights with numpy — no
Laplace code in the path. It is the ground truth, deliberately outside the
substrate, so a passing export cannot be an artifact of shared code.

Three modes:
  forward      <model_dir> "<prompt>"    next-token distribution (the real
                                         knowledge: lives in the 22 layers)
  embed        <model_dir> <surface>     the raw E·E_Uᵀ row for one token
                                         (the embed→unembed map alone — mostly
                                         tokenizer geometry, NOT semantics)
  forward-gguf <gguf> "<prompt>" <tok_dir>
                                         SAME exact forward pass, but reading
                                         tensors from a GGUF (the re-export
                                         deliverable) instead of safetensors —
                                         the read-back gate: does the synthesized
                                         model behave? <tok_dir> supplies the
                                         tokenizer (config/dims come from GGUF KV).

Measured reference (TinyLlama-1.1B-Chat-v1.0, 2026-06-04):
  forward "The capital of France is"  → rank 1: ▁Paris  +13.39
  embed   ▁Paris                      → ▁France rank 10814, ▁London rank 28606
                                         (semantics are NOT in the embedding)

Requires only numpy. BF16/F16/F32 safetensors; fail-loud on anything else.
"""
import json, struct, sys, os
import numpy as np


def _load_header(path):
    with open(path, "rb") as f:
        n = struct.unpack("<Q", f.read(8))[0]
        hdr = json.loads(f.read(n))
    return hdr, 8 + n


def _shard_map(model_dir):
    """name -> (shard_path, header, data_start). Single-file or sharded."""
    import os, glob
    shards = sorted(glob.glob(os.path.join(model_dir, "*.safetensors")))
    if not shards:
        sys.exit(f"no *.safetensors in {model_dir}")
    m = {}
    for sp in shards:
        hdr, ds = _load_header(sp)
        for name, t in hdr.items():
            if name == "__metadata__":
                continue
            m[name] = (sp, t, ds)
    return m


def _load(smap, name):
    sp, t, ds = smap[name]
    off0, off1 = t["data_offsets"]
    with open(sp, "rb") as f:
        f.seek(ds + off0)
        raw = f.read(off1 - off0)
    dt = t["dtype"]
    if dt == "BF16":
        a = (np.frombuffer(raw, np.uint16).astype(np.uint32) << 16).view(np.float32)
    elif dt == "F16":
        a = np.frombuffer(raw, np.float16).astype(np.float32)
    elif dt == "F32":
        a = np.frombuffer(raw, np.float32)
    else:
        sys.exit(f"tensor {name}: dtype {dt} not handled (BF16/F16/F32 only)")
    return a.reshape(t["shape"]).astype(np.float64)


def _vocab(model_dir):
    import os
    tok = json.load(open(os.path.join(model_dir, "tokenizer.json")))
    v = tok["model"]["vocab"]
    return v, {i: s for s, i in v.items()}


def cmd_embed(model_dir, surface):
    smap = _shard_map(model_dir)
    vocab, inv = _vocab(model_dir)
    if surface not in vocab:
        # accept a leading-space SentencePiece form too
        alt = "▁" + surface
        if alt in vocab:
            surface = alt
        else:
            sys.exit(f"token {surface!r} not in vocab (try the ▁-prefixed form)")
    tid = vocab[surface]
    E = _load(smap, "model.embed_tokens.weight")
    U = _load(smap, "lm_head.weight") if "lm_head.weight" in smap else E
    logits = U @ E[tid]
    order = np.argsort(logits)[::-1]
    print(f"raw E·E_Uᵀ row for {surface!r} (token {tid}) — top 20:")
    for r, i in enumerate(order[:20], 1):
        print(f"{r:3d} {inv.get(int(i), '?'):>16s} {logits[i]:+.4f}")
    self_rank = int(np.where(order == tid)[0][0]) + 1
    print(f"self-coupling: rank {self_rank}/{len(logits)}  {logits[tid]:+.4f}")


def cmd_forward(model_dir, prompt):
    import os
    smap = _shard_map(model_dir)
    cfg = json.load(open(os.path.join(model_dir, "config.json")))
    vocab, inv = _vocab(model_dir)
    L = cfg["num_hidden_layers"]; d = cfg["hidden_size"]
    H = cfg["num_attention_heads"]; KV = cfg["num_key_value_heads"]
    hd = d // H; eps = cfg.get("rms_norm_eps", 1e-5); theta = cfg.get("rope_theta", 10000.0)

    # Tokenize: BOS + the prompt's ▁-prefixed words. Good enough for the probe
    # (this oracle pins next-token behaviour, not the tokenizer — that is the
    # source tokenizer's job at real inference).
    ids = [cfg.get("bos_token_id", 1)]
    for w in prompt.strip().split(" "):
        key = "▁" + w
        if key not in vocab:
            sys.exit(f"prompt word {w!r} ({key!r}) not a single vocab token")
        ids.append(vocab[key])
    T = len(ids); pos = np.arange(T)

    def rms(x, w):
        return x / np.sqrt(np.mean(x * x, -1, keepdims=True) + eps) * w

    def rope(x):  # x [T, h, hd]
        freqs = 1.0 / (theta ** (np.arange(0, hd, 2) / hd))
        ang = np.outer(pos, freqs)
        cos, sin = np.cos(ang)[:, None, :], np.sin(ang)[:, None, :]
        x1, x2 = x[..., : hd // 2], x[..., hd // 2:]
        return np.concatenate([x1 * cos - x2 * sin, x1 * sin + x2 * cos], -1)

    x = _load(smap, "model.embed_tokens.weight")[ids]
    for l in range(L):
        p = f"model.layers.{l}."
        h = rms(x, _load(smap, p + "input_layernorm.weight"))
        q = (h @ _load(smap, p + "self_attn.q_proj.weight").T).reshape(T, H, hd)
        k = (h @ _load(smap, p + "self_attn.k_proj.weight").T).reshape(T, KV, hd)
        v = (h @ _load(smap, p + "self_attn.v_proj.weight").T).reshape(T, KV, hd)
        q, k = rope(q), rope(k)
        k = np.repeat(k, H // KV, 1); v = np.repeat(v, H // KV, 1)
        att = np.einsum("thd,shd->hts", q, k) / np.sqrt(hd)
        att = att + np.triu(np.full((T, T), -1e30), 1)[None]
        att = np.exp(att - att.max(-1, keepdims=True)); att /= att.sum(-1, keepdims=True)
        o = np.einsum("hts,shd->thd", att, v).reshape(T, d)
        x = x + o @ _load(smap, p + "self_attn.o_proj.weight").T
        h = rms(x, _load(smap, p + "post_attention_layernorm.weight"))
        g = h @ _load(smap, p + "mlp.gate_proj.weight").T
        u = h @ _load(smap, p + "mlp.up_proj.weight").T
        x = x + (g / (1 + np.exp(-g)) * u) @ _load(smap, p + "mlp.down_proj.weight").T

    lm = _load(smap, "lm_head.weight") if "lm_head.weight" in smap \
        else _load(smap, "model.embed_tokens.weight")
    logits = rms(x[-1], _load(smap, "model.norm.weight")) @ lm.T
    order = np.argsort(logits)[::-1]
    print(f'next-token for "{prompt}" (exact f64 forward pass) — top 15:')
    for r, i in enumerate(order[:15], 1):
        print(f"{r:3d} {inv.get(int(i), '?'):>14s} {logits[i]:+.3f}")


def _read_gguf(path):
    """Minimal GGUF reader → (kv dict, {ggml_name: np.float64 array})."""
    f = open(path, "rb")
    assert f.read(4) == b"GGUF", "not a GGUF"
    ver, n_tensors, n_kv = struct.unpack("<IQQ", f.read(20))

    def rstr():
        n, = struct.unpack("<Q", f.read(8)); return f.read(n).decode("utf-8", "replace")

    def rval(t):
        if t == 8:  # string
            return rstr()
        if t == 9:  # array
            et, n = struct.unpack("<IQ", f.read(12)); return [rval(et) for _ in range(n)]
        fmt = {0:"B",1:"b",2:"H",3:"h",4:"I",5:"i",6:"f",7:"?",10:"Q",11:"q",12:"d"}[t]
        sz  = {0:1,1:1,2:2,3:2,4:4,5:4,6:4,7:1,10:8,11:8,12:8}[t]
        return struct.unpack("<"+fmt, f.read(sz))[0]

    kv = {}
    for _ in range(n_kv):
        k = rstr(); t, = struct.unpack("<I", f.read(4)); kv[k] = rval(t)
    tens = {}
    for _ in range(n_tensors):
        name = rstr(); nd, = struct.unpack("<I", f.read(4))
        dims = struct.unpack(f"<{nd}Q", f.read(8*nd))
        dt, off = struct.unpack("<IQ", f.read(12))
        tens[name] = (dims, dt, off)
    align = kv.get("general.alignment", 32)
    data0 = (f.tell() + align - 1)//align*align
    GG = {0:"f32", 1:"f16", 30:"bf16"}
    out = {}
    for name, (dims, dt, off) in tens.items():
        if dt not in GG:
            sys.exit(f"GGUF tensor {name}: ggml dtype {dt} not handled (f32/f16/bf16)")
        nelem = 1
        for x in dims: nelem *= x
        f.seek(data0 + off)
        raw = f.read(nelem * (4 if dt == 0 else 2))
        if dt == 0:
            a = np.frombuffer(raw, np.float32)
        elif dt == 1:
            a = np.frombuffer(raw, np.float16).astype(np.float32)
        else:
            a = (np.frombuffer(raw, np.uint16).astype(np.uint32) << 16).view(np.float32)
        # GGUF dims are reversed vs row-major [rows, cols]
        out[name] = a.astype(np.float64).reshape(tuple(reversed(dims)))
    return kv, out


def cmd_forward_gguf(gguf, prompt, tok_dir):
    kv, T = _read_gguf(gguf)
    pre = "llama."
    L = kv[pre+"block_count"]; d = kv[pre+"embedding_length"]
    H = kv[pre+"attention.head_count"]; KV = kv[pre+"attention.head_count_kv"]
    hd = d // H
    eps = kv.get(pre+"attention.layer_norm_rms_epsilon", 1e-5)
    theta = kv.get(pre+"rope.freq_base", 10000.0)
    vocab = json.load(open(os.path.join(tok_dir, "tokenizer.json")))["model"]["vocab"]
    inv = {i: s for s, i in vocab.items()}

    ids = [1]
    for w in prompt.strip().split(" "):
        key = "▁" + w
        if key not in vocab:
            sys.exit(f"prompt word {w!r} not a single vocab token")
        ids.append(vocab[key])
    n = len(ids); pos = np.arange(n)

    def rms(x, w): return x/np.sqrt(np.mean(x*x,-1,keepdims=True)+eps)*w
    def rope(x):
        fr = 1.0/(theta**(np.arange(0,hd,2)/hd)); ang = np.outer(pos,fr)
        c,s = np.cos(ang)[:,None,:],np.sin(ang)[:,None,:]
        x1,x2 = x[...,:hd//2],x[...,hd//2:]
        return np.concatenate([x1*c-x2*s, x1*s+x2*c],-1)

    x = T["token_embd.weight"][ids]
    for l in range(L):
        b = f"blk.{l}."
        h = rms(x, T[b+"attn_norm.weight"])
        q = (h @ T[b+"attn_q.weight"].T).reshape(n,H,hd)
        k = (h @ T[b+"attn_k.weight"].T).reshape(n,KV,hd)
        v = (h @ T[b+"attn_v.weight"].T).reshape(n,KV,hd)
        q,k = rope(q),rope(k)
        k = np.repeat(k,H//KV,1); v = np.repeat(v,H//KV,1)
        a = np.einsum("thd,shd->hts",q,k)/np.sqrt(hd)
        a = a + np.triu(np.full((n,n),-1e30),1)[None]
        a = np.exp(a-a.max(-1,keepdims=True)); a/=a.sum(-1,keepdims=True)
        o = np.einsum("hts,shd->thd",a,v).reshape(n,d)
        x = x + o @ T[b+"attn_output.weight"].T
        h = rms(x, T[b+"ffn_norm.weight"])
        g = h @ T[b+"ffn_gate.weight"].T; u = h @ T[b+"ffn_up.weight"].T
        x = x + (g/(1+np.exp(-g))*u) @ T[b+"ffn_down.weight"].T
    lm = T.get("output.weight", T["token_embd.weight"])
    logits = rms(x[-1], T["output_norm.weight"]) @ lm.T
    order = np.argsort(logits)[::-1]
    print(f'GGUF next-token for "{prompt}" — top 15:')
    for r,i in enumerate(order[:15],1):
        print(f"{r:3d} {inv.get(int(i),'?'):>14s} {logits[i]:+.3f}")


if __name__ == "__main__":
    if len(sys.argv) < 4:
        sys.exit(__doc__)
    mode = sys.argv[1]
    if mode == "forward":
        cmd_forward(sys.argv[2], sys.argv[3])
    elif mode == "embed":
        cmd_embed(sys.argv[2], sys.argv[3])
    elif mode == "forward-gguf":
        if len(sys.argv) != 5:
            sys.exit("forward-gguf <gguf> \"<prompt>\" <tokenizer_dir>")
        cmd_forward_gguf(sys.argv[2], sys.argv[3], sys.argv[4])
    else:
        sys.exit(f"unknown mode {mode!r} (forward | embed | forward-gguf)")
