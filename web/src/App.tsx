import { useState } from 'react';
import { ChatView } from './chat/ChatView';
import { BillingView } from './billing/BillingView';
import { ChessView } from './chess/ChessView';
import { ChessLabView } from './chess/ChessLabView';
import { useAppStore } from './store';

type Tab = 'chat' | 'billing' | 'chess-play' | 'chess-lab';

export function App() {
  const [tab, setTab] = useState<Tab>('chat');
  const { tenant, setTenant } = useAppStore();

  return (
    <div className="app">
      <header>
        <h1>
          Laplace
          <span className="tagline">witnessed consensus, not weights</span>
        </h1>
        <nav>
          <button className={tab === 'chat' ? 'active' : ''} onClick={() => setTab('chat')}>Chat</button>
          <button className={tab === 'chess-play' ? 'active' : ''} onClick={() => setTab('chess-play')}>Play</button>
          <button className={tab === 'chess-lab' ? 'active' : ''} onClick={() => setTab('chess-lab')}>Lab</button>
          <button className={tab === 'billing' ? 'active' : ''} onClick={() => setTab('billing')}>Billing</button>
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
