import { useEffect, useMemo, useState } from 'react';
import { Canvas } from '@react-three/fiber';
import { OrbitControls } from '@react-three/drei';
import * as THREE from 'three';
import { ErrorText, LoadingText } from '@ui';
import { exploreUnicodeCloud, exploreUnicodePoint, exploreUnicodePositions } from '../api';
import { clientArtifactGet, clientArtifactPut, clientStorageEstimate } from '../../storage/clientArtifactCache';
import type { UnicodeCloudResponse, UnicodePointResponse } from '../types';
import styles from './UnicodeGlomeView.module.css';

function Cloud({p,pick}:{p:Float32Array;pick:(i:number)=>void}){const g=useMemo(()=>{const x=new THREE.BufferGeometry();x.setAttribute('position',new THREE.BufferAttribute(p,3));return x;},[p]);useEffect(()=>()=>g.dispose(),[g]);return <><points geometry={g} onClick={e=>{e.stopPropagation();if(e.index!=null)pick(e.index);}}><pointsMaterial size={.0038} sizeAttenuation color="#e2e8f0" transparent opacity={.72} toneMapped={false}/></points><mesh><sphereGeometry args={[1,32,32]}/><meshBasicMaterial wireframe transparent opacity={.28} color="#94a3b8" toneMapped={false}/></mesh><OrbitControls makeDefault enablePan enableZoom/></>;}
export function UnicodeGlomeView(){
 const [cloud,setCloud]=useState<UnicodeCloudResponse|null>(null),[buffer,setBuffer]=useState<ArrayBuffer|null>(null),[err,setErr]=useState<string|null>(null),[mode,setMode]=useState<'real'|'hash'>('real'),[point,setPoint]=useState<UnicodePointResponse|null>(null);
 const [cacheState,setCacheState]=useState<'checking'|'hit'|'stored'|'unavailable'>('checking');
 const [storage,setStorage]=useState<{usage:number;quota:number}|null>(null);
 useEffect(()=>{const controller=new AbortController();void (async()=>{try{
   const meta=await exploreUnicodeCloud({signal:controller.signal}); if(controller.signal.aborted)return; setCloud(meta);
   const kind=`unicode-cloud:${meta.positions_format}`;
   let data:ArrayBuffer|null=null;
   try {
     data=await clientArtifactGet(kind,meta.perfcache_receipt_hex);
     if(data && data.byteLength===meta.positions_bytes) setCacheState('hit'); else data=null;
   } catch { setCacheState('unavailable'); }
   if(!data){
     data=await exploreUnicodePositions({signal:controller.signal});
     if(data.byteLength!==meta.positions_bytes) throw new Error(`Unicode GPU artifact size mismatch: expected ${meta.positions_bytes}, received ${data.byteLength}`);
     try { await clientArtifactPut(kind,meta.perfcache_receipt_hex,data); setCacheState('stored'); }
     catch { setCacheState('unavailable'); }
   }
   if(!controller.signal.aborted){ setBuffer(data); setStorage(await clientStorageEstimate()); }
 }catch(e){if(!controller.signal.aborted)setErr(e instanceof Error?e.message:String(e));}})();return()=>controller.abort();},[]);
 const pos=useMemo(()=>{if(!cloud||!buffer)return null;const floats=new Float32Array(buffer),lane=cloud.count*3,offset=mode==='real'?0:lane;return floats.subarray(offset,offset+lane);},[cloud,buffer,mode]);
 const pick=(cp:number)=>void exploreUnicodePoint(cp).then(setPoint).catch(e=>setErr(e instanceof Error?e.message:String(e)));
 return <div className={styles.root}><header><div><span>Tier-0 instrument</span><h2>Unicode Glome</h2><p>Every position in the active Unicode T0 ROM. The physicality view projects the real 4-D open Super-Fibonacci placement onto a 3-D shell; the hash view bit-packs the canonical Hash128 into deterministic mantissa lanes and normalizes that repeatable jumble onto the shell.</p></div><div className={styles.controls}><button className={mode==='real'?styles.active:''} onClick={()=>setMode('real')}>Physicality</button><button className={mode==='hash'?styles.active:''} onClick={()=>setMode('hash')}>Hash / mantissa</button></div></header>{err?<ErrorText>{err}</ErrorText>:null}{!pos?<LoadingText>Loading the complete T0 ROM GPU buffers…</LoadingText>:<div className={styles.layout}><div className={styles.canvas}><Canvas frameloop="demand" camera={{position:[0,0,2.35],fov:48}}><color attach="background" args={['#0b1220']}/><Cloud p={pos} pick={pick}/></Canvas></div><aside><strong>{cloud?.count.toLocaleString()} points</strong><code>{cloud?.perfcache_receipt_hex}</code><div className={styles.cacheState}><span>Client artifact cache</span><strong>{cacheState==='hit'?'HIT · IndexedDB':cacheState==='stored'?'STORED · IndexedDB':cacheState==='checking'?'checking…':'unavailable · network only'}</strong>{storage?<small>{(storage.usage/1048576).toFixed(1)} MiB used / {(storage.quota/1073741824).toFixed(1)} GiB quota</small>:null}</div>{point?<><h3>U+{point.codepoint.toString(16).toUpperCase().padStart(4,'0')} {point.display}</h3><dl><dt>DUCET rank</dt><dd>{point.uca_order.toLocaleString()}</dd><dt>Content ID</dt><dd>{point.id_hex}</dd><dt>Real PointZM</dt><dd>{point.x.toPrecision(9)}, {point.y.toPrecision(9)}, {point.z.toPrecision(9)}, {point.m.toPrecision(9)}</dd><dt>r₄</dt><dd>{point.radius.toPrecision(12)}</dd><dt>Hilbert128</dt><dd>{point.hilbert_hex}</dd><dt>Flags</dt><dd>0x{point.flags.toString(16)}</dd></dl></>:<p>Click any point to read its canonical perfcache record and real 4-D coordinates.</p>}</aside></div>}</div>;
}
