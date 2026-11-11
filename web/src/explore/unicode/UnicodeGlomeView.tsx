import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { Canvas, useThree } from '@react-three/fiber';
import { OrbitControls } from '@react-three/drei';
import * as THREE from 'three';
import { Button, ErrorText, Field, Input, LoadingText } from '@ui';
import { exploreUnicodeCloud, exploreUnicodePoint, exploreUnicodePositions } from '../api';
import { clientArtifactGetOrLoad, clientStorageEstimate } from '../../storage/clientArtifactCache';
import type { UnicodeCloudResponse, UnicodePointResponse } from '../types';
import { useVisualizationPalette } from '../visualizationPalette';
import styles from './UnicodeGlomeView.module.css';

function GpuPicker({
  geometry,
  onPick,
}: {
  geometry: THREE.BufferGeometry;
  onPick: (index: number) => void;
}) {
  const { gl, camera } = useThree();

  useEffect(() => {
    const canvas = gl.domElement;
    const target = new THREE.WebGLRenderTarget(1, 1, {
      format: THREE.RGBAFormat,
      type: THREE.UnsignedByteType,
      depthBuffer: true,
      stencilBuffer: false,
    });
    target.texture.colorSpace = THREE.NoColorSpace;

    const material = new THREE.RawShaderMaterial({
      glslVersion: THREE.GLSL3,
      vertexShader: `
        precision highp float;
        precision highp int;
        in vec3 position;
        uniform mat4 modelViewMatrix;
        uniform mat4 projectionMatrix;
        flat out uint vertexId;
        void main() {
          vertexId = uint(gl_VertexID + 1);
          gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
          gl_PointSize = 7.0;
        }
      `,
      fragmentShader: `
        precision highp float;
        precision highp int;
        flat in uint vertexId;
        out vec4 outColor;
        void main() {
          vec2 d = gl_PointCoord - vec2(0.5);
          if (dot(d, d) > 0.25) discard;
          uint r = (vertexId >> 16u) & 255u;
          uint g = (vertexId >> 8u) & 255u;
          uint b = vertexId & 255u;
          outColor = vec4(float(r), float(g), float(b), 255.0) / 255.0;
        }
      `,
      depthTest: true,
      depthWrite: true,
      transparent: false,
      toneMapped: false,
    });
    const pickScene = new THREE.Scene();
    pickScene.add(new THREE.Points(geometry, material));
    const pixel = new Uint8Array(4);
    let downX = 0;
    let downY = 0;

    const pick = (clientX: number, clientY: number) => {
      const rect = canvas.getBoundingClientRect();
      if (rect.width <= 0 || rect.height <= 0) return;
      const x = Math.max(0, Math.min(rect.width - 1, clientX - rect.left));
      const y = Math.max(0, Math.min(rect.height - 1, clientY - rect.top));
      const fullWidth = Math.max(1, Math.round(rect.width));
      const fullHeight = Math.max(1, Math.round(rect.height));
      const previousTarget = gl.getRenderTarget();

      const perspective = camera as THREE.PerspectiveCamera;
      if (typeof perspective.setViewOffset !== 'function') return;
      perspective.setViewOffset(fullWidth, fullHeight, Math.floor(x), Math.floor(y), 1, 1);
      try {
        gl.setRenderTarget(target);
        gl.setClearColor(0x000000, 0);
        gl.clear(true, true, true);
        gl.render(pickScene, perspective);
        gl.readRenderTargetPixels(target, 0, 0, 1, 1, pixel);
      } finally {
        perspective.clearViewOffset();
        gl.setRenderTarget(previousTarget);
      }

      const encoded = (pixel[0] << 16) | (pixel[1] << 8) | pixel[2];
      if (encoded > 0) onPick(encoded - 1);
    };

    const onDown = (event: PointerEvent) => {
      downX = event.clientX;
      downY = event.clientY;
    };
    const onUp = (event: PointerEvent) => {
      if (Math.hypot(event.clientX - downX, event.clientY - downY) > 4) return;
      pick(event.clientX, event.clientY);
    };

    canvas.addEventListener('pointerdown', onDown);
    canvas.addEventListener('pointerup', onUp);
    return () => {
      canvas.removeEventListener('pointerdown', onDown);
      canvas.removeEventListener('pointerup', onUp);
      target.dispose();
      material.dispose();
    };
  }, [camera, geometry, gl, onPick]);

  return null;
}

