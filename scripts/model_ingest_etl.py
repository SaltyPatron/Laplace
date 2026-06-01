#!/usr/bin/env python3
# Laplace model-ingestion ETL: a transformer witnessed as content x content attestations,
# the same kinds WordNet/ConceptNet/UD give. Trace trajectories (reproduce the computation),
# read typed relations from in-context completions, dedup into consensus edges. No probing-as-
# inference; one trace per content unit, witnessed and accumulated.
import json, struct, os, sys, numpy as np
from collections import defaultdict
M=sys.argv[1]; OUT=sys.argv[2]
vocab=json.load(open(os.path.join(M,"tokenizer.json")))["model"]["vocab"]; id2={v:k for k,v in vocab.items()}
f=open(os.path.join(M,"model.safetensors"),"rb"); n=struct.unpack("<Q",f.read(8))[0]; hdr=json.loads(f.read(n)); base=8+n
def L(nm):
    i=hdr[nm]; s,e=i["data_offsets"]; f.seek(base+s); raw=f.read(e-s)
    u=np.frombuffer(raw,dtype=np.uint16); return ((u.astype(np.uint32)<<16).view(np.float32)).reshape(i["shape"]).astype(np.float32)
emb=L("model.embed_tokens.weight"); head=L("lm_head.weight"); fn=L("model.norm.weight")
NL,H,KV,HD,D=22,32,4,64,2048
def rmsr(x,w,eps=1e-5): return x/np.sqrt((x*x).mean(-1,keepdims=True)+eps)*w
def sig(x): return 1/(1+np.exp(-np.clip(x,-30,30)))
W=[dict(q=L(f"model.layers.{l}.self_attn.q_proj.weight"),k=L(f"model.layers.{l}.self_attn.k_proj.weight"),
        v=L(f"model.layers.{l}.self_attn.v_proj.weight"),o=L(f"model.layers.{l}.self_attn.o_proj.weight"),
        iln=L(f"model.layers.{l}.input_layernorm.weight"),pln=L(f"model.layers.{l}.post_attention_layernorm.weight"),
        g=L(f"model.layers.{l}.mlp.gate_proj.weight"),u=L(f"model.layers.{l}.mlp.up_proj.weight"),
        d=L(f"model.layers.{l}.mlp.down_proj.weight")) for l in range(NL)]
def rope(seq):
    inv=1.0/(10000.0**(np.arange(0,HD,2)/HD)); t=np.arange(seq)[:,None]*inv[None,:]
    return np.concatenate([np.cos(t),np.cos(t)],1), np.concatenate([np.sin(t),np.sin(t)],1)
