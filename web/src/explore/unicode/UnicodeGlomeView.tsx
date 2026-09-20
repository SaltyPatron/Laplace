import { useEffect, useMemo, useState } from 'react';
import { Canvas } from '@react-three/fiber';
import { OrbitControls } from '@react-three/drei';
import * as THREE from 'three';
import { ErrorText, LoadingText } from '@ui';
import { exploreUnicodeCloud, exploreUnicodePoint } from '../api';
import type { UnicodeCloudResponse, UnicodePointResponse } from '../types';
import styles from './UnicodeGlomeView.module.css';

const TAU=Math.PI*2, PHI=Math.SQRT2, PSI=1.5337511687552043;
function radical(n:number){let f=.5,o=0;n>>>=0;while(n){if(n&1)o+=f;n>>>=1;f*=.5;}return o;}
function open(rank:number){const s=rank+.5,t=radical(rank),r=Math.sqrt(t),c=Math.sqrt(1-t),a=s*(TAU/PHI),b=s*(TAU/PSI);return [r*Math.sin(a),r*Math.cos(a),c*Math.sin(b),c*Math.cos(b)] as const;}
function shell(x:number,y:number,z:number){const d=Math.hypot(x,y,z)||1;return [x/d,y/d,z/d] as const;}
function bytes(s:string){const r=atob(s),o=new Uint8Array(r.length);for(let i=0;i<r.length;i++)o[i]=r.charCodeAt(i);return o;}
function hashShell(h:Uint8Array,o:number){const d=new DataView(h.buffer,h.byteOffset+o,16),hi=d.getBigUint64(0,true),lo=d.getBigUint64(8,true),m=(1n<<53n)-1n;const x=Number(lo&m)/Number(m)*2-1;const yb=((hi&((1n<<42n)-1n))<<11n)|((lo>>53n)&0x7ffn);const y=Number(yb&m)/Number(m)*2-1;const zb=(hi>>42n)&((1n<<22n)-1n),z=Number(zb)/Number((1n<<22n)-1n)*2-1;return shell(x,y,z);}
function Cloud({p,pick}:{p:Float32Array;pick:(i:number)=>void}){const g=useMemo(()=>{const x=new THREE.BufferGeometry();x.setAttribute('position',new THREE.BufferAttribute(p,3));return x;},[p]);useEffect(()=>()=>g.dispose(),[g]);return <><points geometry={g} onClick={e=>{e.stopPropagation();if(e.index!=null)pick(e.index);}}><pointsMaterial size={.0038} sizeAttenuation color="#e2e8f0" transparent opacity={.72} toneMapped={false}/></points><mesh><sphereGeometry args={[1,32,32]}/><meshBasicMaterial wireframe transparent opacity={.28} color="#94a3b8" toneMapped={false}/></mesh><OrbitControls makeDefault enablePan enableZoom/></>;}
export function UnicodeGlomeView(){
 const [cloud,setCloud]=useState<UnicodeCloudResponse|null>(null),[err,setErr]=useState<string|null>(null),[mode,setMode]=useState<'real'|'hash'>('real'),[point,setPoint]=useState<UnicodePointResponse|null>(null);
 useEffect(()=>{void exploreUnicodeCloud().then(setCloud).catch(e=>setErr(e instanceof Error?e.message:String(e)));},[]);
 const decoded=useMemo(()=>{if(!cloud)return null;const ob=bytes(cloud.uca_order_u32_base64),hb=bytes(cloud.hash128_base64);return {orders:new Uint32Array(ob.buffer,ob.byteOffset,ob.byteLength/4),hashes:hb};},[cloud]);
 const pos=useMemo(()=>{if(!cloud||!decoded)return null;const out=new Float32Array(cloud.count*3);for(let cp=0;cp<cloud.count;cp++){let q;if(mode==='real'){const r=open(decoded.orders[cp]);q=shell(r[0],r[1],r[2]);}else q=hashShell(decoded.hashes,cp*16);out[cp*3]=q[0];out[cp*3+1]=q[1];out[cp*3+2]=q[2];}return out;},[cloud,decoded,mode]);
 const pick=(cp:number)=>void exploreUnicodePoint(cp).then(setPoint).catch(e=>setErr(e instanceof Error?e.message:String(e)));
 return <div className={styles.root}><header><div><span>Tier-0 instrument</span><h2>Unicode Glome</h2><p>Every position in the active Unicode T0 ROM. The physicality view projects the real 4-D open Super-Fibonacci placement onto a 3-D shell; the hash view bit-packs the canonical Hash128 into deterministic mantissa lanes and normalizes that repeatable jumble onto the shell.</p></div><div className={styles.controls}><button className={mode==='real'?styles.active:''} onClick={()=>setMode('real')}>Physicality</button><button className={mode==='hash'?styles.active:''} onClick={()=>setMode('hash')}>Hash / mantissa</button></div></header>{err?<ErrorText>{err}</ErrorText>:null}{!pos?<LoadingText>Loading the complete T0 ROM cloud…</LoadingText>:<div className={styles.layout}><div className={styles.canvas}><Canvas frameloop="demand" camera={{position:[0,0,2.35],fov:48}}><color attach="background" args={['#0b1220']}/><Cloud p={pos} pick={pick}/></Canvas></div><aside><strong>{cloud?.count.toLocaleString()} points</strong><code>{cloud?.perfcache_receipt_hex}</code>{point?<><h3>U+{point.codepoint.toString(16).toUpperCase().padStart(4,'0')} {point.display}</h3><dl><dt>DUCET rank</dt><dd>{point.uca_order.toLocaleString()}</dd><dt>Content ID</dt><dd>{point.id_hex}</dd><dt>Real PointZM</dt><dd>{point.x.toPrecision(9)}, {point.y.toPrecision(9)}, {point.z.toPrecision(9)}, {point.m.toPrecision(9)}</dd><dt>r₄</dt><dd>{point.radius.toPrecision(12)}</dd><dt>Hilbert128</dt><dd>{point.hilbert_hex}</dd><dt>Flags</dt><dd>0x{point.flags.toString(16)}</dd></dl></>:<p>Click any point to read its canonical perfcache record and real 4-D coordinates.</p>}</aside></div>}</div>;
}
