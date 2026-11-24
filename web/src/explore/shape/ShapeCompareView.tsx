import { useEffect, useRef, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { Badge, Button, ErrorText, Field, Input, LoadingText, Muted, Panel, Stack } from '@ui';
import { plainMessage } from '../../api/client';
import { useAppStore } from '../../store';
import { WorldLink } from '../../world/WorldLink';
import { exploreShape, type ExploreShapeComparisonResponse, type ShapePoint4D } from '../api';
import styles from './ShapeCompareView.module.css';

function drawTrajectories(canvas: HTMLCanvasElement, xPts: ShapePoint4D[], yPts: ShapePoint4D[]) {
  const ctx = canvas.getContext('2d');
  if (!ctx) return;
  const style = getComputedStyle(canvas);
  const bg = style.getPropertyValue('--color-bg-panel-2').trim();
  const axis = style.getPropertyValue('--color-border-default').trim();
  const colorX = style.getPropertyValue('--color-accent').trim();
  const colorY = style.getPropertyValue('--color-confirm').trim();
  const fg = style.getPropertyValue('--color-text-primary').trim();

  const w = canvas.width;
  const h = canvas.height;
  ctx.clearRect(0, 0, w, h);
  if (bg) {
    ctx.fillStyle = bg;
    ctx.fillRect(0, 0, w, h);
  }

  const all = [...xPts, ...yPts];
  if (all.length === 0) return;

  let minX = Infinity;
  let maxX = -Infinity;
  let minY = Infinity;
  let maxY = -Infinity;
  for (const p of all) {
    const u = p.x;
    const v = p.y;
    if (u < minX) minX = u;
    if (u > maxX) maxX = u;
    if (v < minY) minY = v;
    if (v > maxY) maxY = v;
  }
  const spanX = Math.max(0.05, maxX - minX);
  const spanY = Math.max(0.05, maxY - minY);
  const pad = 28;
  const px = (u: number) => pad + ((u - minX) / spanX) * (w - pad * 2);
  const py = (v: number) => h - pad - ((v - minY) / spanY) * (h - pad * 2);

  if (axis) {
    ctx.strokeStyle = axis;
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(pad, h / 2);
    ctx.lineTo(w - pad, h / 2);
    ctx.moveTo(w / 2, pad);
    ctx.lineTo(w / 2, h - pad);
    ctx.stroke();
  }

  const plotCurve = (pts: ShapePoint4D[], stroke: string, labelPrefix: string) => {
    if (!pts.length) return;
    ctx.strokeStyle = stroke;
    ctx.fillStyle = stroke;
    ctx.lineWidth = 2.5;
    ctx.beginPath();
    pts.forEach((p, idx) => {
      const cx = px(p.x);
      const cy = py(p.y);
      if (idx === 0) ctx.moveTo(cx, cy);
      else ctx.lineTo(cx, cy);
    });
    ctx.stroke();
    pts.forEach((p, idx) => {
      const cx = px(p.x);
      const cy = py(p.y);
      ctx.beginPath();
      ctx.arc(cx, cy, 4, 0, Math.PI * 2);
      ctx.fill();
      if (fg && (idx === 0 || idx === pts.length - 1)) {
        ctx.fillStyle = fg;
        ctx.fillText(`${labelPrefix}${idx + 1}`, cx + 6, cy - 6);
        ctx.fillStyle = stroke;
      }
    });
  };

  plotCurve(xPts, colorX || fg, 'X');
  plotCurve(yPts, colorY || fg, 'Y');
}

/** Realized 4-D trajectory comparison using native Fréchet, DTW, and Hausdorff distances. */
export function ShapeCompareView() {
  const [params] = useSearchParams();
  const nav = useNavigate();
  const { tenant, quoteId } = useAppStore();
  const x = params.get('x')?.trim() ?? 'dog';
  const y = params.get('y')?.trim() ?? 'wolf';
  const [draftX, setDraftX] = useState(x);
  const [draftY, setDraftY] = useState(y);
  const [data, setData] = useState<ExploreShapeComparisonResponse | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const canvasRef = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    setDraftX(x);
    setDraftY(y);
  }, [x, y]);

  useEffect(() => {
    if (!x || !y) return;
    const ac = new AbortController();
    setLoading(true);
    setErr(null);
    exploreShape(x, y, { tenant, quoteId, signal: ac.signal })
      .then((res) => setData(res))
      .catch((e) => { if (!ac.signal.aborted) setErr(plainMessage(e)); })
      .finally(() => { if (!ac.signal.aborted) setLoading(false); });
    return () => ac.abort();
  }, [x, y, tenant, quoteId]);

  useEffect(() => {
    if (!canvasRef.current || !data) return;
    drawTrajectories(canvasRef.current, data.x.points, data.y.points);
  }, [data]);

  function submit() {
    if (!draftX.trim() || !draftY.trim()) return;
    nav(`/explore/shape?x=${encodeURIComponent(draftX.trim())}&y=${encodeURIComponent(draftY.trim())}`);
  }

  return (
    <div className={styles.page}>
      <Stack gap={3}>
        <header>
          <h2>Realized 4-D trajectory shape</h2>
          <Muted>Fréchet, DTW, and Hausdorff distance over realized constituent 4-D curves. Shape similarity is structural, not meaning.</Muted>
        </header>

        <form className={styles.picker} onSubmit={(e) => { e.preventDefault(); submit(); }}>
          <Field label="First subject" htmlFor="shape-x">
            <Input id="shape-x" value={draftX} onChange={(e) => setDraftX(e.target.value)} placeholder="dog" />
          </Field>
          <Muted>vs</Muted>
          <Field label="Second subject" htmlFor="shape-y">
            <Input id="shape-y" value={draftY} onChange={(e) => setDraftY(e.target.value)} placeholder="wolf" />
          </Field>
          <Button type="submit" disabled={!draftX.trim() || !draftY.trim()}>Compare shape</Button>
        </form>

        {err && <ErrorText role="alert">{err}</ErrorText>}
        {loading && !data && <LoadingText>Reading realized 4-D trajectories…</LoadingText>}

        <Panel title={data ? `${data.x.label} vs ${data.y.label}` : 'Trajectory projection'}>
          <Stack gap={3}>
            {data && (
              <div className={styles.metrics}>
                <Badge>Fréchet {data.frechet_distance != null ? data.frechet_distance.toFixed(5) : '—'}</Badge>
                <Badge>DTW {data.dtw_distance != null ? data.dtw_distance.toFixed(5) : '—'}</Badge>
                <Badge>Hausdorff {data.hausdorff_distance != null ? data.hausdorff_distance.toFixed(5) : '—'}</Badge>
              </div>
            )}
            <canvas ref={canvasRef} width={760} height={300} className={styles.canvas} aria-label="4-D trajectory shape projection" />
          </Stack>
        </Panel>

        {data && (
          <div className={styles.grid}>
            {[data.x, data.y].map((side) => (
              <Panel key={side.entity_id_hex + side.reference} title={side.label}>
                <Stack gap={2}>
                  <Muted>
                    Tier {side.tier} · {side.points.length} realized vertices ·{' '}
                    <WorldLink to={`/explore/entity/${side.entity_id_hex}`}>Open entity</WorldLink>
                  </Muted>
                  <ol className={styles.list}>
                    {side.points.slice(0, 12).map((pt) => (
                      <li key={pt.ordinal}>
                        [{pt.x.toFixed(4)}, {pt.y.toFixed(4)}, {pt.z.toFixed(4)}, {pt.m.toFixed(4)}]
                      </li>
                    ))}
                  </ol>
                </Stack>
              </Panel>
            ))}
          </div>
        )}
      </Stack>
    </div>
  );
}
