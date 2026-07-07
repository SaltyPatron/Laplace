import { useState } from 'react';
import { BrowserRouter, Link as RouterLink, Route, Routes, useNavigate } from 'react-router-dom';
import { AppHeader, NavTabs, TenantField } from '@ui';
import { ChatView } from './chat/ChatView';
import { BillingView } from './billing/BillingView';
import { ChessView } from './chess/ChessView';
import { ChessLabView } from './chess/ChessLabView';
import { ExploreView } from './explore/ExploreView';
import { useAppStore } from './store';
import { SubstrateStatusBanner } from './layout/SubstrateStatusBanner';
import styles from './App.module.css';

type Tab = 'chat' | 'billing' | 'chess-play' | 'chess-lab';

function MainShell() {
  const [tab, setTab] = useState<Tab>('chat');
  const { tenant, setTenant } = useAppStore();
  const nav = useNavigate();

  return (
    <div className={styles.shell}>
      <AppHeader
        title="Laplace"
        tagline="witnessed consensus, not weights"
        nav={
          <NavTabs
            tabs={[
              { id: 'chat', label: 'Chat', active: tab === 'chat', onClick: () => setTab('chat') },
              { id: 'explore', label: 'Explore', onClick: () => nav('/explore') },
              { id: 'play', label: 'Play', active: tab === 'chess-play', onClick: () => setTab('chess-play') },
              { id: 'lab', label: 'Lab', active: tab === 'chess-lab', onClick: () => setTab('chess-lab') },
              { id: 'billing', label: 'Billing', active: tab === 'billing', onClick: () => setTab('billing') },
            ]}
          />
        }
        tenant={<TenantField value={tenant} onChange={setTenant} />}
      />
      <SubstrateStatusBanner />
      <main className={styles.main}>
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
    <div className={styles.shell}>
      <AppHeader
        title={<RouterLink to="/explore" className={styles.exploreTitle}>Laplace Explore</RouterLink>}
        tagline="warehouse · inspect · export"
        nav={
          <NavTabs
            tabs={[
              { id: 'chat', label: 'Chat', onClick: () => nav('/') },
              { id: 'explore', label: 'Explore', active: true, onClick: () => {} },
            ]}
          />
        }
        tenant={<TenantField id="tenant-explore" value={tenant} onChange={setTenant} />}
      />
      <SubstrateStatusBanner />
      <main className={styles.main}>
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
