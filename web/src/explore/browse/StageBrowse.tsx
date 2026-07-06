import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
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

  if (!stage) return <p className="muted">Loading stage…</p>;

  return (
    <div>
      <h2>{stage.stage}</h2>
      {stage.law ? <p className="muted">{stage.law}</p> : null}
      <ul className="source-list">
        {stage.sources.map((s) => (
          <li key={s.cli}>
            <Link to={`/explore/source/${encodeURIComponent(s.cli)}`}>{s.cli}</Link>
            <span className="muted">{s.layer} — {s.role ?? s.links}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}
