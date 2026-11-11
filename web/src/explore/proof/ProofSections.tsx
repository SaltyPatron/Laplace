/** Static and read-only sections of the storage proof: summary strip, law cards, selected address panel and address ledger. */
import { Link } from 'react-router-dom';
import { Muted, Table } from '@ui';

import type { ExploreEntityPreviewResponse, StorageProofNodeRow, StorageProofResponse } from '../types';
import { STORAGE_PROOF_SURFACE_VERSION, compactId, prettyNumber, visibleText } from './proofGeometry';
import styles from './StorageProofView.module.css';

export function ProofSummary({ proof }: { proof: StorageProofResponse }) {
  const maxTier = Math.max(...proof.nodes.map((node) => node.tier), 0);
  const tier0Count = proof.nodes.filter((node) => node.tier === 0).length;
  const emittedRoot = proof.nodes.find((node) => node.id_hex === proof.root_id_hex) ?? null;
  const rootPackedVertices = emittedRoot?.packed_vertices.length ?? 0;
  const rootRealizedVertices = emittedRoot?.realized_vertices.length ?? 0;
  return (
    <section className={styles.summaryStrip}>
      <div><span>Root</span><code title={proof.root_id_hex}>{compactId(proof.root_id_hex)}</code></div>
      <div><span>Emitted nodes</span><strong>{proof.nodes.length.toLocaleString()}</strong></div>
      <div><span>Max tier</span><strong>{maxTier}</strong></div>
      <div><span>Tier-0 leaves</span><strong>{tier0Count.toLocaleString()}</strong></div>
      <div><span>Unicode window</span><strong>{proof.atom_window.toLocaleString()}</strong></div>
      <div><span>Proof surface</span><strong>{STORAGE_PROOF_SURFACE_VERSION}</strong></div>
      <div>
        <span>T0 ROM receipt</span>
        <code title={proof.perfcache_receipt_hex}>{compactId(proof.perfcache_receipt_hex)}</code>
      </div>
      <div>
        <span>DB mmap = app ROM</span>
        <strong>
          {proof.perfcache_aligned == null ? 'UNVERIFIED' : proof.perfcache_aligned ? 'YES' : 'MISMATCH'}
        </strong>
      </div>
      <div>
        <span>Root emitted</span>
        <strong>{emittedRoot ? 'YES' : 'NO'}</strong>
      </div>
      <div>
        <span>Root packed vertices</span>
        <strong>{rootPackedVertices.toLocaleString()}</strong>
      </div>
      <div>
        <span>Root realized vertices</span>
        <strong>{rootRealizedVertices.toLocaleString()}</strong>
      </div>
    </section>
  );
}

export function ProofIntro() {
  return (
    <section className={styles.emptyIntro}>
      <h3>What this page proves</h3>
      <p>
        The endpoint runs the same TextDecomposer, HashComposer and flagged-RLE trajectory
        builder used by ingestion. It does not invent browser-only coordinates. Enter text
        above, or navigate here with a URL-encoded <code>q</code> parameter.
      </p>
    </section>
  );
}

