import { useState } from 'react';
import { ChatView } from './chat/ChatView';
import { BillingView } from './billing/BillingView';
import { useAppStore } from './store';

type Tab = 'chat' | 'billing';

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
          <button className={tab === 'billing' ? 'active' : ''} onClick={() => setTab('billing')}>Billing</button>
        </nav>
        <div className="tenant">
          <label htmlFor="tenant">tenant</label>
          <input id="tenant" value={tenant} onChange={(e) => setTenant(e.target.value)} />
        </div>
      </header>
      <main>{tab === 'chat' ? <ChatView /> : <BillingView />}</main>
    </div>
  );
}
