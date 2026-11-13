/**
 * The three honest outcomes of a chat turn (entity contract, "Answer states"). Never conflated:
 * an answer, an abstention with its reason, or a failure that names the stage it stopped at.
 */
import type { ForwardPassProof } from '../api/forwardProof';

export type TurnOutcome =
  | { state: 'pending' }
  | { state: 'answered' }
  | { state: 'abstained'; reason: string }
  | { state: 'failed'; stage: string | null; message: string };

const STAGES = ['resolve', 'couple', 'orient', 'route', 'scan', 'compose', 'propose', 'steer', 'select', 'realize', 'witness'];

/** The last pipeline stage the Engine's trace reached, in pipeline order: each stage line begins with its name. */
export function lastStage(proof: ForwardPassProof | undefined): string | null {
  let best = -1;
  for (const line of (proof?.trace ?? '').split(/\r?\n/)) {
    const word = /^([A-Z]+)\b/.exec(line)?.[1]?.toLowerCase();
    if (word) best = Math.max(best, STAGES.indexOf(word));
  }
  return best >= 0 ? STAGES[best] : null;
}

export function classifyTurn(turn: { content: string; streaming?: boolean; error?: string; forwardProof?: ForwardPassProof }): TurnOutcome {
  if (turn.streaming) return { state: 'pending' };
  if (turn.error) return { state: 'failed', stage: null, message: turn.error };
  if (turn.content.trim()) return { state: 'answered' };
  const proof = turn.forwardProof;
  if (proof && /abstain/i.test(proof.disposition)) return { state: 'abstained', reason: proof.disposition };
  if (proof) {
    const stage = lastStage(proof);
    return { state: 'failed', stage, message: stage
      ? `Reasoning stopped after the ${stage} step without producing an answer (${proof.disposition || 'unresolved'}).`
      : `Reasoning produced no answer (${proof.disposition || 'unresolved'}).` };
  }
  return { state: 'failed', stage: null, message: 'No answer came back for this question.' };
}
