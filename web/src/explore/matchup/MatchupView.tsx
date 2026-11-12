import { useEffect, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { WorldLink as RouterLink } from '../../world/WorldLink';
import { changeWorld } from '../../world/context';
import { useRouteText } from '../../layout/useRouteText';
import { Button, ErrorText, Input, LoadingText, Muted, Panel, useReadResource } from '@ui';
import { exploreMatchup, exploreResponseField, exploreLexicon } from '../../query/api';
import type { Matchup, MatchupSide, ResponseField, ResponseFieldEntry, LexiconReading, TapeRow } from '../../query/types';
import { useAppStore } from '../../store';
import { PaymentRequiredError } from '../../api/client';
import { GatePrompt } from '../components/GatePrompt';
import styles from './MatchupView.module.css';

/**
 * Head-to-head of two entities: their meanings and usage, and the verdict path between them.
 */
export function MatchupView({ selectedId, embedded = false }: { selectedId?: string; embedded?: boolean } = {}) {
  const routeX = useRouteText('/explore/matchup/:x/:y', 'x');
  const routeY = useRouteText('/explore/matchup/:x/:y', 'y');
  const [params, setParams] = useSearchParams();
  const x = selectedId ?? routeX;
  const y = embedded ? params.get('other') ?? '' : routeY;
  const nav = useNavigate();
  const { tenant, quoteId } = useAppStore();

  const queryX = params.get('x') ?? '';
  const queryY = params.get('y') ?? '';
  const [xInput, setXInput] = useState(x || queryX);
  const [yInput, setYInput] = useState(y || queryY);
  useEffect(() => {
    setXInput(x || queryX);
    setYInput(y || queryY);
  }, [x, y, queryX, queryY]);

  const [data, setData] = useState<Matchup | null>(null);
  const [responseField, setResponseField] = useState<ResponseField | null>(null);
  const [responseError, setResponseError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [gated, setGated] = useState(false);

  useEffect(() => {
    if (!x || !y) return;
    let stale = false;
    setLoading(true);
    setError(null);
    setData(null);
    setResponseField(null);
    setResponseError(null);
    setGated(false);

    const opts = { tenant, quoteId };
    exploreMatchup(x, y, opts)
      .then((m) => {
        if (stale) return;
        setData(m);
        exploreLexicon(x, y, opts)
          .then((field) => { if (!stale) setResponseField(field); })
          .catch((e) => { if (!stale) setResponseError(e instanceof Error ? e.message : String(e)); });
      })
      .catch((e) => {
        if (stale) return;
        if (e instanceof PaymentRequiredError) setGated(true);
        else setError(e instanceof Error ? e.message : String(e));
      })
      .finally(() => { if (!stale) setLoading(false); });

    return () => { stale = true; };
  }, [x, y, tenant, quoteId]);

  const submit = () => {
    if (!xInput.trim() || !yInput.trim()) return;
    if (embedded) {
      setParams(changeWorld(params, { other: yInput.trim() }), { preventScrollReset: true });
      return;
    }
    nav(`/explore/matchup/${encodeURIComponent(xInput.trim())}/${encodeURIComponent(yInput.trim())}`);
  };

  return (
    <div className={styles.page}>
      <form
        className={styles.picker}
        onSubmit={(e) => { e.preventDefault(); submit(); }}
      >
        <Input aria-label="first entity" value={xInput} placeholder="dog" readOnly={embedded} onChange={(e) => setXInput(e.target.value)} />
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
            <SideCard side={data.x} align="left" readings={responseField?.readings?.filter(row => row.side === 1)} readingError={responseError} />
            <div className={styles.center}>
              <span className={styles.vsBig}>vs</span>
              <Connections field={responseField} error={responseError} />
            </div>
            <SideCard side={data.y} align="right" readings={responseField?.readings?.filter(row => row.side === 2)} readingError={responseError} />
          </div>
          <SupportingRecords key={JSON.stringify([x, y, tenant, quoteId])} x={x} y={y} />
          <details><summary>Direct claim overlap</summary><Tape tape={data.tape} xLabel={data.x.label} yLabel={data.y.label} /></details>
        </>
      ) : null}
    </div>
  );
}

function SupportingRecords({ x, y }: { x: string; y: string }) {
  const { tenant, quoteId } = useAppStore();
  const [open, setOpen] = useState(false);
  const inspection = useReadResource({
    key: JSON.stringify(['comparison-inspection', x, y, tenant, quoteId]),
    enabled: open,
    read: (signal) => exploreResponseField(x, y, { tenant, quoteId, signal }),
  });
  return <details onToggle={(event) => setOpen(event.currentTarget.open)}>
    <summary>Inspect supporting records</summary>
    {open && <ResponseFieldView field={inspection.data ?? null} error={inspection.error?.message ?? null} />}
  </details>;
}

function SideCard({ side, align, readings, readingError }: { side: MatchupSide; align: 'left' | 'right'; readings?: LexiconReading[]; readingError: string | null }) {
  return (
    <div className={`${styles.side} ${align === 'right' ? styles.right : ''}`}>
      <RouterLink className={styles.sideName} to={`/explore/entity/${side.id}`}>{side.label}</RouterLink>
      <Readings rows={readings} error={readingError} />
    </div>
  );
}

