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
 * Head-to-head. Generic entities compare on consensus facts; typed domains may supply their own
 * evidence boundary. Chess_Player, for example, uses source Elo/career/head-to-head evidence
 * instead of pretending a relation's Glicko eff_mu is the player's chess rating.
 */
export function MatchupView() {
  const { x = '', y = '' } = useParams();
  const [params] = useSearchParams();
  const nav = useNavigate();
  const { tenant, quoteId } = useAppStore();

  const [xInput, setXInput] = useState(decodeURIComponent(x) || params.get('x') || '');
  const [yInput, setYInput] = useState(decodeURIComponent(y) || params.get('y') || '');

  const [data, setData] = useState<Matchup | null>(null);
  const [verdict, setVerdict] = useState<MatchupVerdict | null>(null);
  const [verdictError, setVerdictError] = useState<string | null>(null);
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
    setVerdictError(null);
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

    exploreMatchupVerdict(decodeURIComponent(x), decodeURIComponent(y), opts)
      .then((v) => { if (!stale) setVerdict(v); })
      .catch((e) => {
        if (stale) return;
        setVerdictError(
          e instanceof PaymentRequiredError
            ? 'verdict locked'
            : e instanceof Error ? e.message : String(e),
        );
      });

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
              <Verdict verdict={verdict} error={verdictError} />
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
  const chess = side.chess ?? null;
  return (
    <div className={`${styles.side} ${align === 'right' ? styles.right : ''}`}>
      <RouterLink className={styles.sideName} to={`/explore/entity/${side.id}`}>{side.label}</RouterLink>
      <div className={styles.record}>
        {chess ? (
          <>
            <Rec n={chess.wins} label="wins" tone="confirm" />
            <Rec n={chess.draws} label="draws" tone="draw" />
            <Rec n={chess.losses} label="losses" tone="refute" />
            <Rec n={chess.unscored} label="unscored" />
          </>
        ) : (
          <>
            <Rec n={side.record.confirmed} label="confirmed" tone="confirm" />
            <Rec n={side.record.contested} label="contested" tone="draw" />
            <Rec n={side.record.refuted} label="refuted" tone="refute" />
            <Rec n={side.record.thin} label="thin" />
          </>
        )}
      </div>
      <div className={styles.topStat}>
        {chess ? (
          <>
            <span className={styles.topMu}>{chess.peak_source_elo ?? '—'}</span>
            <span className={styles.topLabel}>peak source Elo · {chess.games.toLocaleString()} witnessed games</span>
          </>
        ) : (
          <>
            <span className={styles.topMu}>{topMu != null ? topMu.toFixed(0) : '—'}</span>
            <span className={styles.topLabel}>top relation standing · {side.record.thin + side.record.confirmed + side.record.contested + side.record.refuted} rated edges</span>
          </>
        )}
      </div>
      <ul className={styles.facts}>
        {side.top_facts.slice(0, 5).map((f, i) => {
          const type = chess && f.type.toLowerCase() === 'played by' ? 'played against' : f.type;
          return <li key={i}><span className={styles.factType}>{type}</span> {f.fact}</li>;
        })}
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

function Verdict({ verdict, error }: { verdict: MatchupVerdict | null; error: string | null }) {
  if (error) return <ErrorText className={styles.verdictPending}>no verdict — {error}</ErrorText>;
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
