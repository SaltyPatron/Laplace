/**
 * Invented competitions for stories, fixtures and tests. No route imports this file. The results
 * here are made up by `settle`; in the product, results, advancement and standings are given.
 */
import type { Competition, Entrant, Match, Slot, Stage, Standings } from './model';

const NAMES = ['Avery Stone', 'Blake Rivers', 'Casey Lane', 'Devon Pike', 'Emery Wood', 'Finley Cross', 'Gray Marsh', 'Harper Dale',
  'Indigo Frost', 'Jordan Vale', 'Kendall Reed', 'Logan Hayes', 'Morgan Bell', 'Noel Park', 'Oakley Shaw', 'Parker Quinn'];

const field = (count: number): Entrant[] => NAMES.slice(0, count).map((name, i) => ({ id: `e${i + 1}`, name, seed: i + 1 }));
const pins = (seed: number, salt: number) => 150 + ((seed * 37 + salt * 53) % 120);
const entry = (seed: number): Slot => ({ entrant: `e${seed}` });
const winner = (match: string): Slot => ({ from: { match, place: 1 } });
const loser = (match: string): Slot => ({ from: { match, place: 2 } });
const game = (id: string, stage: string, round: number, name: string, slots: Slot[]): Match => ({ id, stage, round, name, state: 'scheduled', slots });

/** Plays the first `played` matches in order, carries finishers forward, and marks the next ready match live. */
function settle(matches: Match[], played: number): Match[] {
  const done = new Map<string, Match>();
  let live = false;
  return matches.map((match, order) => {
    const slots = match.slots.map((slot): Slot => {
      const source = slot.from && done.get(slot.from.match);
      const finisher = source?.state === 'final' ? source.slots.find((each) => each.place === slot.from?.place) : undefined;
      return finisher ? { ...slot, entrant: finisher.entrant } : slot;
    });
    const ready = slots.every((slot) => slot.entrant);
    let out: Match = { ...match, slots };
    if (ready && order < played) {
      const games = slots.length > 2 ? 3 : 1;
      const totals = slots.map((slot) => {
        const seed = Number(slot.entrant?.slice(1));
        let total = 0;
        for (let g = 0; g < games; g++) total += pins(seed, order + g * 7);
        return total;
      });
      const ranked = totals.map((total, i) => ({ total, i })).sort((a, b) => b.total - a.total || a.i - b.i);
      out = { ...out, state: 'final', slots: slots.map((slot, i) => ({ ...slot, score: String(totals[i]), place: ranked.findIndex((r) => r.i === i) + 1 })) };
    } else if (ready && !live) {
      live = true;
      out = { ...out, state: 'live' };
    }
    done.set(out.id, out);
    return out;
  });
}

/** Seeds in bracket order: 1 meets the last seed, and 1 and 2 can only meet in the final. */
function seedOrder(size: number): number[] {
  let order = [1];
  while (order.length < size) order = order.flatMap((seed) => [seed, order.length * 2 + 1 - seed]);
  return order;
}

function roundNames(rounds: number): string[] {
  return Array.from({ length: rounds }, (_, i) => {
    const left = rounds - i;
    return left === 1 ? 'Final' : left === 2 ? 'Semifinals' : left === 3 ? 'Quarterfinals' : `Round of ${2 ** left}`;
  });
}

/** Any number of entrants. A seed with no opponent has no first-round match: it simply enters the next round. */
export function singleElimination(count: number, played: number): Competition {
  let size = 2;
  while (size < count) size *= 2;
  const rounds = Math.log2(size);
  const titles = roundNames(rounds);
  const matches: Match[] = [];
  let feeds: Slot[] = seedOrder(size).map((seed) => (seed <= count ? entry(seed) : {}));
  for (let round = 1; round <= rounds; round++) {
    const next: Slot[] = [];
    for (let i = 0; i < feeds.length; i += 2) {
      const a = feeds[i]; const b = feeds[i + 1];
      const bye = !a.entrant && !a.from ? b : !b.entrant && !b.from ? a : null;
      if (bye) { next.push(bye); continue; }
      const id = `r${round}m${i / 2 + 1}`;
      const single = titles[round - 1].replace(/s$/, '');
      matches.push(game(id, 'main', round, feeds.length === 2 ? 'Final' : `${single} ${i / 2 + 1}`, [a, b]));
      next.push(winner(id));
    }
    feeds = next;
  }
  return { title: `${count}-player knockout`, entrants: field(count), stages: [{ id: 'main', title: 'Bracket', rounds: titles }], matches: settle(matches, played) };
}