function Cloud({
  p,
  pick,
  selected,
}: {
  p: Float32Array;
  pick: (i: number) => void;
  selected: number | null;
}) {
  const geometry = useMemo(() => {
    const value = new THREE.BufferGeometry();
    value.setAttribute('position', new THREE.BufferAttribute(p, 3));
    return value;
  }, [p]);
  useEffect(() => () => geometry.dispose(), [geometry]);
  const palette = useVisualizationPalette();

  const selectedPosition = selected != null && selected >= 0 && selected * 3 + 2 < p.length
    ? [p[selected * 3], p[selected * 3 + 1], p[selected * 3 + 2]] as [number, number, number]
    : null;

  return <>
    <points geometry={geometry}>
      <pointsMaterial
        size={1.15}
        sizeAttenuation={false}
        color={palette.primary}
        transparent={false}
        toneMapped={false}
      />
    </points>
    <GpuPicker geometry={geometry} onPick={pick} />
    {selectedPosition ? (
      <mesh position={selectedPosition}>
        <sphereGeometry args={[0.018, 12, 12]} />
        <meshBasicMaterial color={palette.signal} toneMapped={false} />
      </mesh>
    ) : null}
    <mesh>
      <sphereGeometry args={[1,32,32]} />
      <meshBasicMaterial wireframe transparent opacity={.28} color={palette.muted} toneMapped={false} />
    </mesh>
    <OrbitControls makeDefault enablePan enableZoom />
  </>;
}
/**
 * Read a codepoint from what a person typed: one character ("A", "é", "😀"),
 * "U+0041", "u+41", "0x41", or a decimal "#65". Returns null when the text
 * names no single scalar value.
 */
export function parseCodepointQuery(raw: string): number | null {
  const text = raw.trim();
  if (!text && raw.length > 0) {
    // A lone whitespace character is still a character.
    const only = Array.from(raw);
    return only.length === 1 ? only[0].codePointAt(0) ?? null : null;
  }
  if (!text) return null;
  const hex = /^(?:u\+|0x|\\u\{?)([0-9a-f]{1,6})\}?$/i.exec(text);
  const dec = /^#(\d{1,7})$/.exec(text);
  const chars = Array.from(text);
  const cp = hex ? parseInt(hex[1], 16)
    : dec ? parseInt(dec[1], 10)
    : chars.length === 1 ? chars[0].codePointAt(0) ?? -1
    : -1;
  return Number.isInteger(cp) && cp >= 0 && cp <= 0x10ffff ? cp : null;
}

function codepointLabel(cp: number): string {
  return `U+${cp.toString(16).toUpperCase().padStart(4, '0')}`;
}

