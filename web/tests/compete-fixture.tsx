// Presentation only: invented competitions drawn by the production components. Never contacts a substrate.
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { Panel, TooltipProvider } from '../src/ui';
import { CompetitionView } from '../src/compete/CompetitionView';
import { doubleElimination, roundRobin, singleElimination, sweeperLadder } from '../src/compete/samples';
import '../src/ui/layers.css';
import '../src/ui/theme.css';

const params = new URLSearchParams(location.search);
const played = Number(params.get('played') ?? 5);
const size = Number(params.get('size') ?? 8);
const view = params.get('view') ?? 'single';
const competition = view === 'double' ? doubleElimination(played)
  : view === 'robin' ? roundRobin(size, played)
  : view === 'sweeper' ? sweeperLadder(played)
  : singleElimination(size, played);

createRoot(document.getElementById('root')!).render(
  <StrictMode><TooltipProvider><main><h1>Competition fixture</h1><Panel><CompetitionView competition={competition} /></Panel></main></TooltipProvider></StrictMode>,
);
