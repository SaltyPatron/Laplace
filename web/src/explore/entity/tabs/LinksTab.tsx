import { ConsensusBadge, Muted, Panel, Stack } from '@ui';
import { EntityLink } from '../../components/EntityLink';
import { GatePrompt } from '../../components/GatePrompt';
import styles from '../EntityDetail.module.css';

export function LinksTab({
  linksUnlocked,
  members,
  peers,
  onLoadLinks,
}: {
  linksUnlocked: boolean;
  members: Awaited<ReturnType<typeof import('../../api').exploreMembers>>['members'] | null;
  peers: Awaited<ReturnType<typeof import('../../api').explorePeers>>['peers'] | null;
  onLoadLinks: () => void;
}) {
  if (!linksUnlocked || !members || !peers) {
    return (
      <GatePrompt
        serviceId="visualization.deep_export"
        label="Expand cross-links — members and semantic peers (billable fan-out)."
        onReady={onLoadLinks}
      />
    );
  }

  return (
    <Stack gap={4}>
      <Panel title="Members">
        {members.members.length === 0 ? (
          <Muted>None</Muted>
        ) : (
          <ul className={styles.list}>
            {members.members.map((m) => (
              <li key={m.member_id_hex}>
                <EntityLink idHex={m.member_id_hex} label={m.member_label} /> ({m.kind}){' '}
                <ConsensusBadge mu={m.eff_mu} witnesses={m.witnesses} tone="explore" />
              </li>
            ))}
          </ul>
        )}
      </Panel>

      <Panel title="Peers">
        {peers.peers.length === 0 ? (
          <Muted>None</Muted>
        ) : (
          <ul className={styles.list}>
            {peers.peers.map((p) => (
              <li key={`${p.peer}-${p.kind}`}>
                {p.peer} ({p.kind}) — {p.strength.toFixed(2)}
              </li>
            ))}
          </ul>
        )}
      </Panel>
    </Stack>
  );
}
