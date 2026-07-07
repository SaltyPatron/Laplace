import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { LoadingText, Muted, Panel, Stack } from '@ui';
import { exploreCatalog } from '../api';
import { useExploreStore } from '../store';
import type { ExploreStageRow } from '../types';

export function StageBrowse() {
  const { stageId } = useParams();
  const setBreadcrumb = useExploreStore((s) => s.setBreadcrumb);
  const [stage, setStage] = useState<ExploreStageRow | null>(null);

  useEffect(() => {
    const id = decodeURIComponent(stageId ?? '');
    exploreCatalog().then((c) => {
      const hit = c.stages.find((s) => s.stage === id) ?? null;
      setStage(hit);
      if (hit) setBreadcrumb({ stage: hit.stage });
    });
  }, [stageId, setBreadcrumb]);

  if (!stage) return <LoadingText>Loading stage…</LoadingText>;

  return (
    <Stack gap={4}>
      <Panel title={stage.stage}>
        {stage.law ? <Muted>{stage.law}</Muted> : null}
        <ul style={{ listStyle: 'none', margin: 0, padding: 0, display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
          {stage.sources.map((s) => (
            <li key={s.cli}>
              <Link to={`/explore/source/${encodeURIComponent(s.cli)}`}>{s.cli}</Link>
              <br />
              <Muted>{s.layer} — {s.role ?? s.links}</Muted>
            </li>
          ))}
        </ul>
      </Panel>
    </Stack>
  );
}
