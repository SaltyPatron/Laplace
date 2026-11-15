// Presentation only: the production event pages against an in-memory stand-in for the API. Never contacts a server.
// The stand-in knows one bracket (four entrants, single elimination); the real pairing law is the server's.
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { TooltipProvider } from '../src/ui';
import { EventsView } from '../src/compete/EventsView';
import '../src/ui/layers.css';
import '../src/ui/theme.css';

interface Slot { entrant: string | null; from: { match: string; place: number } | null; score: string | null; place: number | null }
interface Match { id: string; stage: string; round: number; name: string; state: 'scheduled' | 'final'; slots: Slot[] }
interface Event { id: string; title: string; format: string; status: 'registration' | 'running'; session?: string | null; pairing?: string; entrants: { id: string; name: string; seed: number | null }[]; matches: Match[] }

const params = new URLSearchParams(location.search);
const FREE = 8;
const events = new Map<string, Event>();
const sessions: { id: string; title: string; created_at: string; covered_until: string | null }[] = [];
let next = 1;
const json = (body: unknown, status = 200) => Response.json(body, { status });
const refuse = (status: number, code: string, message: string) => json({ error: { type: 'competition_error', code, message } }, status);
const entry = (entrant: string): Slot => ({ entrant, from: null, score: null, place: null });
const winner = (match: string): Slot => ({ entrant: null, from: { match, place: 1 }, score: null, place: null });

/** The one draw the stand-in knows: four entrants, single elimination, in the order they stand. */
function draw(event: Event): Match[] {
  if (event.entrants.length !== 4) return [];
  const [a, b, c, d] = event.entrants.map((e) => e.id);
  return [
    { id: 'r1m1', stage: 'main', round: 1, name: 'Semifinal 1', state: 'scheduled', slots: event.pairing === 'list' ? [entry(a), entry(b)] : [entry(a), entry(d)] },
    { id: 'r1m2', stage: 'main', round: 1, name: 'Semifinal 2', state: 'scheduled', slots: event.pairing === 'list' ? [entry(c), entry(d)] : [entry(b), entry(c)] },
    { id: 'r2m1', stage: 'main', round: 2, name: 'Final', state: 'scheduled', slots: [winner('r1m1'), winner('r1m2')] },
  ];
}

if (params.get('view') === 'large') {
  events.set('big', {
    id: 'big', title: 'Big sweeper', format: 'single_elimination', status: 'registration', matches: [],
    entrants: Array.from({ length: FREE + 1 }, (_, i) => ({ id: `b${i}`, name: `Bowler ${i + 1}`, seed: null })),
  });
}