export function ProofLaws() {
  return (
    <section className={styles.section}>
      <div className={styles.sectionHead}>
        <div>
          <h3>What the receipts mean</h3>
          <p>
            A fixed bounded geometric domain carries a countably open Tier-0 address law and
            recursively composed observed content. Identity, physicality, locality, packed
            structure and realized trajectory are deterministic but non-interchangeable state.
          </p>
        </div>
      </div>
      <div className={styles.lawGrid}>
        <article className={styles.lawCard}>
          <span className={styles.lawStep}>01 · open Tier-0 law · finite Unicode ROM</span>
          <strong>Tier 0 has no terminal rank; Unicode is one finite observed window</strong>
          <p>
            The current v4 ROM materializes the selected Unicode generation: every
            codepoint&apos;s content ID, DUCET/UCA order, 4-D S³ coordinate, 128-bit Hilbert
            key, and segmentation properties. The underlying rank/address law is open
            (`n ∈ ℕ`); 1,114,112 is this Unicode window, not Tier-0 capacity. DUCET order
            feeds the open Super-Fibonacci map, so extending a realized prefix densifies
            the same bounded shell instead of filling one latitude band or requiring a
            final population N.
          </p>
          <code>codepoint → {'{'} id, uca_order, x, y, z, m, hilbert, flags {'}'}</code>
        </article>

        <article className={styles.lawCard}>
          <span className={styles.lawStep}>02 · recursive identity</span>
          <strong>Ordered children determine content identity</strong>
          <p>
            A single-child wrapper collapses to the child identity. A multi-child node is
            the Merkle-domain BLAKE3 content address of the ordered child-ID sequence.
            Tier, source, ordinal, worker, and container are not identity salt.
          </p>
          <code>n = 1 → id(child)</code>
          <code>n &gt; 1 → BLAKE3(domain ∥ child₁.id ∥ … ∥ childₙ.id)[0..127]</code>
        </article>

        <article className={styles.lawCard}>
          <span className={styles.lawStep}>03 · bounded placement</span>
          <strong>Every composition stays inside one finite 4-D ball</strong>
          <p>
            HashComposer places a parent at the Euclidean centroid of its immediate child
            coordinates. The closed unit 4-ball is convex, so finite recursive composition
            cannot escape it. Hilbert encodes that resulting 4-D point for locality.
          </p>
          <code>coord(parent) = (Σ childᵢ.coord) / n</code>
          <code>hilbert(parent) = Hilbert4D(coord(parent))</code>
        </article>

        <article className={styles.lawCard}>
          <span className={styles.lawStep}>04 · exact sequence manifest</span>
          <strong>Same content converges; order and identity survive composition</strong>
          <p>
            Equal canonical content converges on the same Merkle identity and subtree.
            The physicality trajectory stores each child identity plus logical position,
            run length, tier/atom metadata, and flags in four 53-bit float payload slots.
            Repetition reuses canonical structure and RLE compresses repeated constituents
            without changing the logical expanded sequence.
          </p>
          <code>vertex = pack(child.id, ordinal, run_length, flags) → 4 × 53 bits</code>
        </article>

        <article className={styles.lawCard}>
          <span className={styles.lawStep}>05 · deterministic derived geometry</span>
          <strong>Spatial telemetry is calculated from canonical child physicalities</strong>
          <p>
            When a geometric operation is requested, Laplace resolves the stored child IDs
            through their canonical physicalities and orders those exact coordinates by
            ordinal. The resulting curve is a deterministic derived view; canonical identity
            and the packed manifest remain intact. Geometry never replaces them.
          </p>
          <code>manifest child IDs → live PointZM → ordered realized curve</code>
        </article>

        <article className={styles.lawCard}>
          <span className={styles.lawStep}>06 · central content world</span>
          <strong>Complexity and telemetry accumulate without enlarging the geometric boundary</strong>
          <p>
            Every observed composition becomes another canonical addressable structure whose
            physicality and trajectory can participate in higher compositions, occurrences,
            relations and evidence. Distinct structures may share a centroid while their
            identities and trajectories remain distinct; the fixed 4-D boundary does not
            become the identity store.
          </p>
          <code>finite basis → finite nodes → finite parents → …</code>
        </article>
      </div>
    </section>
  );
}

