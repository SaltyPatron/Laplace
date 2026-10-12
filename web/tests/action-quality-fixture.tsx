// Controlled transport failures around the production billing and training components.
import { useState } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter } from 'react-router-dom';
import { TooltipProvider } from '../src/ui';
import { GatePrompt } from '../src/explore/components/GatePrompt';
import { ChessView } from '../src/chess/ChessView';
import { useAppStore } from '../src/store';
import '../src/ui/layers.css';
import '../src/ui/theme.css';

const params = new URLSearchParams(location.search);
const view = params.get('view') ?? 'gate-paid';
useAppStore.getState().setTenant('fixture-workspace');
let releaseQuote: (() => void) | undefined;
let releaseStatus: (() => void) | undefined;
let delayNextStatus = false;
let commandFailures = true;
let statusFailures = params.get('statusError') === '1';
let running = params.get('running') === '1';
const failure = (message: string) => Response.json({ error: { message } }, { status: 503 });
const fen = 'rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1';
window.fetch = async (input) => {
  const path = String(input);
  if (path === '/v1/billing/plans') return Response.json({ billing_enforced: view !== 'gate-bypass', data: [] });
  if (path === '/v1/billing/preflight') {
    if (params.get('deferQuote') === '1') await new Promise<void>((resolve) => { releaseQuote = resolve; });
    return Response.json({ quote_id: 'fixture-quote', amount_cents: 250, currency: 'usd', status: 'approved' });
  }
  if (path === '/chess/train/status') {
    const response = statusFailures ? failure('Training status unavailable')
      : Response.json({ running, games: 0, white: 0, black: 0, draws: 0, adjudicated: 0, lastOutcome: '', temperature: 120, weight: 0.5, maxPlies: 200 });
    if (delayNextStatus) {
      delayNextStatus = false;
      await new Promise<void>((resolve) => { releaseStatus = resolve; });
    }
    return response;
  }
  if (path.startsWith('/chess/train/start') || path === '/chess/train/stop') {
    running = path.startsWith('/chess/train/start');
    return commandFailures ? failure('Command response lost') : Response.json({ ok: true });
  }
  if (path === '/chess/legal') return Response.json({ moves: [], inCheck: false, status: 'ongoing' });
  if (path === '/chess/play/start') return Response.json({ sessionId: 'fixture-session', fen, status: 'ongoing', ply: 0 });
  if (path === '/chess/eval') return Response.json({ whiteScoreCp: 0, depth: 0, nodes: 0 });
  return failure('Unused fixture read');
};

function Fixture() {
  const [ready, setReady] = useState(0);
  return <>
    <h1>Action quality fixture</h1>
    {view === 'training' ? <>
      <button onClick={() => { commandFailures = false; }}>Allow commands</button>
      <button onClick={() => { statusFailures = false; }}>Restore status reads</button>
      <button onClick={() => { delayNextStatus = true; }}>Delay next status read</button>
      <button onClick={() => releaseStatus?.()}>Complete pending status</button>
      <ChessView />
    </> : <>
      <button onClick={() => useAppStore.getState().setTenant('second-workspace')}>Switch workspace</button>
      <button onClick={() => releaseQuote?.()}>Complete pending quote</button>
      <output aria-label="Ready calls">{ready}</output>
      <GatePrompt serviceId="fixture.read" label="Load fixture data" onReady={() => setReady((n) => n + 1)} />
    </>}
  </>;
}
createRoot(document.getElementById('root')!).render(<TooltipProvider><MemoryRouter><Fixture /></MemoryRouter></TooltipProvider>);