def roth(x): h=x.shape[-1]//2; return np.concatenate([-x[...,h:],x[...,:h]],-1)
def fwd(toks):
    seq=len(toks); x=emb[toks].astype(np.float32); c,s=rope(seq)
    mask=np.triu(np.full((seq,seq),-1e30,np.float32),1)
    for w in W:
        h=rmsr(x,w["iln"])
        q=(h@w["q"].T).reshape(seq,H,HD); k=(h@w["k"].T).reshape(seq,KV,HD); v=(h@w["v"].T).reshape(seq,KV,HD)
        q=q*c[:,None,:]+roth(q)*s[:,None,:]; k=k*c[:,None,:]+roth(k)*s[:,None,:]
        k=np.repeat(k,H//KV,1); v=np.repeat(v,H//KV,1)
        sc=np.einsum('qhd,khd->hqk',q,k)/np.sqrt(HD)+mask
        sc-=sc.max(-1,keepdims=True); p=np.exp(sc); p/=p.sum(-1,keepdims=True)
        out=np.einsum('hqk,khd->qhd',p,v).reshape(seq,D)
        x=x+out@w["o"].T
        h2=rmsr(x,w["pln"]); a=h2@w["g"].T; a=a*sig(a)*(h2@w["u"].T); x=x+a@w["d"].T
    lg=rmsr(x,fn)[-1]@head.T; pr=np.exp(lg-lg.max()); pr/=pr.sum(); return pr
def tk(s):
    t=vocab.get("▁"+s, vocab.get(s)); return t
# typed relational templates: kind -> token sequence with X slot (completion read at the end)
TEMPL=[("IS_A",       ["A","{X}","is","a"]),
       ("ANTONYM",    ["The","opposite","of","{X}","is"]),
       ("CAPITAL_OF", ["The","capital","of","{X}","is"]),
       ("USED_FOR",   ["A","{X}","is","used","for"]),
       ("HAS_PROP",   ["{X}","is","very"]),
       ("PART_OF",    ["A","{X}","is","part","of","a"])]
ENT=["king","queen","Paris","France","Japan","Germany","dog","cat","water","fire","sun","moon",
     "hot","cold","big","small","fast","slow","happy","sad","car","boat","knife","hammer","book",
     "doctor","teacher","river","mountain","apple","bread","gold","iron","red","blue","summer","winter"]
def seqtoks(tmpl,X):
    out=[]
    for part in tmpl:
        w=X if part=="{X}" else part
        t=tk(w)
        if t is None: return None
        out.append(t)
    return out
edges=defaultdict(lambda:[0.0,0]); REL=0
for kind,tmpl in TEMPL:
    for X in ENT:
        ids=seqtoks(tmpl,X)
        if ids is None: continue
        pr=fwd(ids)
        for j in np.argsort(-pr)[:3]:
            B=id2[int(j)].lstrip("▁")
            if not B or not B[0].isalpha(): continue
            e=edges[(X,kind,B)]; e[0]+=float(pr[j]); e[1]+=1; REL+=1
# trajectory relatedness (single-token) for the same entities -> SIMILAR_TO
def traj(tid):
    x=emb[tid].copy(); T=[x.copy()]
    for w in W:
        h=rmsr(x,w["iln"]); vh=(w["v"]@h).reshape(KV,HD); x=x+w["o"]@np.repeat(vh,H//KV,0).reshape(D)
        h2=rmsr(x,w["pln"]); x=x+w["d"]@((w["g"]@h2)*sig(w["g"]@h2)*(w["u"]@h2))
        T.append(x.copy())
    return np.array(T)
eids=[(e,tk(e)) for e in ENT]; eids=[(e,i) for e,i in eids if i is not None]
TRm=np.stack([traj(i) for _,i in eids]); TRm-=TRm.mean(0,keepdims=True)
Fm=TRm.reshape(len(eids),-1); Fm/=(np.linalg.norm(Fm,1.0 if False else None,axis=1,keepdims=True)+1e-9)
Sm=Fm@Fm.T; np.fill_diagonal(Sm,-1)
for i,(A,_) in enumerate(eids):
    for j in np.argsort(-Sm[i])[:3]:
        B=eids[int(j)][0]; e=edges[(A,"SIMILAR_TO",B)]; e[0]+=float(Sm[i,j]); e[1]+=1; REL+=1
with open(OUT,"w") as o:
    o.write("subject\tkind\tobject\twitnesses\tconsensus\n")
    for (A,k,B),(w,c) in sorted(edges.items(),key=lambda kv:(kv[0][1],-kv[1][0])):
        o.write(f"{A}\t{k}\t{B}\t{c}\t{round(w,3)}\n")
print(f"witnessed {REL} relations -> DEDUP -> {len(edges)} unique typed attestations -> {OUT}")
bykind=defaultdict(int)
for (A,k,B) in edges: bykind[k]+=1
print("by kind:", dict(bykind))
print("--- samples (top per kind) ---")
seen=set()
for (A,k,B),(w,c) in sorted(edges.items(),key=lambda kv:(kv[0][1],-kv[1][0])):
    if k in seen and list(edges).count: 
        pass
    if (k) not in seen:
        seen.add(k)
    if len([1 for kk in seen if kk==k]):
        pass
for kind,_ in TEMPL+[("SIMILAR_TO",None)]:
    rows=[((A,k,B),v) for (A,k,B),v in edges.items() if k==kind]
    rows.sort(key=lambda r:-r[1][0])
    for (A,k,B),(w,c) in rows[:4]:
        print(f"  {A:9s} --{k}--> {B:12s} (x{c}, {round(w,2)})")
