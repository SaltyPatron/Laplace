import { Link as RouterLink } from 'react-router-dom';
import { Chip, Muted, Table, TableScroll, Td, Th } from '@ui';
import { SHARE_BP_TOTAL } from '../api/forwardProof';
import type { ArenaWitnessShareView } from './api';
import styles from './ArenaView.module.css';

export function todaySeed(): string {
  return new Date().toISOString().slice(0, 10).replace(/-/g, '');
}

export function EntityIdLink({ idHex, label }: { idHex: string; label?: string | null }) {
  const clean = idHex.toLowerCase();
  const isHex = /^[0-9a-f]{32}$/.test(clean);
  if (!isHex) {
    return <code>{label || clean}</code>;
  }
  return (
    <RouterLink className={styles.entityLink} to={`/explore/entity/${clean}`} title={clean}>
      {label ? <span>{label}</span> : null}
      <code className={styles.hex}>{clean}</code>
    </RouterLink>
  );
}

export function ShareBreakdown({ shares, totalBp }: { shares: ArenaWitnessShareView[]; totalBp: number }) {
  if (shares.length === 0) {
    return <Muted>No witness share attribution recorded for this result.</Muted>;
  }
  const conserved = totalBp === SHARE_BP_TOTAL;
  return (
    <div className={styles.stack}>
      <div className={styles.rowHeader}>
        <strong>Witness Attribution &amp; Fisher share_bp</strong>
        <Chip variant={conserved ? 'engineOk' : 'engineMissing'}>
          Σ {totalBp} / {SHARE_BP_TOTAL} bp {conserved ? '(conserved)' : '(partial)'}
        </Chip>
      </div>
      <TableScroll>
        <Table>
          <thead>
            <tr>
              <Th scope="col">Witness ID</Th>
              <Th scope="col">Label</Th>
              <Th scope="col">Trust</Th>
              <Th scope="col">Fisher I(e)</Th>
              <Th scope="col">Games</Th>
              <Th scope="col">share_bp</Th>
              <Th scope="col">Share %</Th>
            </tr>
          </thead>
          <tbody>
            {shares.map((s) => (
              <tr key={`${s.witness_id}:${s.label}`}>
                <Td><EntityIdLink idHex={s.witness_id} /></Td>
                <Td>{s.label}</Td>
                <Td className={styles.num}>{s.trust.toFixed(3)}</Td>
                <Td className={styles.num}>{s.fisher_info.toFixed(2)}</Td>
                <Td className={styles.num}>{s.games.toLocaleString()}</Td>
                <Td className={styles.num}><code>{s.share_bp}</code></Td>
                <Td className={styles.num}>{((s.share_bp / SHARE_BP_TOTAL) * 100).toFixed(2)}%</Td>
              </tr>
            ))}
          </tbody>
        </Table>
      </TableScroll>
    </div>
  );
}
