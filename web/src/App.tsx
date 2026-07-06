import { useState } from 'react';
import { BrowserRouter, Link, Route, Routes, useNavigate } from 'react-router-dom';
import { ChatView } from './chat/ChatView';
import { BillingView } from './billing/BillingView';
import { ChessView } from './chess/ChessView';
import { ChessLabView } from './chess/ChessLabView';
import { ExploreView } from './explore/ExploreView';
import { useAppStore } from './store';

type Tab = 'chat' | 'billing' | 'chess-play' | 'chess-lab';

function MainShell() {
  const [tab, setTab] = useState<Tab>('chat');
  const { tenant, setTenant } = useAppStore();
  const nav = useNavigate();

  return (
    <div className="app">
      <header>
        <h1>
          Laplace
          <span className="tagline">witnessed consensus, not weights</span>
        </h1>
        <nav>
          <button aria-current={tab === 'chat' ? 'page' : undefined} className={tab === 'chat' ? 'active' : ''} onClick={() => setTab('chat')}>Chat</button>
          <button type="button" onClick={() => nav('/explore')}>Explore</button>
          <button aria-current={tab === 'chess-play' ? 'page' : undefined} className={tab === 'chess-play' ? 'active' : ''} onClick={() => setTab('chess-play')}>Play</button>
          <button aria-current={tab === 'chess-lab' ? 'page' : undefined} className={tab === 'chess-lab' ? 'active' : ''} onClick={() => setTab('chess-lab')}>Lab</button>
          <button aria-current={tab === 'billing' ? 'page' : undefined} className={tab === 'billing' ? 'active' : ''} onClick={() => setTab('billing')}>Billing</button>
        </nav>
        <div className="tenant">
          <label htmlFor="tenant">tenant</label>
          <input id="tenant" value={tenant} onChange={(e) => setTenant(e.target.value)} />
        </div>
      </header>
      <main>
        {tab === 'chat' ? <ChatView />
          : tab === 'chess-play' ? <ChessView />
          : tab === 'chess-lab' ? <ChessLabView />
          : <BillingView />}
      </main>
    </div>
  );
}

function ExploreShell() {
  const { tenant, setTenant } = useAppStore();
  const nav = useNavigate();

  return (
    <div className="app">
      <header>
        <h1>
          <Link to="/explore" className="brand-link">Laplace Explore</Link>
          <span className="tagline">warehouse · inspect · export</span>
        </h1>
        <nav>
          <button type="button" onClick={() => nav('/')}>Chat</button>
          <button type="button" className="active">Explore</button>
        </nav>
        <div className="tenant">
          <label htmlFor="tenant-explore">tenant</label>
          <input id="tenant-explore" value={tenant} onChange={(e) => setTenant(e.target.value)} />
        </div>
      </header>
      <main>
        <ExploreView />
      </main>
    </div>
  );
}

export function App() {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/explore/*" element={<ExploreShell />} />
        <Route path="/*" element={<MainShell />} />
      </Routes>
    </BrowserRouter>
  );
}
