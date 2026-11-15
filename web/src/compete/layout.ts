/**
 * Where the matches of one stage sit on the page. The shape comes from the DAG alone: a format's
 * name never selects a layout. Units are rem; the stylesheet reads the same constants.
 */
import type { Match } from './model';

/** Width of a match, the gutter its connectors cross, its header, one slot row, the gap between stacked matches. */
export const COL = 13;
export const GAP = 3;
export const HEAD = 1.5;
export const ROW = 1.875;
export const LANE = 0.75;

/** tree: slots fed by earlier matches of the stage. pairs: independent two-sided matches. field: everyone at once. */
export type StageShape = 'tree' | 'pairs' | 'field';

export interface Box { id: string; column: number; x: number; y: number; height: number }
export interface Link { from: string; to: string; slot: number; x1: number; y1: number; x2: number; y2: number }
export interface StageLayout { rounds: number[]; boxes: Box[]; links: Link[]; width: number; height: number }

export function stageShape(matches: readonly Match[]): StageShape {
  const ids = new Set(matches.map((match) => match.id));
  if (matches.some((match) => match.slots.some((slot) => slot.from && ids.has(slot.from.match)))) return 'tree';
  return matches.every((match) => match.slots.length === 2) ? 'pairs' : 'field';
}

/**
 * One column per round. A match fed from earlier rounds of this stage is centred on its feeders;
 * a match with none follows the one before it. Matches in a column never overlap.
 */
export function layoutStage(matches: readonly Match[]): StageLayout {
  const rounds = [...new Set(matches.map((match) => match.round))].sort((a, b) => a - b);
  const placed = new Map<string, Box>();
  const boxes: Box[] = [];
  const links: Link[] = [];
  let height = 0;
  rounds.forEach((round, column) => {
    const x = column * (COL + GAP);
    let cursor = 0;
    const pending = matches.filter((match) => match.round === round).map((match, order) => {
      const tall = HEAD + match.slots.length * ROW;
      const feeders = match.slots.flatMap((slot) => {
        const box = slot.from && placed.get(slot.from.match);
        return box ? [box] : [];
      });
      const top = feeders.length
        ? Math.max(0, feeders.reduce((sum, box) => sum + box.y + box.height / 2, 0) / feeders.length - tall / 2)
        : cursor;
      cursor = top + tall + LANE;
      return { match, order, top, tall };
    });
    pending.sort((a, b) => a.top - b.top || a.order - b.order);
    let floor = 0;
    const column_boxes: Box[] = [];
    for (const { match, top, tall } of pending) {
      const y = Math.max(top, floor);
      floor = y + tall + LANE;
      height = Math.max(height, y + tall);
      column_boxes.push({ id: match.id, column, x, y, height: tall });
      match.slots.forEach((slot, index) => {
        const source = slot.from && placed.get(slot.from.match);
        if (!source) return;
        links.push({
          from: source.id, to: match.id, slot: index,
          x1: source.x + COL, y1: source.y + source.height / 2,
          x2: x, y2: y + HEAD + (index + 0.5) * ROW,
        });
      });
    }
    for (const box of column_boxes) { placed.set(box.id, box); boxes.push(box); }
  });
  return { rounds, boxes, links, width: Math.max(0, rounds.length * (COL + GAP) - GAP), height };
}