window.fetch = async (input, init) => {
  const path = String(input).split('?')[0];
  const method = init?.method ?? 'GET';
  const body = init?.body ? JSON.parse(String(init.body)) : {};
  const parts = path.split('/').filter(Boolean).slice(2);
  if (path === '/v1/competitions/formats') {
    return json({ free_entrants: FREE, max_entrants: 256, host_service: 'competition.host', session_hours: 24, formats: [
      { id: 'single_elimination', title: 'Single elimination', summary: 'Lose once and you are out.', paired: true },
      { id: 'round_robin', title: 'Round robin', summary: 'Everyone plays everyone once.', paired: false },
    ], pairings: [
      { id: 'seeded', title: 'Top against bottom', summary: '1 plays the last seed.' },
      { id: 'list', title: 'As listed', summary: '1 plays 2, 3 plays 4.' },
    ] });
  }
  if (path === '/v1/competitions/sessions' && method === 'GET') return json({ sessions });
  if (path === '/v1/competitions/sessions' && method === 'POST') {
    const session = { id: `s${next++}`, title: body.title as string, created_at: '2026-11-15T12:00:00Z', covered_until: null };
    sessions.push(session);
    return json(session, 201);
  }
  if (path === '/v1/billing/preflight') return json({ quote_id: 'q_fixture', stripe_checkout_url: null });
  if (path === '/v1/competitions' && method === 'GET') {
    return json({ competitions: [...events.values()].map((e) => ({ id: e.id, title: e.title, format: e.format, status: e.status, created_at: '2026-11-15T12:00:00Z', entrants: e.entrants.length, session: e.session ?? null })) });
  }
  if (path === '/v1/competitions' && method === 'POST') {
    const id = `e${next++}`;
    events.set(id, { id, title: body.title, format: body.format, status: 'registration', session: body.session ?? null, entrants: [], matches: [] });
    return json({ id }, 201);
  }
  const event = events.get(parts[0]);
  if (!event) return refuse(404, 'competition_not_found', 'No such event exists.');
  if (parts.length === 1 && method === 'GET') {
    const held = sessions.find((s) => s.id === event.session);
    return json({ ...event, pairing: event.pairing ?? 'seeded', session: held ? { id: held.id, title: held.title, covered: false } : null, matches: event.status === 'registration' ? draw(event) : event.matches, host: params.get('host') !== '0', standings: [],
      stages: [{ id: 'main', title: 'Bracket', rounds: ['Semifinals', 'Final'] }] });
  }
  if (parts.length === 1 && method === 'DELETE') { events.delete(event.id); return new Response(null, { status: 204 }); }
  if (parts[1] === 'entrants' && parts[2] === 'batch') {
    const fresh = (body.names as string[]).filter((name) => {
      if (event.entrants.some((e) => e.name.toLowerCase() === name.toLowerCase())) return false;
      event.entrants.push({ id: `p${next++}`, name, seed: null });
      return true;
    });
    return json({ added: fresh.length, skipped: body.names.length - fresh.length });
  }
  if (parts[1] === 'draw' && method === 'PUT') {
    if (body.format) event.format = body.format;
    if (body.pairing) event.pairing = body.pairing;
    if (body.order) event.entrants = (body.order as string[]).map((id) => event.entrants.find((e) => e.id === id)!);
    if (body.shuffle) [event.entrants[1], event.entrants[3]] = [event.entrants[3], event.entrants[1]];
    return new Response(null, { status: 204 });
  }
  if (parts[1] === 'entrants' && method === 'POST') {
    if (event.entrants.some((e) => e.name.toLowerCase() === String(body.name).toLowerCase())) {
      return refuse(409, 'registration_refused', 'That name is already registered, or the event is full.');
    }
    const entrant = { id: `p${next++}`, name: body.name, seed: null };
    event.entrants.push(entrant);
    return json(entrant, 201);
  }
  if (parts[1] === 'entrants' && method === 'DELETE') {
    event.entrants = event.entrants.filter((e) => e.id !== parts[2]);
    return new Response(null, { status: 204 });
  }
  if (parts[1] === 'start') {
    if (event.entrants.length > FREE) return json({ error: { type: 'billing_error', code: 'quote_required', message: 'Payment required' } }, 402);
    event.entrants.forEach((e, i) => { e.seed = i + 1; });
    event.matches = draw(event);

    event.status = 'running';
    return json({ id: event.id, matches: 3 });
  }
  if (parts[1] === 'matches' && method === 'PUT') {
    const match = event.matches.find((m) => m.id === parts[2]);
    if (!match) return refuse(404, 'competition_not_found', 'No such event exists.');
    match.state = 'final';
    match.slots.forEach((slot, i) => { slot.score = body.slots[i].score; slot.place = body.slots[i].place; });
    for (const later of event.matches) for (const slot of later.slots) {
      if (slot.from?.match === match.id) slot.entrant = match.slots.find((s) => s.place === slot.from?.place)?.entrant ?? null;
    }
    return new Response(null, { status: 204 });
  }
  return refuse(404, 'unknown_route', 'No such API route.');
};

createRoot(document.getElementById('root')!).render(
  <StrictMode><TooltipProvider><MemoryRouter initialEntries={[params.get('at') ?? '/events']}>
    <main><h1>Events fixture</h1><h2>Events</h2><Routes><Route path="/events/*" element={<EventsView />} /></Routes></main>
  </MemoryRouter></TooltipProvider></StrictMode>,
);
