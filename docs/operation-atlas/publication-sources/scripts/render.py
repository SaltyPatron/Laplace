"""Render Patreon visuals from the H1 codepoint placement (Unicode 17, Super-Fibonacci in Hilbert order)."""
import re, sys, numpy as np, matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
SP, OUT = sys.argv[1], "/vault/Data/LaplacePatreon/images"
P = np.load(f"{SP}/P_h1.npy"); assigned = np.load(f"{SP}/assigned.npy")
NAVY, COPPER, TEAL, INK, CREAM = "#131d2b", "#c07a45", "#39bbae", "#9fb3c8", "#f3ece4"
plt.rcParams.update({"font.family": "DejaVu Sans", "text.color": CREAM, "axes.labelcolor": INK,
                     "xtick.color": INK, "ytick.color": INK, "axes.edgecolor": "#33445a"})
# scripts
sc = {}
for line in open("/vault/Data/UCD/Public/UCD/latest/ucd/Scripts.txt", encoding="utf-8"):
    m = re.match(r"([0-9A-F]+)(?:\.\.([0-9A-F]+))?\s*;\s*(\w+)", line)
    if m:
        lo = int(m[1], 16); hi = int(m[2] or m[1], 16)
        for cp in range(lo, hi + 1): sc[cp] = m[3]
fam = {"Latin": "#e8b04b", "Greek": "#f08a5d", "Cyrillic": "#b83b5e", "Arabic": "#6a9f58", "Hebrew": "#5ec4b6",
       "Devanagari": "#8e7cc3", "Han": "#4e7bad", "Hangul": "#7fb3d5", "Hiragana": "#d4a5c9", "Katakana": "#d4a5c9"}
def hopf(q):
    z1 = q[:, 0] + 1j * q[:, 1]; z2 = q[:, 2] + 1j * q[:, 3]; w = z1 * np.conj(z2)
    return np.stack([2 * w.real, 2 * w.imag, np.abs(z1) ** 2 - np.abs(z2) ** 2], 1)

# 1. The glome of Unicode: every assigned codepoint, Hopf-projected to a sphere, coloured by script
H = hopf(P[assigned]); cols = [fam.get(sc.get(int(c), ""), "#3d4f66") for c in assigned]
fig = plt.figure(figsize=(12, 12), facecolor=NAVY); ax = fig.add_subplot(projection="3d", facecolor=NAVY)
order_draw = np.argsort([c != "#3d4f66" for c in cols])
ax.scatter(H[order_draw, 0], H[order_draw, 1], H[order_draw, 2], c=np.array(cols)[order_draw], s=0.6, alpha=0.8, linewidths=0)
ax.set_axis_off(); ax.view_init(elev=18, azim=35); ax.set_box_aspect((1, 1, 1))
for i, (k, v) in enumerate(list(fam.items())[:9]):
    fig.text(0.07, 0.86 - i * 0.028, "●  " + k, color=v, fontsize=13)
fig.text(0.07, 0.93, "The glome of Unicode", fontsize=26, color=CREAM, weight="bold")
fig.text(0.07, 0.905, "All 159,866 assigned codepoints of Unicode 17 on S³, seen through the Hopf map", fontsize=13, color=INK)
fig.text(0.07, 0.05, "Super-Fibonacci points in 4D Hilbert order · DUCET rank r takes the r-th point", fontsize=11, color=COPPER)
fig.savefig(f"{OUT}/01-glome-of-unicode.png", dpi=150, facecolor=NAVY); plt.close(fig)

# 2. King by king by ding by dong by kong: local 2D map of word centroids
cen = lambda w: np.mean([P[ord(c)] for c in w], 0)
words = ["King", "king", "KING", "ring", "sing", "ding", "dong", "kong", "Kong", "tree", "zebu", "wing", "kind"]
X = np.array([cen(w) for w in words]); X0 = X - X.mean(0)
U = np.linalg.svd(X0, full_matrices=False)[2][:2]; Y = X0 @ U.T
fig, ax = plt.subplots(figsize=(12, 8), facecolor=NAVY); ax.set_facecolor(NAVY)
for w, (x, y) in zip(words, Y):
    hot = w in ("King", "king", "ding", "dong", "kong")
    ax.scatter(x, y, s=140 if hot else 70, color=COPPER if hot else TEAL, zorder=3, edgecolors=CREAM, linewidths=0.6)
    ax.annotate(w, (x, y), xytext=(8, 6), textcoords="offset points", fontsize=15 if hot else 12, color=CREAM if hot else INK)
chain = [words.index(w) for w in ["King", "king", "ding", "dong", "kong"]]
ax.plot(Y[chain, 0], Y[chain, 1], color=COPPER, lw=1.5, alpha=0.7, zorder=2)
for s in ax.spines.values(): s.set_visible(False)
ax.set_xticks([]); ax.set_yticks([])
fig.text(0.06, 0.92, "King by king by ding by dong by kong", fontsize=24, color=CREAM, weight="bold")
fig.text(0.06, 0.885, "Word positions (4D centroids of their codepoints), projected to the plane that shows them best", fontsize=12, color=INK)
fig.text(0.06, 0.04, "king→King 0.023 · ring/sing 0.049 · dong 0.070 · kong 0.079 · ding 0.105 · tree 0.162 · random pairs ≈ 0.163", fontsize=11, color=COPPER)
fig.savefig(f"{OUT}/02-king-by-king.png", dpi=150, facecolor=NAVY); plt.close(fig)

# 3. Falling toward the centre: norm of the centroid of k codepoints
rng = np.random.default_rng(7); ks = np.array([1, 2, 3, 4, 5, 8, 10, 16, 25, 50, 100, 200])
fig, ax = plt.subplots(figsize=(12, 7), facecolor=NAVY); ax.set_facecolor(NAVY)
for k in ks:
    norms = [np.linalg.norm(P[rng.choice(assigned, k)].mean(0)) for _ in range(400)]
    ax.scatter(np.full(len(norms), k) * np.exp(rng.normal(0, 0.03, len(norms))), norms, s=4, color=TEAL, alpha=0.35, linewidths=0)
kk = np.logspace(0, np.log10(200), 200); ax.plot(kk, 1 / np.sqrt(kk), color=COPPER, lw=2, label="1/√k")
ax.axhline(1.0, color=CREAM, lw=1, ls="--", alpha=0.6); ax.text(1.05, 1.02, "the wall: only [n,n,n,…] sits here", color=CREAM, fontsize=11)
ax.set_xscale("log"); ax.set_ylim(0, 1.1); ax.set_xlabel("constituents averaged (k)", fontsize=12); ax.set_ylabel("distance from the origin", fontsize=12)
ax.legend(frameon=False, labelcolor=CREAM, fontsize=12)
fig.text(0.06, 0.93, "Everything falls toward the center", fontsize=24, color=CREAM, weight="bold")
fig.text(0.06, 0.895, "Compositions of random codepoints: the more varied the constituents, the deeper they sit", fontsize=12, color=INK)
fig.savefig(f"{OUT}/03-falling-inward.png", dpi=150, facecolor=NAVY); plt.close(fig)
print("rendered")
