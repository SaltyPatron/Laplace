#!/usr/bin/env python3
import json, struct, sys, math, os, subprocess
import numpy as np


def load_embed(model_dir):
    sp = os.path.join(model_dir, "model.safetensors")
    with open(sp, "rb") as f:
        n = struct.unpack("<Q", f.read(8))[0]
        hdr = json.loads(f.read(n)); ds = 8 + n
        t = hdr["model.embed_tokens.weight"]
        o0, o1 = t["data_offsets"]
        f.seek(ds + o0); raw = f.read(o1 - o0)
    assert t["dtype"] == "BF16", t["dtype"]
    E = (np.frombuffer(raw, np.uint16).astype(np.uint32) << 16).view(np.float32)
    return E.reshape(t["shape"]).astype(np.float64)


def signed_strength(mu):
    e = 1.0 / (1.0 + np.power(10.0, -(mu - 1500.0) / 400.0))
    x = np.clip(2.0 * e - 1.0, -1 + 1e-12, 1 - 1e-12)
    return np.arctanh(x)


def main():
    if len(sys.argv) != 6:
        sys.exit('Usage: validate-arena-reconstruction.py <model_dir> <token_surface> "<conninfo>" "<source_cli_hex>" "<token_entity_cli_hex>"')
    model_dir, surface, conn, src_hex, tok_hex = sys.argv[1:6]

    vocab = json.load(open(os.path.join(model_dir, "tokenizer.json")))["model"]["vocab"]
    key = surface if surface in vocab else "▁" + surface
    if key not in vocab:
        sys.exit(f"{surface!r} not in vocab")
    tid = vocab[key]

    E = load_embed(model_dir)
    M = math.sqrt(float(np.mean(E * E)))
    w = E[tid]
    d = w.shape[0]
    print(f"token {key!r} id={tid}  d_model={d}  arena M(EMBEDS)={M:.6e}")

    sql = f"""
    WITH ax AS (
      SELECT i, public.laplace_hash128_blake3(
               convert_to('model/{src_hex}/channel/'||i,'UTF8')) AS id
      FROM generate_series(0,{d-1}) i),
    subj AS (
      SELECT decode(
        (SELECT string_agg(substr('{tok_hex}',17-2*k,2),'' ORDER BY k) FROM generate_series(1,8) k) ||
        (SELECT string_agg(substr('{tok_hex}',33-2*k,2),'' ORDER BY k) FROM generate_series(1,8) k),
        'hex') AS id)
    SELECT a.i, c.rating/1e9
    FROM ax a
    JOIN laplace.consensus c
      ON c.object_id = a.id
     AND c.subject_id = (SELECT id FROM subj)
     AND c.type_id = public.laplace_hash128_blake3(convert_to('substrate/type/EMBEDS/v1','UTF8'))
    ORDER BY a.i;
    """
    out = subprocess.run(["psql", conn, "-tAF,", "-c", sql],
                         capture_output=True, text=True)
    if out.returncode != 0:
        sys.exit("psql failed:\n" + out.stderr)
    mu = np.full(d, np.nan)
    present = np.zeros(d, bool)
    for line in out.stdout.strip().splitlines():
        if not line:
            continue
        i_s, mu_s = line.split(",")
        i = int(i_s); mu[i] = float(mu_s); present[i] = True
    n = int(present.sum())
    print(f"channels with a consensus row: {n}/{d}"
          f"  ({'all present' if n == d else 'missing = exact-draw cells, w≈0'})")
    if n < 16:
        sys.exit("too few channels to measure")

    wp = w[present]
    mhat = signed_strength(mu[present])
    target = wp / M
    what = mhat * M

    phi = 350.0 + (30.0 - 350.0) * (0.27 * 0.50)
    grid_w = np.linspace(-6.0 * M, 6.0 * M, 2001)
    score_fp = np.clip(0.5 * (1.0 + np.tanh(grid_w / M)), 0, 1) * 1_000_000_000
    rows = ",".join(f"({i},{int(s)})" for i, s in enumerate(score_fp))
    fwd_sql = f"""
    WITH g(i,score) AS (VALUES {rows})
    SELECT i, (laplace.laplace_glicko2_accumulate_games(
        1500000000000,350000000000,60000000,
        1500000000000,{int(round(phi*1e9))},1,score,500000000)).rating/1e9
    FROM g ORDER BY i;"""
    fo = subprocess.run(["psql", conn, "-tAF,", "-c", fwd_sql], capture_output=True, text=True)
    if fo.returncode != 0:
        sys.exit("forward-map psql failed:\n" + fo.stderr)
    gmu = np.array([float(l.split(",")[1]) for l in fo.stdout.strip().splitlines()])
    order = np.argsort(gmu)
    what_cal = np.interp(mu[present], gmu[order], grid_w[order])
    sat = np.abs(wp) / M > 6.0
    sat_any = np.abs(wp) / M > np.arctanh(1 - 2e-9) if True else sat
    n_sat = int((0.5*(1+np.tanh(wp/M)) > 1 - 1e-9).sum() + (0.5*(1+np.tanh(wp/M)) < 1e-9).sum())

    def pearson(a, b):
        a, b = a - a.mean(), b - b.mean()
        return float(a @ b / (np.linalg.norm(a) * np.linalg.norm(b) + 1e-30))
    def spearman(a, b):
        ra = np.argsort(np.argsort(a)); rb = np.argsort(np.argsort(b))
        return pearson(ra.astype(float), rb.astype(float))

    rel_l2 = float(np.linalg.norm(what - wp) / (np.linalg.norm(wp) + 1e-30))
    rel_cal = float(np.linalg.norm(what_cal - wp) / (np.linalg.norm(wp) + 1e-30))
    sign_agree = float(np.mean(np.sign(what) == np.sign(wp)))
    print(f"Pearson r (m̂ vs w/M)          = {pearson(mhat, target):+.4f}")
    print(f"Spearman ρ (m̂ vs w/M)         = {spearman(mhat, target):+.4f}")
    print(f"sign agreement                = {sign_agree*100:.1f}%")
    print(f"relative L2, approx inverse    = {rel_l2:.4f}")
    print(f"relative L2, CALIBRATED inverse= {rel_cal:.4f}   <-- exporter should use this")
    print(f"tanh-saturated cells (|w/M|≫1) = {n_sat}/{n}  ({100.0*n_sat/n:.1f}% — magnitude unrecoverable there)")
    print(f"w range [{wp.min():+.4f},{wp.max():+.4f}]  ŵ_cal range [{what_cal.min():+.4f},{what_cal.max():+.4f}]")


if __name__ == "__main__":
    main()
