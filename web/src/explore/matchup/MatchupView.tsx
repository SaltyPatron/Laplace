import { useEffect, useState } from 'react';
import { Link as RouterLink, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { Button, ErrorText, Input, LoadingText, Muted, Panel } from '@ui';
import { exploreMatchup, exploreMatchupVerdict } from '../../query/api';
import type { Matchup, MatchupSide, MatchupVerdict, TapeRow } from '../../query/types';
import { useAppStore } from '../../store';
import { PaymentRequiredError } from '../../api/client';
import { GatePrompt } from '../components/GatePrompt';
import styles from './MatchupView.module.css';

/**
 * Head-to-head. Two entities as a sports matchup: each side's card and record,
 * the tale of the tape (what each holds that the other doesn't, from
 * contrast()), and — fetched separately because its path search is slow — the
 * substrate's verdict on how they relate. This is the single most demoable
 * screen the rated graph can produce: two things, compared on the evidence.
 */
export function MatchupView() {
  const { x = '', y = '' } = useParams();
  const [params] = useSearchParams();
  const nav = useNavigate();
  const { tenant, quoteId } = useAppStore();

  // Path params drive the loaded comparison; ?x=/?y= only prefill the picker
  // (a "compare from here" handoff that waits for the second contender).
  const [xInput, setXInput] = useState(decodeURIComponent(x) || params.get('x') || '');
  const [yInput, setYInput] = useState(decodeURIComponent(y) || params.get('y') || '');

  const [data, setData] = useState<Matchup | null>(null);
  const [verdict, setVerdict] = useState<MatchupVerdict | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [gated, setGated] = useState(false);

  useEffect(() => {
    if (!x || !y) return;
    let stale = false;
    setLoading(true);
    setError(null);
    setData(null);
    setVerdict(null);
    setGated(false);

    const opts = { tenant, quoteId };
    exploreMatchup(decodeURIComponent(x), decodeURIComponent(y), opts)
      .then((m) => { if (!stale) setData(m); })
      .catch((e) => {
        if (stale) return;
        if (e instanceof PaymentRequiredError) setGated(true);
        else setError(e instanceof Error ? e.message : String(e));
      })
      .finally(() => { if (!stale) setLoading(false); });

    // The verdict rides its own request — path search runs seconds; never let
    // it hold up the cards and the tape.
    exploreMatchupVerdict(decodeURIComponent(x), decodeURIComponent(y), opts)
      .then((v) => { if (!stale) setVerdict(v); })
      .catch(() => { /* verdict is optional; the tape carries the comparison */ });

    return () => { stale = true; };
  }, [x, y, tenant, quoteId]);

  const submit = () => {
    if (!xInput.trim() || !yInput.trim()) return;
    nav(`/explore/matchup/${encodeURIComponent(xInput.trim())}/${encodeURIComponent(yInput.trim())}`);
  };

  return (
    <div className={styles.page}>
      <form
        className={styles.picker}
        onSubmit={(e) => { e.preventDefault(); submit(); }}
      >
        <Input aria-label="first entity" value={xInput} placeholder="dog" onChange={(e) => setXInput(e.target.value)} />
        <span className={styles.vs}>vs</span>
        <Input aria-label="second entity" value={yInput} placeholder="cat" onChange={(e) => setYInput(e.target.value)} />
        <Button type="submit" disabled={!xInput.trim() || !yInput.trim()}>Compare</Button>
      </form>

      {!x || !y ? (
        <Muted className={styles.hint}>Name two entities — a word or an id — to put them head to head.</Muted>
      ) : gated ? (
        <GatePrompt serviceId="inspect" label="Head-to-head comparison over the consensus graph." onReady={() => nav(0)} />
      ) : error ? (
        <ErrorText>{error}</ErrorText>
      ) : loading && !data ? (
        <LoadingText>Reading the evidence…</LoadingText>
      ) : data ? (
        <>
          <div className={styles.tale}>
            <SideCard side={data.x} align="left" />
            <div className={styles.center}>
              <span className={styles.vsBig}>vs</span>
              <Verdict verdict={verdict} />
            </div>
            <SideCard side={data.y} align="right" />
          </div>
          <Tape tape={data.tape} xLabel={data.x.label} yLabel={data.y.label} />
        </>
      ) : null}
    </div>
  );
}

function SideCard({ side, align }: { side: MatchupSide; align: 'left' | 'right' }) {
  const topMu = side.top_facts.length ? Math.max(...side.top_facts.map((f) => Number(f.eff_mu))) : null;
  return (
    <div className={`${styles.side} ${align === 'right' ? styles.right : ''}`}>
      <RouterLink className={styles.sideName} to={`/explore/entity/${side.id}`}>{side.label}</RouterLink>
      <div className={styles.record}>
        <Rec n={side.record.confirmed} label="confirmed" tone="confirm" />
        <Rec n={side.record.contested} label="contested" tone="draw" />
        <Rec n={side.record.refuted} label="refuted" tone="refute" />
        <Rec n={side.record.thin} label="thin" />
      </div>
      <div className={styles.topStat}>
        <span className={styles.topMu}>{topMu != null ? topMu.toFixed(0) : '—'}</span>
        <span className={styles.topLabel}>top rating · {side.record.thin + side.record.confirmed + side.record.contested + side.record.refuted} rated edges</span>
      </div>
      <ul className={styles.facts}>
        {side.top_facts.slice(0, 5).map((f, i) => (
          <li key={i}><span className={styles.factType}>{f.type}</span> {f.fact}</li>
        ))}
      </ul>
    </div>
  );
}

function Rec({ n, label, tone }: { n: number; label: string; tone?: 'confirm' | 'draw' | 'refute' }) {
  return (
    <div className={styles.rec}>
      <span className={`${styles.recN} ${tone ? styles[tone] : ''}`}>{n}</span>
      <span className={styles.recLabel}>{label}</span>
    </div>
  );
}

function Verdict({ verdict }: { verdict: MatchupVerdict | null }) {
  if (verdict === null) return <Muted className={styles.verdictPending}>weighing the path…</Muted>;
  if (!verdict.verdict) return <Muted className={styles.verdictPending}>no verdict</Muted>;
  return (
    <div className={styles.verdict}>
      <span className={styles.verdictText}>{verdict.verdict}</span>
      {verdict.relation && <span className={styles.verdictPath}>{verdict.relation}</span>}
      {verdict.eff_mu != null && <span className={styles.verdictMu}>μ {Number(verdict.eff_mu).toFixed(0)}</span>}
    </div>
  );
}

function Tape({ tape, xLabel, yLabel }: { tape: TapeRow[]; xLabel: string; yLabel: string }) {
  const both = tape.filter((t) => t.holder === 'both');
  const xOnly = tape.filter((t) => t.holder === 'x-only');
  const yOnly = tape.filter((t) => t.holder === 'y-only');
  return (
    <Panel title="Tale of the tape">
      <div className={styles.tapeGrid}>
        <TapeCol title={`only ${xLabel}`} rows={xOnly} />
        <TapeCol title="shared" rows={both} accent />
        <TapeCol title={`only ${yLabel}`} rows={yOnly} />
      </div>
    </Panel>
  );
}

function TapeCol({ title, rows, accent }: { title: string; rows: TapeRow[]; accent?: boolean }) {
  return (
    <div className={styles.tapeCol}>
      <div className={`${styles.tapeHead} ${accent ? styles.tapeShared : ''}`}>{title} <span className={styles.tapeCount}>{rows.length}</span></div>
      <ul className={styles.tapeRows}>
        {rows.length === 0 ? <li className={styles.tapeEmpty}>—</li> :
          rows.slice(0, 12).map((r, i) => (
            <li key={i}><span className={styles.tapeType}>{r.type}</span> {r.fact}</li>
          ))}
      </ul>
    </div>
  );
}
