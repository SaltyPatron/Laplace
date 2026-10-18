"""Reproducible analytic figures; no invented placement measurements or DB runs."""
from pathlib import Path
from atlas_paths import REPOSITORIES, TOOLS, PLANTUML, JAVA
import sys,json,hashlib,html
from datetime import datetime,timezone
ROOT=Path(__file__).resolve().parent
sys.path.insert(0,str(TOOLS/'.plotdeps'))
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
OUT=ROOT/'publication-figures'; OUT.mkdir(exist_ok=True)
NAVY='#14293c'; TEAL='#168b88'; COPPER='#be6a31'
plt.rcParams.update({'font.family':'DejaVu Sans','font.size':12,'axes.spines.top':False,'axes.spines.right':False,'text.color':NAVY,'axes.labelcolor':NAVY,'svg.fonttype':'none'})
figures=[]
def save(fig,id,title,caption):
    for ext in ('svg','png','pdf'): fig.savefig(OUT/f'{id}.{ext}',dpi=180,facecolor='white',bbox_inches='tight')
    plt.close(fig)
    figures.append(dict(id=id,title=title,caption=caption,kind='Analytic illustration, not a measured Laplace corpus result'))

k=np.geomspace(1,1000,400)
fig,ax=plt.subplots(figsize=(11,7))
fig.subplots_adjust(top=.78,bottom=.18)
for mu,col in [(0,NAVY),(.5,TEAL),(.85,COPPER)]:
    ax.plot(k,np.sqrt(mu*mu+(1-mu*mu)/k),lw=2.5,color=col,label=f'Population mean length = {mu:g}')
    if mu: ax.axhline(mu,color=col,alpha=.35,ls=':')
ax.set(xscale='log',xlabel='Independent draws with replacement, k',ylabel='Root mean square centroid length',ylim=(0,1.05))
ax.legend(frameon=False,loc='center right');ax.grid(alpha=.15)
fig.suptitle('Averages approach the population mean',fontsize=22,x=.125,ha='left')
fig.text(.125,.86,r'$\sqrt{E\|\bar X_k\|^2}=\sqrt{\|\mu\|^2+(E\|X\|^2-\|\mu\|^2)/k}$',fontsize=16)
fig.text(.125,.025,'Analytic unit-vector examples. These curves are RMS lengths, not mean lengths.\nThe three mean lengths are illustrative; none is a measured Unicode subset.',fontsize=11)
save(fig,'centroid-population','Averages approach the population mean','Independent identically distributed samples give E||mean||² = ||μ||² + (E||X||² − ||μ||²)/k. The 1/√k RMS curve requires zero mean and unit squared norm. Correlated text, hierarchy and fixed-point truncation need their own analysis.')

fig,(ax,bx)=plt.subplots(1,2,figsize=(12,5.7),gridspec_kw={'width_ratios':[1,1.2]})
t=np.linspace(0,2*np.pi,500);ax.plot(np.cos(t),np.sin(t),color=NAVY,alpha=.4)
pts=np.array([[1,0],[-1,0],[1,0]],float);means=np.cumsum(pts,axis=0)/np.arange(1,4)[:,None]
ax.scatter([-1,1],[0,0],color=TEAL,s=90);ax.scatter([0,1/3],[0,0],color=COPPER,s=75)
for x,y,label in [(-1,0,'B'),(1,0,'A'),(0,0,'mean(A,B)'),(1/3,0,'mean(A,B,A)')]:
    ax.annotate(label,(x,y),xytext=(0,18 if x!=0 else -30),textcoords='offset points',ha='center',fontsize=10)
ax.set(aspect='equal',xlim=(-1.25,1.25),ylim=(-1.2,1.2));ax.set_axis_off()
bx.plot([1,2,3],np.linalg.norm(means,axis=1),'o-',color=COPPER,lw=3)
bx.set(xticks=[1,2,3],xticklabels=['A','A, B','A, B, A'],ylim=(-.05,1.1),ylabel='Centroid length',xlabel='Ordered input sequence')
bx.grid(alpha=.15)
fig.suptitle('Inside the wall does not mean always moving inward',fontsize=20)
fig.text(.1,-.015,'Analytic 2D section of a 4D ball. Appending A raises the length from 0 to 1/3.\nConvexity still holds: the parent length is bounded by the average length of its immediate children.',fontsize=11)
save(fig,'wall-and-order','Inside the wall; no universal inward step','The triangle inequality bounds a parent by the average norm of its immediate children, not by each child or the previous prefix. A hierarchy averages immediate children, so regrouping generally changes the effective leaf weights. This example does not use Unicode placement data.')

