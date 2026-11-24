import { useState } from 'react';
import { Badge, Muted, Tab, TabList } from '@ui';
import { BlindMatchupWorkspace } from './BlindMatchupWorkspace';
import { CombineWorkspace } from './CombineWorkspace';
import { GraphleWorkspace } from './GraphleWorkspace';
import { HighwayRaceWorkspace } from './HighwayRaceWorkspace';
import { ProvenanceDetectiveWorkspace } from './ProvenanceDetectiveWorkspace';
import styles from './ArenaView.module.css';

type ArenaTab = 'combine' | 'highway_race' | 'graphle' | 'provenance_detective' | 'blind_matchup';

const ARENA_TABS: { id: ArenaTab; label: string; summary: string }[] = [
  { id: 'combine', label: 'Craft (COMBINE)', summary: 'Constructive discovery, Merkle pairing, 4D midpoint, and witnessed bridge strands' },
  { id: 'highway_race', label: 'Highway Race', summary: 'Live hop-by-hop traversal from Start to Target across attested mesh neighbors' },
  { id: 'graphle', label: 'Graphle', summary: 'Spoiler-free deduction from tier, 64-bit highway mask, and 4D quadrant clues' },
  { id: 'provenance_detective', label: 'Provenance Detective', summary: 'Identify the true highest-standing witness behind an attested claim' },
  { id: 'blind_matchup', label: 'Blind Matchup', summary: 'Anonymized head-to-head of two Laplace firmware profiles with OODA reveal' },
];

export function ArenaView() {
  const [tab, setTab] = useState<ArenaTab>('combine');
  const activeTab = ARENA_TABS.find((t) => t.id === tab) ?? ARENA_TABS[0];

  return (
    <div className={styles.page}>
      <header className={styles.hero}>
        <div>
          <h2>Knowledge Arena &amp; Constructive Discovery</h2>
          <Muted>{activeTab.summary}</Muted>
        </div>
        <Badge>Live substrate · 4D geometry · Fisher consensus</Badge>
      </header>

      <TabList className={styles.tabList} aria-label="Knowledge Arena modes">
        {ARENA_TABS.map((item) => (
          <Tab
            key={item.id}
            id={`arena-tab-${item.id}`}
            aria-controls={`arena-panel-${item.id}`}
            selected={tab === item.id}
            className={`${styles.tabButton} ${tab === item.id ? styles.tabActive : ''}`}
            onClick={() => setTab(item.id)}
          >
            {item.label}
          </Tab>
        ))}
      </TabList>

      <div id={`arena-panel-${tab}`} role="tabpanel" aria-labelledby={`arena-tab-${tab}`} className={styles.panelWrap}>
        {tab === 'combine' && <CombineWorkspace />}
        {tab === 'highway_race' && <HighwayRaceWorkspace />}
        {tab === 'graphle' && <GraphleWorkspace />}
        {tab === 'provenance_detective' && <ProvenanceDetectiveWorkspace />}
        {tab === 'blind_matchup' && <BlindMatchupWorkspace />}
      </div>
    </div>
  );
}
