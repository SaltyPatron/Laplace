import type { ClaimLeaderRow, QueryResult } from '../../query/types';
import type { BrowseHit } from './types';

export interface KnowledgeResult {
  key: string;
  text: string;
  kind: string;
  entityId?: string;
  topicId?: string;
  tier?: number;
  methods: string[];
  observations: { method: string; standing?: number | null; witnesses?: number | null }[];
  claim?: ClaimLeaderRow;
}

export interface KnowledgeAnswers extends QueryResult { rowKeys: string[] }

/** Compact presentation references keep answer text out of addresses and logs. */
export async function keyedAnswers(result: QueryResult): Promise<KnowledgeAnswers> {
  const rowKeys = await Promise.all(result.rows.map(async row => {
    const input = new TextEncoder().encode(JSON.stringify([result.topic_id, result.topic2_id, row.reply]));
    const digest = await crypto.subtle.digest('SHA-256', input);
    return 'answer:' + Array.from(new Uint8Array(digest), value => value.toString(16).padStart(2, '0')).join('');
  }));
  return { ...result, rowKeys };
}

/** Identical replies share a row; every method's measurements remain separate. */
export function collectKnowledge(hits: BrowseHit[], answers: KnowledgeAnswers[]): KnowledgeResult[] {
  const rows = new Map<string, KnowledgeResult>();
  for (const hit of hits) rows.set('entity:' + hit.id_hex, {
    key: 'entity:' + hit.id_hex, entityId: hit.id_hex, text: hit.label, kind: hit.type || 'entity', tier: hit.tier,
    methods: [hit.match_kind], observations: [{ method: hit.match_kind, standing: hit.eff_mu, witnesses: hit.witnesses }],
  });
  for (const result of answers) for (const [index, answer] of result.rows.entries()) {
    if (!answer.reply.trim()) continue;
    // Replies are text, not canonical entity IDs. A topic's identity must not
    // be presented as the identity of its answer.
    const key = result.rowKeys[index];
    const existing = rows.get(key);
    const observation = { method: result.shape, standing: answer.eff_mu, witnesses: answer.witnesses };
    if (existing) {
      if (!existing.methods.includes(result.shape)) existing.methods.push(result.shape);
      existing.observations.push(observation);
    } else rows.set(key, { key, text: answer.reply, kind: 'answer', topicId: result.topic_id ?? undefined,
      methods: [result.shape], observations: [observation] });
  }
  return [...rows.values()];
}
