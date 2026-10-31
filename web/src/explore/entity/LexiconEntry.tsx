import { useState } from 'react';
import { Link } from 'react-router-dom';
import { Button, ErrorText, LoadingText, Muted, Panel, Stack } from '@ui';
import type { ResponseField } from '../../query/types';
import { EntityLink } from '../components/EntityLink';
import styles from './EntityDetail.module.css';

export function LexiconEntry({ field, error, onRetry }: {
  field: ResponseField | null;
  error: string | null;
  onRetry: () => void;
}) {
  const [limit, setLimit] = useState(6);
  const [relatedLimit, setRelatedLimit] = useState(8);
  if (error) return <Panel title="Meanings and usage"><ErrorText>{error}</ErrorText><Button onClick={onRetry}>Retry</Button></Panel>;
  if (!field) return <Panel title="Meanings and usage"><LoadingText>Reading recorded meanings and usage…</LoadingText></Panel>;
  const readings = field.readings ?? [];
  const relationships = field.connections ?? [];
  return <Stack gap={4} className={styles.lexiconEntry}>
    <Panel title="Meanings and usage">
      {readings.length === 0 ? <Muted>No recorded description or usage example was found for this entry.</Muted> :
        <ol className={styles.meanings}>{readings.slice(0, limit).map(reading => <li key={reading.text}>
          <p>{reading.text}</p>
          <details className={styles.sourceEvidence}>
            <summary>View source evidence</summary>
            {reading.witnesses != null && <p>{reading.witnesses} {reading.witnesses === 1 ? 'source asserts' : 'sources assert'} this reading.</p>}
            <ul>{reading.claim_ids.map((id, index) => <li key={id}><Link to={`/explore/entity/${id}`}>Recorded claim {index + 1}</Link></li>)}</ul>
          </details>
        </li>)}</ol>}
      {readings.length > limit && <Button variant="ghost" onClick={() => setLimit(limit + 6)}>More meanings and usage ({readings.length - limit})</Button>}
    </Panel>
    {relationships.length > 0 && <Panel title="Related information">
      <ul className={styles.readableRelations}>{relationships.slice(0, relatedLimit).map(relationship => {
        return <li key={relationship.claim_id}>
          <p>{relationship.from_id ? <EntityLink idHex={relationship.from_id} label={relationship.from} /> : relationship.from}{' '}
            <span className={styles.relationLabel}>{relationship.relation}</span>{' '}
            {relationship.to_id ? <EntityLink idHex={relationship.to_id} label={relationship.to} /> : relationship.to}</p>
          <details className={styles.sourceEvidence}><summary>View source evidence</summary>
            <Link to={`/explore/entity/${relationship.claim_id}`}>Recorded claim</Link>
          </details>
        </li>;
      })}</ul>
      {relationships.length > relatedLimit && <Button variant="ghost" onClick={() => setRelatedLimit(relatedLimit + 8)}>More related information ({relationships.length - relatedLimit})</Button>}
    </Panel>}
  </Stack>;
}