function Readings({ rows, error }: { rows?: LexiconReading[]; error: string | null }) {
  const [count, setCount] = useState(6);
  if (error) return <ErrorText>{error}</ErrorText>;
  if (!rows) return <LoadingText>Reading meanings and usage…</LoadingText>;
  return <div className={styles.readings}>
    <h2>Meanings and usage</h2>
    {rows.length === 0 && <Muted>This comparison has not reached a source description or example.</Muted>}
    <ol>{rows.slice(0, count).map(row => <li key={row.text}>
      <p>{row.text}</p>
      <details><summary>Source evidence{row.witnesses != null && <> · {row.witnesses} {row.witnesses === 1 ? 'witness' : 'witnesses'}</>}</summary>
        <p>{row.via} — {row.relation}</p>
        {row.claim_ids.map((id, index) => <p key={id}><RouterLink to={`/explore/entity/${id}`}>Recorded claim {index + 1}</RouterLink></p>)}
      </details>
    </li>)}</ol>
    {rows.length > count && <Button onClick={() => setCount(count + 6)}>More meanings and usage ({rows.length - count})</Button>}
  </div>;
}

function Connections({ field, error }: { field: ResponseField | null; error: string | null }) {
  if (error) return <ErrorText>{error}</ErrorText>;
  if (!field) return <LoadingText>Reading their relationship…</LoadingText>;
  const connections = field.connections ?? [];
  const relations = [...new Set(connections.map(row => row.relation))];
  return <div className={styles.verdict}>
    {relations.length ? <><Muted>Witnessed relationships between readings</Muted>{relations.map(relation =>
      <span key={relation} className={styles.verdictText}>{relation}</span>)}
      <details><summary>Source evidence</summary>{connections.map(row => <p key={row.claim_id}>
        {row.from} — {row.relation} — {row.to}{' '}
        <RouterLink to={`/explore/entity/${row.claim_id}`}>Recorded claim{row.witnesses != null && <> · {row.witnesses} {row.witnesses === 1 ? 'witness' : 'witnesses'}</>}</RouterLink>
      </p>)}</details></> : <Muted>No relationship was realized from the evidence read.</Muted>}
  </div>;
}

function ResponseFieldView({ field, error }: { field: ResponseField | null; error: string | null }) {
  const [route, setRoute] = useState<ResponseFieldEntry['route']>('relation');
  const [page, setPage] = useState(0);
  if (error) return <Panel title="Response field"><ErrorText>{error}</ErrorText></Panel>;
  if (!field) return <Panel title="Response field"><LoadingText>Reading relations, containment and shape…</LoadingText></Panel>;
  const rows = field.rows.filter((row) => row.route === route);
  const pages = Math.max(1, Math.ceil(rows.length / 50));
  const current = Math.min(page, pages - 1);
  return (
    <Panel title="Response field">
      <Muted>{field.rows.length.toLocaleString()} responses across {field.rounds} rounds · fan {field.fanout} · {field.nearest} nearest shapes</Muted>
      <div className={styles.responseControls}>
        {(['relation', 'containment', 'shape'] as const).map((value) => (
          <Button key={value} aria-pressed={route === value} onClick={() => { setRoute(value); setPage(0); }}>
            {value === 'relation' ? 'Relations' : value === 'containment' ? 'Containment' : 'Shape'} ({field.rows.filter((row) => row.route === value).length})
          </Button>
        ))}
      </div>
      {route === 'containment' && <Muted>Open a container to inspect its ordered constituents, occurrences and trajectory.</Muted>}
      {route === 'shape' && <Muted>Geometric responses to the pair’s trajectory, ordered here by identity. Distance is shown separately from claim standing.</Muted>}
      <ol className={styles.responseList} start={current * 50 + 1}>
        {rows.slice(current * 50, (current + 1) * 50).map((row, index) => (
          <li key={`${row.round}:${row.occurrence}:${row.entity_id}:${row.claim_id}:${index}`} className={styles.responseRow}>
            <Muted>Round {row.round}</Muted>{' '}
            {row.route === 'shape' ? (
              <><RouterLink to={`/explore/entity/${row.entity_id}`}>{row.label}</RouterLink> · Fréchet {row.distance?.toFixed(5)}</>
            ) : (
              <>
                <RouterLink to={`/explore/entity/${row.anchor_id}`}>{row.anchor ?? row.anchor_id}</RouterLink>{' '}
                {row.route === 'containment' ? 'is contained in' : row.outbound ? '→' : '←'}{' '}
                {row.relation && <><span className={styles.factType}>{row.relation}</span>{' '}{row.outbound ? '→' : '←'}{' '}</>}
                <RouterLink to={`/explore/entity/${row.entity_id}`}>{row.label}</RouterLink>
              </>
            )}
            {row.claim_id && (
              <details>
                <summary>Claim standing{row.witnesses != null && <> · {row.witnesses} sources</>}{row.usage != null && <> · {row.usage} observations</>}</summary>
                <RouterLink to={`/explore/entity/${row.claim_id}`}>Recorded claim</RouterLink>{' '}
                · rating {row.rating?.toFixed(2)} · deviation {row.deviation?.toFixed(2)} · volatility {row.volatility?.toFixed(6)}
              </details>
            )}
          </li>
        ))}
      </ol>
      {rows.length === 0 && <Muted>No responses in this plane within the selected bounds.</Muted>}
      <div className={styles.responseControls}>
        <Button disabled={current === 0} onClick={() => setPage(current - 1)}>Previous</Button>
        <Muted>Page {current + 1} of {pages}</Muted>
        <Button disabled={current + 1 >= pages} onClick={() => setPage(current + 1)}>Next</Button>
      </div>
    </Panel>
  );
}

function Tape({ tape, xLabel, yLabel }: { tape: TapeRow[]; xLabel: string; yLabel: string }) {
  const both = tape.filter((t) => t.holder === 'both');
  const xOnly = tape.filter((t) => t.holder === 'x-only');
  const yOnly = tape.filter((t) => t.holder === 'y-only');
  return (
    <Panel title="Direct claim overlap">
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