export function SelectedAddressPanel({
  proof,
  selected,
  preview,
  previewBusy,
}: {
  proof: StorageProofResponse;
  selected: StorageProofNodeRow | null;
  preview: ExploreEntityPreviewResponse | null;
  previewBusy: boolean;
}) {
  return (
    <article className={styles.detailPanel}>
      <div className={styles.panelHead}>
        <div>
          <h3>Selected storage address</h3>
          <p>Identity, 4-D placement and 1-D Hilbert locality are distinct fields.</p>
        </div>
        {selected ? <span className={styles.tierBadge}>T{selected.tier}</span> : null}
      </div>

      {selected ? (
        <>
          <div className={styles.addressGrid}>
            <div><span>Entity ID</span><code title={selected.id_hex}>{selected.id_hex}</code></div>
            <div><span>Hilbert 128</span><code title={selected.hilbert_hex}>{selected.hilbert_hex}</code></div>
            <div><span>X</span><code>{prettyNumber(selected.x, 10)}</code></div>
            <div><span>Y</span><code>{prettyNumber(selected.y, 10)}</code></div>
            <div><span>Z</span><code>{prettyNumber(selected.z, 10)}</code></div>
            <div><span>M</span><code>{prettyNumber(selected.m, 10)}</code></div>
            <div><span>4-D radius</span><code>{prettyNumber(selected.radius, 10)}</code></div>
            <div><span>Span</span><code>{selected.text_offset}+{selected.text_length} bytes</code></div>
            {selected.atom != null ? (
              <div><span>Codepoint atom</span><code>U+{selected.atom.toString(16).toUpperCase().padStart(4, '0')}</code></div>
            ) : null}
            {selected.ducet_rank != null ? (
              <div><span>Recovered DUCET rank</span><code>{selected.ducet_rank.toLocaleString()}</code></div>
            ) : null}
          </div>

          <div className={styles.dbWitness}>
            <div>
              <strong>PostgreSQL witness</strong>
              {previewBusy ? <span>checking…</span> : preview ? (
                <span>{preview.exists ? 'stored' : 'computed only'} · evidence {preview.evidence_count.toLocaleString()}</span>
              ) : <span>unavailable</span>}
            </div>
            {preview?.exists ? (
              <Link to={`/explore/entity/${selected.id_hex}`}>open persisted entity →</Link>
            ) : null}
          </div>
          <div className={styles.romWitness}>
            <div>
              <span>App mmap</span>
              <code title={proof.perfcache_receipt_hex}>{proof.perfcache_receipt_hex}</code>
            </div>
            <div>
              <span>PostgreSQL mmap</span>
              <code title={proof.database_perfcache_receipt_hex ?? ''}>
                {proof.database_perfcache_receipt_hex ?? 'unavailable'}
              </code>
            </div>
            <strong className={
              proof.perfcache_aligned == null
                ? styles.romUnverified
                : proof.perfcache_aligned
                  ? styles.romMatch
                  : styles.romMismatch
            }>
              {proof.perfcache_aligned == null
                ? 'UNVERIFIED — database ROM receipt unavailable'
                : proof.perfcache_aligned
                  ? 'same exact T0 ROM'
                  : 'T0 ROM MISMATCH'}
            </strong>
          </div>
          {proof.database_perfcache_error ? (
            <div className={styles.romError}>
              <strong>Database verification failed</strong>
              <code>{proof.database_perfcache_error}</code>
            </div>
          ) : null}
        </>
      ) : (
        <Muted>Select a node from the composition walk.</Muted>
      )}
    </article>
  );
}

export function AddressLedger({
  orderedNodes,
  selected,
  onSelect,
}: {
  orderedNodes: StorageProofNodeRow[];
  selected: StorageProofNodeRow | null;
  onSelect: (ordinal: number) => void;
}) {
  return (
    <section className={styles.section}>
      <div className={styles.sectionHead}>
        <div>
          <h3>Finite address ledger</h3>
          <p>
            Every emitted node in this observation, with its exact recursive parent,
            content address, 4-D coordinate and Hilbert locality key. Select any row to
            drive the detailed address, packed carrier and realized-curve views.
          </p>
        </div>
      </div>
      <div className={styles.ledgerWrap}>
        <Table className={styles.ledger}>
          <thead>
            <tr>
              <th>ord</th>
              <th>tier</th>
              <th>surface</th>
              <th>parent</th>
              <th>content id</th>
              <th>X</th>
              <th>Y</th>
              <th>Z</th>
              <th>M</th>
              <th>r₄</th>
              <th>Hilbert128</th>
              <th>T0 / DUCET</th>
            </tr>
          </thead>
          <tbody>
            {orderedNodes.map((node) => (
              <tr
                key={`ledger-${node.ordinal}`}
                className={node.ordinal === selected?.ordinal ? styles.ledgerSelected : undefined}
                onClick={() => onSelect(node.ordinal)}
              >
                <td>{node.ordinal}</td>
                <td>T{node.tier}</td>
                <td title={visibleText(node.label)}>{visibleText(node.label) || '∅'}</td>
                <td>{node.parent_ordinal ?? '—'}</td>
                <td><code title={node.id_hex}>{compactId(node.id_hex)}</code></td>
                <td><code>{prettyNumber(node.x, 7)}</code></td>
                <td><code>{prettyNumber(node.y, 7)}</code></td>
                <td><code>{prettyNumber(node.z, 7)}</code></td>
                <td><code>{prettyNumber(node.m, 7)}</code></td>
                <td><code>{prettyNumber(node.radius, 7)}</code></td>
                <td><code title={node.hilbert_hex}>{compactId(node.hilbert_hex)}</code></td>
                <td>
                  {node.atom == null
                    ? '—'
                    : `U+${node.atom.toString(16).toUpperCase().padStart(4, '0')} · #${node.ducet_rank?.toLocaleString() ?? '?'}`}
                </td>
              </tr>
            ))}
          </tbody>
        </Table>
      </div>
    </section>
  );
}
