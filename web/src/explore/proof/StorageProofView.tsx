import { useEffect, useMemo, useState, type CSSProperties, type FormEvent } from 'react';
import { useSearchParams } from 'react-router-dom';
import { ErrorText, LoadingText } from '@ui';

import { explorePreview, exploreStorageProof } from '../api';
import type {
  ExploreEntityPreviewResponse,
  StorageProofResponse,
} from '../types';
import { GlomeCanvas } from '../glome/GlomeCanvas';
import {
  STORAGE_PROOF_SURFACE_VERSION,
  compactId,
  depthOf,
  nodeToGlomeNode,
  packedToCarrierNode,
  realizedToGlomeNode,
  visibleText,
  type WalkDirection,
} from './proofGeometry';
import { BitPackingPanel, DistributionLab } from './ProofPanels';
import { AddressLedger, ProofIntro, ProofLaws, ProofSummary, SelectedAddressPanel } from './ProofSections';
import styles from './StorageProofView.module.css';

export { visibleText };

export function StorageProofView() {
  const [searchParams, setSearchParams] = useSearchParams();
  const query = searchParams.get('q') ?? '';
  const [draft, setDraft] = useState(query);
  const [proof, setProof] = useState<StorageProofResponse | null>(null);
  const [selectedOrdinal, setSelectedOrdinal] = useState<number | null>(null);
  const [selectedPacked, setSelectedPacked] = useState(0);
  const [direction, setDirection] = useState<WalkDirection>('trunk');
  const [preview, setPreview] = useState<ExploreEntityPreviewResponse | null>(null);
  const [previewBusy, setPreviewBusy] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [occupiedRanks, setOccupiedRanks] = useState(250_000);

  useEffect(() => {
    setDraft(query);
    if (!query.trim()) {
      setProof(null);
      setSelectedOrdinal(null);
      setError(null);
      return;
    }

    let cancelled = false;
    setBusy(true);
    setError(null);
    void exploreStorageProof(query)
      .then((result) => {
        if (cancelled) return;
        setProof(result);
        const emittedRoot = result.nodes.find((node) => node.id_hex === result.root_id_hex)
          ?? result.nodes.find((node) => node.ordinal === result.natural_unit_ordinal)
          ?? result.nodes.find((node) => node.tier > 0)
          ?? result.nodes[0]
          ?? null;
        setSelectedOrdinal(emittedRoot?.ordinal ?? null);
        setSelectedPacked(0);
        setOccupiedRanks((value) => Math.min(result.atom_window, value));
      })
      .catch((reason: unknown) => {
        if (cancelled) return;
        setProof(null);
        setError(reason instanceof Error ? reason.message : String(reason));
      })
      .finally(() => {
        if (!cancelled) setBusy(false);
      });

    return () => {
      cancelled = true;
    };
  }, [query]);

  const byOrdinal = useMemo(
    () => new Map((proof?.nodes ?? []).map((node) => [node.ordinal, node])),
    [proof],
  );
  const selected = useMemo(() => {
    if (!proof) return null;
    if (selectedOrdinal != null) {
      const exact = byOrdinal.get(selectedOrdinal);
      if (exact) return exact;
    }
    return proof.nodes.find((node) => node.id_hex === proof.root_id_hex)
      ?? proof.nodes.find((node) => node.ordinal === proof.natural_unit_ordinal)
      ?? proof.nodes.find((node) => node.tier > 0)
      ?? proof.nodes[0]
      ?? null;
  }, [proof, selectedOrdinal, byOrdinal]);

  useEffect(() => {
    if (!selected) {
      setPreview(null);
      return;
    }
    let cancelled = false;
    setPreviewBusy(true);
    setPreview(null);
    void explorePreview(selected.id_hex)
      .then((result) => {
        if (!cancelled) setPreview(result);
      })
      .catch(() => {
        if (!cancelled) setPreview(null);
      })
      .finally(() => {
        if (!cancelled) setPreviewBusy(false);
      });
    return () => {
      cancelled = true;
    };
  }, [selected?.id_hex]);

  const orderedNodes = useMemo(() => {
    if (!proof) return [];
    return proof.nodes.slice().sort((a, b) => {
      const da = depthOf(a, byOrdinal);
      const db = depthOf(b, byOrdinal);
      const depthOrder = direction === 'trunk' ? da - db : db - da;
      if (depthOrder !== 0) return depthOrder;
      if (a.text_offset !== b.text_offset) return a.text_offset - b.text_offset;
      return a.ordinal - b.ordinal;
    });
  }, [proof, byOrdinal, direction]);

  const placementNodes = useMemo(
    () => (proof?.nodes ?? []).map((node) => nodeToGlomeNode(
      node,
      node.ordinal === selectedOrdinal,
      node.parent_ordinal != null ? byOrdinal.get(node.parent_ordinal)?.id_hex : undefined,
    )),
    [proof, selectedOrdinal, byOrdinal],
  );
  const carrierNodes = useMemo(() => {
    const rows = selected?.packed_vertices ?? [];
    const maxOrd = rows.reduce((m, r) => Math.max(m, r.logical_ordinal), 1);
    return rows.map((row) => packedToCarrierNode(row, maxOrd));
  }, [selected]);
  const realizedNodes = useMemo(() => {
    if (!selected) return [];
    const rows = selected.realized_vertices ?? [];
    const maxOrd = rows.reduce((m, r) => Math.max(m, r.ordinal), 1);
    const parentNode = nodeToGlomeNode(selected, true);
    return [
      parentNode,
      ...rows.map((row) => realizedToGlomeNode(row, selected.id_hex, maxOrd)),
    ];
  }, [selected]);

  const selectedPackedRow = selected?.packed_vertices[selectedPacked] ?? null;

  function submit(event: FormEvent) {
    event.preventDefault();
    const value = draft.trim();
    if (!value) return;
    setSearchParams({ q: value });
  }

  return (
    <div
      className={styles.root}
      aria-label="Storage Proof workspace"
      data-storage-proof-surface={STORAGE_PROOF_SURFACE_VERSION}
      data-storage-proof-root={proof?.root_id_hex ?? ''}
      data-storage-proof-rom={proof?.perfcache_receipt_hex ?? ''}
    >
      <header className={styles.hero}>
        <div>
          <span className={styles.eyebrow}>Executable storage proof · T0 ROM {STORAGE_PROOF_SURFACE_VERSION}</span>
          <h2>Storage Proof · deterministic content structure in one bounded 4-D world</h2>
          <p>
            This surface executes the real decomposition/composition kernels and reports their
            calculated invariants: canonical Merkle convergence, S³ Tier-0 placement, bounded
            centroid closure, exact packed constituent manifests, realized trajectories and
            deterministic Hilbert locality. Derived views never replace canonical state.
          </p>
        </div>
        <form className={styles.promptForm} onSubmit={submit}>
          <label htmlFor="storage-proof-prompt">Prompt / surface</label>
          <textarea
            id="storage-proof-prompt"
            rows={3}
            value={draft}
            onChange={(event) => setDraft(event.target.value)}
            placeholder="Paste any text, or link here with ?q=..."
          />
          <div className={styles.formActions}>
            <span>Shareable: /proof?q=…</span>
            <button type="submit" disabled={!draft.trim() || busy}>
              {busy ? 'Computing…' : 'Prove storage'}
            </button>
          </div>
        </form>
      </header>

      {error ? <ErrorText>{error}</ErrorText> : null}
      {busy && !proof ? <LoadingText>Running native decomposition and storage packing…</LoadingText> : null}

      {!query.trim() && !busy ? (
        <ProofIntro />
      ) : null}

      {proof ? (
        <>
          <ProofSummary proof={proof} />

          <section className={styles.section}>
            <div className={styles.sectionHead}>
              <div>
                <h3>Executable invariants</h3>
                <p>These receipts are calculated from this prompt's real native output, not asserted by the browser.</p>
              </div>
            </div>
            <div className={styles.invariantGrid}>
              {proof.invariants.map((invariant) => (
                <article className={invariant.passed ? styles.invariantPass : styles.invariantFail} key={invariant.key}>
                  <span>{invariant.passed ? 'PASS' : 'FAIL'}</span>
                  <strong>{invariant.label}</strong>
                  <code>{invariant.observed}</code>
                </article>
              ))}
            </div>
          </section>

          <ProofLaws />

          <section className={styles.proofGrid}>
            <article className={styles.treePanel}>
              <div className={styles.panelHead}>
                <div>
                  <h3>O(Tier) composition walk</h3>
                  <p>Collapsed storage spine only; parser-only unary wrappers are omitted.</p>
                </div>
                <div className={styles.segmented}>
                  <button
                    type="button"
                    className={direction === 'trunk' ? styles.segmentActive : styles.segment}
                    onClick={() => setDirection('trunk')}
                  >
                    trunk → leaf
                  </button>
                  <button
                    type="button"
                    className={direction === 'leaf' ? styles.segmentActive : styles.segment}
                    onClick={() => setDirection('leaf')}
                  >
                    leaf → trunk
                  </button>
                </div>
              </div>
              <div className={styles.nodeList}>
                {orderedNodes.map((node) => {
                  const depth = depthOf(node, byOrdinal);
                  const isSelected = node.ordinal === selectedOrdinal;
                  return (
                    <button
                      type="button"
                      key={node.ordinal}
                      className={isSelected ? styles.nodeRowSelected : styles.nodeRow}
                      onClick={() => {
                        setSelectedOrdinal(node.ordinal);
                        setSelectedPacked(0);
                      }}
                    >
                      <span className={styles.depthMark} style={{ '--depth': depth } as CSSProperties} />
                      <span className={styles.tierBadge}>T{node.tier}</span>
                      <span className={styles.nodeLabel} title={visibleText(node.label)}>{visibleText(node.label) || '∅'}</span>
                      <code>{compactId(node.id_hex)}</code>
                      <span className={styles.radius}>r {node.radius.toFixed(3)}</span>
                    </button>
                  );
                })}
              </div>
            </article>

            <SelectedAddressPanel proof={proof} selected={selected} preview={preview} previewBusy={previewBusy} />
          </section>

          <AddressLedger
            orderedNodes={orderedNodes}
            selected={selected}
            onSelect={(ordinal) => {
              setSelectedOrdinal(ordinal);
              setSelectedPacked(0);
            }}
          />

          <section className={styles.section}>
            <div className={styles.sectionHead}>
              <div>
                <h3>Real 4-D placement</h3>
                <p>
                  Every emitted node from this prompt, projected with X–M and Z–M rotations.
                  Radius is the actual distance from the 4-D origin, so composed centroids stay interior.
                </p>
              </div>
            </div>
            <div className={styles.canvasWide}>
              <GlomeCanvas
                nodes={placementNodes}
                projection="placement"
                highlightIds={selected ? [selected.id_hex] : []}
                fill
                onSelectOrdinal={(ordinal) => {
                  if (ordinal != null && byOrdinal.has(ordinal)) {
                    setSelectedOrdinal(ordinal);
                    setSelectedPacked(0);
                  }
                }}
                note="Actual HashComposer coordinates. Click a point to inspect its exact address. Tier-0 atoms are on S³; composed entities are bounded centroids inside the 4-D ball."
              />
            </div>
          </section>

          <section className={styles.section}>
            <div className={styles.sectionHead}>
              <div>
                <h3>One composition, two non-interchangeable views</h3>
                <p>
                  Left: the exact packed identity carrier transformed into a 4-D bit-space display.
                  Right: the same ordered constituents at their real live coordinates.
                </p>
              </div>
            </div>

            {selected && selected.packed_vertices.length > 0 ? (
              <>
                <div className={styles.vertexPicker} aria-label="Packed vertex">
                  {selected.packed_vertices.map((vertex, index) => (
                    <button
                      type="button"
                      key={vertex.vertex}
                      className={selectedPacked === index ? styles.vertexActive : styles.vertex}
                      onClick={() => setSelectedPacked(index)}
                    >
                      v{vertex.vertex} · ord {vertex.logical_ordinal}
                      {vertex.run_length > 1 ? ` · ×${vertex.run_length}` : ''}
                    </button>
                  ))}
                </div>
                <div className={styles.glomePair}>
                  <article className={styles.visualCard}>
                    <header>
                      <strong>212-bit carrier inspection</strong>
                      <span>exact 53-bit payload × X/Y/Z/M</span>
                    </header>
                    <div className={styles.canvasTall}>
                      <GlomeCanvas
                        nodes={carrierNodes}
                        projection="carrier"
                        highlightOrdinal={selectedPackedRow?.logical_ordinal ?? null}
                        fill
                        note="Display-only inspection of the exact packed payload bits. The browser view is not canonical state; the decoded 212-bit carrier below is. M carries metadata (ordinal/run/flags), not spatial position."
                      />
                    </div>
                  </article>
                  <article className={styles.visualCard}>
                    <header>
                      <strong>Realized constituent curve</strong>
                      <span>Actual child PointZM coordinates</span>
                    </header>
                    <div className={styles.canvasTall}>
                      <GlomeCanvas
                        nodes={realizedNodes}
                        projection="placement"
                        highlightOrdinal={selectedPackedRow?.logical_ordinal ?? null}
                        fill
                        note="The spatial operand: child coordinates in logical order. This is the curve geometry metrics may use."
                      />
                    </div>
                  </article>
                </div>
                <BitPackingPanel row={selectedPackedRow} />
              </>
            ) : (
              <div className={styles.emptyPanel}>
                This node is a leaf or has no emitted constituent trajectory. Select a compositional node.
              </div>
            )}
          </section>

          <DistributionLab
            atomWindow={proof.atom_window}
            occupied={occupiedRanks}
            setOccupied={setOccupiedRanks}
          />
        </>
      ) : null}
    </div>
  );
}
