import { useMemo } from 'react';
import { Chip, IconButton, Muted, Table, Td, Th, Tooltip, TooltipContent, TooltipTrigger } from '@ui';
import { EntityLink } from '../../components/EntityLink';
import type { ExploreEntityResponse, LabeledEvidenceItem } from '../../types';
import evidenceStyles from '../../components/EvidenceTable.module.css';

function formatMu(value: number | null | undefined): string | null {
  if (value === null || value === undefined) return null;
  return Number.isFinite(value) ? value.toFixed(1) : null;
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

function groupEvidence(items: LabeledEvidenceItem[]) {
  const map = new Map<string, LabeledEvidenceItem[]>();
  for (const item of items) {
    const key = item.type_label || 'claim';
    const bucket = map.get(key);
    if (bucket) bucket.push(item);
    else map.set(key, [item]);
  }
  return [...map.entries()].sort((a, b) => b[1].length - a[1].length);
}

export function ProvenanceTab({ entity }: { entity: ExploreEntityResponse }) {
  const grouped = useMemo(() => groupEvidence(entity.evidence), [entity.evidence]);

  if (entity.evidence.length === 0) {
    return <Muted>No provenance-backed claims recorded.</Muted>;
  }

  return (
    <div className={evidenceStyles.evidence}>
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
                const witnesses = item.observation_count;
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
  );
}
