import { useMemo, useRef, useState } from 'react';

import { Link as RouterLink } from 'react-router-dom';

import {

  Button,

  Chip,

  IconButton,

  LookupRow,

  Muted,

  Table,

  Td,

  Th,

  Tooltip,

  TooltipContent,

  TooltipTrigger,

} from '@ui';

import { apiGet, ApiError, type EvidenceResponse } from '../api/client';

import { asNum, useAppStore } from '../store';

import { EntityLink } from '../explore/components/EntityLink';

import evidenceStyles from '../explore/components/EvidenceTable.module.css';

import styles from './ReceiptPanel.module.css';



const WIDTH_KEY = 'laplace.receiptPanelWidth';

const MIN_WIDTH = 300;

const DEFAULT_WIDTH = 380;



function formatMu(value: string | number | null | undefined): string | null {

  if (value === null || value === undefined) return null;

  const n = typeof value === 'number' ? value : Number(value);

  return Number.isFinite(n) ? n.toFixed(1) : null;

}



function readWidth(): number {

  const saved = localStorage.getItem(WIDTH_KEY);

  if (!saved) return DEFAULT_WIDTH;

  const n = parseInt(saved, 10);

  return Number.isFinite(n) ? Math.max(MIN_WIDTH, n) : DEFAULT_WIDTH;

}



function splitSources(label: string): string[] {

  return label.split(',').map((s) => s.trim()).filter(Boolean);

}



async function copyText(text: string) {

  try {

    await navigator.clipboard.writeText(text);

  } catch {

    /* clipboard unavailable */

  }

}



export function ReceiptPanel() {

  const tenant = useAppStore((s) => s.tenant);

  const [target, setTarget] = useState('');

  const [evidence, setEvidence] = useState<EvidenceResponse | null>(null);

  const [error, setError] = useState('');

  const [loading, setLoading] = useState(false);

  const [width, setWidth] = useState(readWidth);

  const widthRef = useRef(width);

  widthRef.current = width;



  const grouped = useMemo(() => {

    if (!evidence) return [];

    const map = new Map<string, EvidenceResponse['evidence']>();

    for (const item of evidence.evidence) {

      const key = item.type_label || 'claim';

      const bucket = map.get(key);

      if (bucket) bucket.push(item);

      else map.set(key, [item]);

    }

    return [...map.entries()].sort((a, b) => b[1].length - a[1].length);

  }, [evidence]);



  async function lookup() {

    const query = target.trim();

    if (!query) return;

    setLoading(true);

    setError('');

    setEvidence(null);

    try {

      setEvidence(await apiGet<EvidenceResponse>(

        `/v1/evidence/${encodeURIComponent(query)}?limit=40`, { tenant }));

    } catch (e) {

      setError(e instanceof ApiError ? e.message : 'Evidence lookup failed.');

    } finally {

      setLoading(false);

    }

  }



  function onResizeStart(e: React.MouseEvent) {

    e.preventDefault();

    const startX = e.clientX;

    const startW = widthRef.current;

    const maxW = Math.floor(window.innerWidth * 0.6);



    function onMove(ev: MouseEvent) {

      setWidth(Math.max(MIN_WIDTH, Math.min(maxW, startW + (startX - ev.clientX))));

    }

    function onUp() {

      localStorage.setItem(WIDTH_KEY, String(widthRef.current));

      window.removeEventListener('mousemove', onMove);

      window.removeEventListener('mouseup', onUp);

      delete document.body.dataset.receiptResizing;

    }

    document.body.dataset.receiptResizing = 'true';

    window.addEventListener('mousemove', onMove);

    window.addEventListener('mouseup', onUp);

  }



  return (

    <aside className={styles.panel} style={{ width }}>

      <div

        className={styles.resizeHandle}

        role="separator"

        aria-orientation="vertical"

        aria-label="Resize evidence panel"

        onMouseDown={onResizeStart}

      />

      <div className={styles.body}>

        <h2>Evidence</h2>

        <Muted>Grouped provenance receipts — open objects in Explore or copy ids.</Muted>

        <LookupRow

          value={target}

          onChange={setTarget}

          onSubmit={() => void lookup()}

          placeholder="word or entity id"

          submitLabel={loading ? '…' : 'Look up'}

          disabled={loading}

          error={error || null}

        />

        {evidence && (

          <div className={evidenceStyles.evidence}>

            <header className={evidenceStyles.header}>

              <div>

                <h3>

                  <EntityLink idHex={evidence.entity_id} label={evidence.entity_label} />

                </h3>

                <Tooltip>

                  <TooltipTrigger asChild>

                    <code className={evidenceStyles.entityId}>{evidence.entity_id}</code>

                  </TooltipTrigger>

                  <TooltipContent>{evidence.entity_id}</TooltipContent>

                </Tooltip>

              </div>

              <div className={evidenceStyles.headerActions}>

                <Button variant="ghost" size="sm" asChild>

                  <RouterLink to={`/explore/entity/${evidence.entity_id}`}>Explore</RouterLink>

                </Button>

                <Button

                  type="button"

                  variant="ghost"

                  size="sm"

                  onClick={() => void copyText(evidence.entity_id)}

                >

                  Copy id

                </Button>

              </div>

            </header>



            {evidence.evidence.length === 0 && (

              <Muted>No provenance-backed claims yet.</Muted>

            )}



            {grouped.map(([relation, items]) => (

              <section key={relation} className={evidenceStyles.group}>

                <h4 className={evidenceStyles.groupTitle}>

                  {relation}

                  <span className={evidenceStyles.groupCount}>{items.length}</span>

                </h4>

                <Table className={evidenceStyles.table}>

                  <thead>

                    <tr>

                      <Th>Object</Th>

                      <Th>μ</Th>

                      <Th>Wit</Th>

                      <Th>Sources</Th>

                      <Th aria-label="Actions" />

                    </tr>

                  </thead>

                  <tbody>

                    {items.map((item) => {

                      const mu = formatMu(item.eff_mu);

                      const witnesses = asNum(item.observation_count);

                      const sources = splitSources(item.source_label);

                      return (

                        <tr key={`${item.type_id}-${item.object_id}`}>

                          <Td className={evidenceStyles.object}>

                            <EntityLink idHex={item.object_id} label={item.object_label} />

                          </Td>

                          <Td className={evidenceStyles.num}>{mu ?? '—'}</Td>

                          <Td className={evidenceStyles.num}>{witnesses > 0 ? witnesses : '—'}</Td>

                          <Td className={evidenceStyles.sources}>

                            {sources.length > 0

                              ? sources.map((s) => (

                                  <Chip key={s} variant="source">{s}</Chip>

                                ))

                              : '—'}

                          </Td>

                          <Td className={evidenceStyles.actions}>

                            <Tooltip>

                              <TooltipTrigger asChild>

                                <IconButton

                                  type="button"

                                  variant="ghost"

                                  aria-label={`Copy ${item.object_id}`}

                                  onClick={() => void copyText(item.object_id)}

                                >

                                  ⧉

                                </IconButton>

                              </TooltipTrigger>

                              <TooltipContent>Copy {item.object_id}</TooltipContent>

                            </Tooltip>

                          </Td>

                        </tr>

                      );

                    })}

                  </tbody>

                </Table>

              </section>

            ))}

          </div>

        )}

      </div>

    </aside>

  );

}