export function UnicodeGlomeView(){
  const palette = useVisualizationPalette();
 const [cloud,setCloud]=useState<UnicodeCloudResponse|null>(null),[buffer,setBuffer]=useState<ArrayBuffer|null>(null),[err,setErr]=useState<string|null>(null),[mode]=useState<'real'|'hash'>('real'),[point,setPoint]=useState<UnicodePointResponse|null>(null);
 const [cacheState,setCacheState]=useState<'checking'|'hit'|'stored'|'unavailable'>('checking');
 const [storage,setStorage]=useState<{usage:number;quota:number}|null>(null);
 useEffect(()=>{const controller=new AbortController();void (async()=>{try{
   const meta=await exploreUnicodeCloud({signal:controller.signal}); if(controller.signal.aborted)return; setCloud(meta);
   const kind=`unicode-cloud:${meta.positions_format}`;
   const artifact=await clientArtifactGetOrLoad(
     kind, meta.perfcache_receipt_hex, meta.positions_bytes,
     () => exploreUnicodePositions(meta.perfcache_receipt_hex, {signal:controller.signal}),
   );
   setCacheState(artifact.source==='cache'?'hit':'stored');
   if(!controller.signal.aborted){ setBuffer(artifact.value); setStorage(await clientStorageEstimate()); }
 }catch(e){if(!controller.signal.aborted)setErr(e instanceof Error?e.message:String(e));}})();return()=>controller.abort();},[]);
 const pos=useMemo(()=>{if(!cloud||!buffer)return null;const floats=new Float32Array(buffer),lane=cloud.count*3,offset=mode==='real'?0:lane;return floats.subarray(offset,offset+lane);},[cloud,buffer,mode]);
 const [query,setQuery]=useState(''),[findError,setFindError]=useState<string|null>(null),[finding,setFinding]=useState(false);
 const pick=useMemo(() => (cp:number) => {
   void exploreUnicodePoint(cp).then(setPoint).catch(e=>setErr(e instanceof Error?e.message:String(e)));
 }, []);
 const find=async(event:FormEvent<HTMLFormElement>)=>{
   event.preventDefault();
   const cp=parseCodepointQuery(query);
   if(cp==null){setFindError('Type one character (A), a code point (U+0041 or 0x41), or #65.');return;}
   if(cloud&&cp>=cloud.count){setFindError(`${codepointLabel(cp)} is outside the ${cloud.count.toLocaleString()} positions in this T0 ROM.`);return;}
   setFindError(null);setFinding(true);
   try{setPoint(await exploreUnicodePoint(cp));}
   catch(e){setFindError(e instanceof Error?e.message:String(e));}
   finally{setFinding(false);}
 };
 return <div className={styles.root}><header><div><span>Tier-0 instrument</span><h2>Unicode Glome</h2><p>Every position in the active Unicode T0 ROM. The view projects each codepoint's real 4-D Super-Fibonacci S³ placement into the 3-D unit ball.</p></div><form className={styles.find} role="search" aria-label="Find a character" onSubmit={(e)=>void find(e)}><Field label="Find a character" help="A character, U+0041, 0x41 or #65" error={findError}><Input value={query} onChange={(e)=>{setQuery(e.target.value);setFindError(null);}} placeholder="A or U+0041" autoComplete="off" spellCheck={false}/></Field><Button type="submit" disabled={finding||!query}>{finding?'Finding…':'Find'}</Button></form></header>{err?<ErrorText>{err}</ErrorText>:null}{!pos?<LoadingText>Loading the complete T0 ROM GPU buffers…</LoadingText>:<div className={styles.layout}><div className={styles.canvas}><Canvas
  frameloop="demand"
  dpr={1}
  camera={{position:[0,0,2.35],fov:48}}
  gl={{ antialias: false, powerPreference: 'high-performance', preserveDrawingBuffer: false }}
><color attach="background" args={[palette.background]}/><Cloud p={pos} pick={pick} selected={point?.codepoint ?? null}/></Canvas></div><aside><strong>{cloud?.count.toLocaleString()} points</strong><code>{cloud?.perfcache_receipt_hex}</code><div className={styles.cacheState}><span>Client artifact cache</span><strong>{cacheState==='hit'?'HIT · IndexedDB':cacheState==='stored'?'STORED · IndexedDB':cacheState==='checking'?'checking…':'unavailable · network only'}</strong>{storage?<small>{(storage.usage/1048576).toFixed(1)} MiB used / {(storage.quota/1073741824).toFixed(1)} GiB quota</small>:null}</div>{point?<><h3>{codepointLabel(point.codepoint)} {point.display}</h3>{/^[0-9a-f]{32}$/i.test(point.id_hex)?<RouterLink className={styles.entityLink} to={`/explore/entity/${point.id_hex.toLowerCase()}`}>Open {codepointLabel(point.codepoint)} entity page</RouterLink>:null}<dl><dt>DUCET rank</dt><dd>{point.uca_order.toLocaleString()}</dd><dt>Content ID</dt><dd>{point.id_hex}</dd><dt>Real PointZM</dt><dd>{point.x.toPrecision(9)}, {point.y.toPrecision(9)}, {point.z.toPrecision(9)}, {point.m.toPrecision(9)}</dd><dt>r₄</dt><dd>{point.radius.toPrecision(12)}</dd><dt>Hilbert128</dt><dd>{point.hilbert_hex}</dd><dt>Flags</dt><dd>0x{point.flags.toString(16)}</dd></dl></>:<p>Click any point to read its canonical perfcache record and real 4-D coordinates.</p>}</aside></div>}</div>;
}