fig,ax=plt.subplots(figsize=(13,6.8));ax.set_axis_off()
boxes=[(.02,.56,.28,.28,'ORDERED IDENTITY','[t, a, r] ≠ [r, a, t]\nHash ordered child IDs\nRetain order and repeats'),(.36,.56,.28,.28,'CENTROID GEOMETRY','Same immediate multiset\n→ same integer sum / count\n→ same centroid'),(.70,.56,.28,.28,'RECONSTRUCTION','Read the stored child path\nExpand runs / descendants\nRecover the original sequence')]
from matplotlib.patches import FancyBboxPatch
for x,y,w,h,title,body in boxes:
    ax.add_patch(FancyBboxPatch((x,y),w,h,boxstyle='round,pad=0.012',facecolor='#eef5f8',edgecolor=TEAL,transform=ax.transAxes))
    ax.text(x+.02,y+h-.06,title,weight='bold',fontsize=12,transform=ax.transAxes)
    ax.text(x+.02,y+.055,body,fontsize=11,linespacing=1.6,transform=ax.transAxes)
ax.text(.03,.35,'Same position can hold different ordered content.',fontsize=22,weight='bold',transform=ax.transAxes)
ax.text(.03,.26,'Geometry supplies a location. The ordered content address and stored path carry identity and order.\nPrototype code checks a hash hit against the children, but a mismatch increments a counter\nand returns the existing record; that path is not collision repair.',fontsize=12,linespacing=1.6,va='top',transform=ax.transAxes)
fig.suptitle('A location is not the content address',fontsize=23,x=.14,ha='left')
save(fig,'identity-and-geometry','A location is not the content address','Prototype dag/ingest.c: one-child collapse; BLAKE3-128 over ordered child IDs, including repeats; integer centroid; run-length path. Different ordered preimages ordinarily produce different IDs, subject to hash collisions. No collision-free proof or lookup-performance claim is made.')

prototype='d488b3b097421a9aed8de713b7ae0485857631dd';patreon='4416c2750051578e4bb7e13281fc526cef902d18'
sources=[]
for repo,rev,base,paths in [
 ('Laplace-Prototype',prototype,TOOLS/'research-sources/Laplace-Prototype',['README.md','tier0/gen_tier0.py','tier0/fingerprint.txt','dag/ingest.c','tests/verify.py']),
 ('Laplace-Wiki','1145467f6da11b13a5916f0c70d4282d555ab2d3',(REPOSITORIES / 'Laplace-Wiki'),['Research/Placement.md','Research/Prototype.md','Research/Numerics.md']),
 ('Laplace-Engine','64f6184c6c92949e4c58f1d1f27f9414406a56d0',(REPOSITORIES / 'Laplace-Engine'),['src/tier0.c']),
 ('Laplace-Native','8022bbddb5f6ab33c7c8b94b1a9247a057da50e1',(REPOSITORIES / 'Laplace-Native'),['src/coord.c']),
 ('Laplace-Patreon',patreon,ROOT/'publication-sources',['scripts/render.py','drafts/01-every-character-on-one-sphere.md','drafts/02-the-cosmic-wall.md','drafts/03-git-for-all-knowledge.md'])]:
    for path in paths:
        p=base/path;sources.append(dict(repo=repo,revision=rev,path=path,local=str(p),sha256=hashlib.sha256(p.read_bytes()).hexdigest(),url=f'https://github.com/SaltyPatron/{repo}/blob/{rev}/{path}'))
findings=[
 ('Old image 03 versus revised draft 02','The renderer samples assigned codepoints and overlays 1/√k. The revised draft requests an unrendered prototype database tier-band figure. They are different claims and neither supplies local measurement inputs.'),
 ('Exact repeats versus exact unit norm','Both generators nudge rounded coordinates inward until the integer squared norm is at most 2^106. A repeat preserves its constituent coordinate; that does not establish that the stored coordinate lies exactly at norm one.'),
 ('Measurement precision','Prototype verify.py checks the wall using integer squared norms. Its sampled average-depth check uses Decimal square roots at precision 80 with tolerance 1e−40 grid units. Calling every check exact integer arithmetic overstates the latter test.'),
 ('Verification coverage','The count equality check does not by itself establish one physicality per entity. ID/tier checks are sampled; recomposition checks select two books; an idempotence test launches ingest in a temporary output directory. None of those tests was executed for this atlas.'),
 ('Fingerprint boundaries','Prototype fingerprint.txt reports SHA-256 of its table and order. Current modular Engine prints BLAKE3-256 and uses CORE-MATH trigonometry. A digest from one generator is not verification of the other.'),
 ('Required empirical inputs','P_h1.npy, assigned.npy, pinned Unicode script data and the historical prototype database were not supplied in these source repositories. The published distance and 3.9-million-record results remain attributed reports, not reproduced results.'),
 ('Publication scope','These figures explain researched storage geometry boundaries. They supplement the whole-system operation atlas; they do not replace its authority, learning, runtime, modality, deployment or recovery flows.')]
data=dict(updated_utc=datetime.now(timezone.utc).isoformat(),sources=sources,figures=figures,findings=[dict(topic=a,observation=b) for a,b in findings],empirical_reproduction=False)
(ROOT/'publication-evidence.json').write_text(json.dumps(data,indent=2,ensure_ascii=False),encoding='utf-8')
print(json.dumps({'publication_figures':len(figures),'empirical_reproduction':False}))