/** Eight entrants, two lives each: a winners bracket, a losers bracket fed from it, and a final between the two. */
export function doubleElimination(played: number): Competition {
  const w = (n: number, a: Slot, b: Slot, round: number) => game(`w${n}`, 'winners', round, `Winners ${n}`, [a, b]);
  const l = (n: number, a: Slot, b: Slot, round: number) => game(`l${n}`, 'losers', round, `Losers ${n}`, [a, b]);
  const matches = [
    w(1, entry(1), entry(8), 1), w(2, entry(4), entry(5), 1), w(3, entry(2), entry(7), 1), w(4, entry(3), entry(6), 1),
    w(5, winner('w1'), winner('w2'), 2), w(6, winner('w3'), winner('w4'), 2),
    l(1, loser('w1'), loser('w2'), 1), l(2, loser('w3'), loser('w4'), 1),
    w(7, winner('w5'), winner('w6'), 3),
    l(3, winner('l1'), loser('w6'), 2), l(4, winner('l2'), loser('w5'), 2),
    l(5, winner('l3'), winner('l4'), 3), l(6, winner('l5'), loser('w7'), 4),
    game('gf', 'final', 1, 'Grand final', [winner('w7'), winner('l6')]),
  ];
  const stages: Stage[] = [{ id: 'winners', title: 'Winners' }, { id: 'losers', title: 'Losers' }, { id: 'final', title: 'Final' }];
  return { title: 'Double elimination', entrants: field(8), stages, matches: settle(matches, played) };
}

/** Everyone meets everyone once, by the circle method. */
export function roundRobin(count: number, played: number): Competition {
  const entrants = field(count);
  const ring = entrants.map((_, i) => i + 1);
  const matches: Match[] = [];
  for (let round = 1; round < count; round++) {
    for (let i = 0; i < count / 2; i++) {
      matches.push(game(`r${round}m${i + 1}`, 'league', round, `Week ${round}, pair ${i + 1}`, [entry(ring[i]), entry(ring[count - 1 - i])]));
    }
    ring.splice(1, 0, ring.pop() as number);
  }
  const settled = settle(matches, played);
  const tally = new Map(entrants.map((entrant) => [entrant.id, { won: 0, lost: 0, pins: 0 }]));
  for (const match of settled) for (const slot of match.slots) {
    const row = slot.entrant ? tally.get(slot.entrant) : undefined;
    if (!row || match.state !== 'final') continue;
    if (slot.place === 1) row.won++; else row.lost++;
    row.pins += Number(slot.score);
  }
  const standings: Standings = {
    stage: 'league', columns: ['W', 'L', 'Pins'],
    rows: [...tally].sort((a, b) => b[1].won - a[1].won || b[1].pins - a[1].pins)
      .map(([entrant, row]) => ({ entrant, cells: [String(row.won), String(row.lost), String(row.pins)] })),
  };
  return { title: 'Round robin league', entrants, stages: [{ id: 'league', title: 'League' }], matches: settled, standings: [standings] };
}

/** A sweeper: twelve bowl a three-game block, and the top five climb a stepladder for the title. */
export function sweeperLadder(played: number): Competition {
  const entrants = field(12);
  const from = (place: number): Slot => ({ from: { match: 'q', place } });
  const matches = [
    game('q', 'qualifying', 1, 'Qualifying block', entrants.map((_, i) => entry(i + 1))),
    game('s1', 'ladder', 1, 'Step 1', [from(5), from(4)]),
    game('s2', 'ladder', 2, 'Step 2', [winner('s1'), from(3)]),
    game('s3', 'ladder', 3, 'Step 3', [winner('s2'), from(2)]),
    game('s4', 'ladder', 4, 'Title match', [winner('s3'), from(1)]),
  ];
  const stages: Stage[] = [{ id: 'qualifying', title: 'Qualifying' }, { id: 'ladder', title: 'Stepladder', rounds: ['Step 1', 'Step 2', 'Step 3', 'Title'] }];
  return { title: 'Friday sweeper', entrants, stages, matches: settle(matches, played) };
}
