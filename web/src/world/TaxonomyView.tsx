import { Button, Muted, Panel, ReadStatus, useReadResource } from '@ui';
import { useAppStore } from '../store';
import { entityTaxonomy } from '../query/api';
import { EntityLink } from '../explore/components/EntityLink';

export function TaxonomyView({ id }: { id: string }) {
  const { tenant, quoteId } = useAppStore();
  const read = useReadResource({ key: JSON.stringify(['world-taxonomy', tenant, quoteId, id]),
    read: signal => entityTaxonomy(id, { tenant, quoteId, signal }) });
  return <Panel title="Classification" actions={<Button variant="ghost" onClick={() => void read.refresh()}>Refresh classification</Button>}>
    <ReadStatus label="Classification" resource={read} />
    {read.data && <>
      <h3>Broader classes</h3>{read.data.up.length ? <ul>{read.data.up.map(node => <li key={node.id}><EntityLink idHex={node.id} label={node.label} />{node.eff_mu != null && <Muted> · conservative standing {node.eff_mu}</Muted>}</li>)}</ul> : <Muted>No broader classes were returned.</Muted>}
      <h3>Members and subclasses</h3>{read.data.children.length ? <ul>{read.data.children.map(node => <li key={node.id}><EntityLink idHex={node.id} label={node.label} />{node.eff_mu != null && <Muted> · conservative standing {node.eff_mu}</Muted>}</li>)}</ul> : <Muted>No members or subclasses were returned.</Muted>}
    </>}
  </Panel>;
}
