import { useEffect } from 'react';
import { BrowserRouter, Link as RouterLink, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { AppHeader, NavTabs, TenantField } from '@ui';
import { ChatView } from './chat/ChatView';
import { HomeView } from './home/HomeView';
import { QueryConsole } from './query/QueryConsole';
import { TopicView } from './topic/TopicView';
import { BillingView } from './billing/BillingView';
import { ChessView } from './chess/ChessView';
import { LabView } from './chess/lab/LabView';
import { ChessDbView } from './chess/db/ChessDbView';
import { ExploreView } from './explore/ExploreView';
import { AdminView } from './admin/AdminView';
import { useAppStore } from './store';
import { SubstrateStatusBanner } from './layout/SubstrateStatusBanner';
import { AmbientFamiliar } from './layout/AmbientFamiliar';
import { AccountControls } from './auth/AccountControls';
import { apiGet } from './api/client';
import type { AuthProvider, AuthUser } from './store';
import styles from './App.module.css';

/**
 * One shell, one nav, for every surface. Previously the app ran two shells — a
 * tab-state MainShell and a separate ExploreShell whose header showed only two
 * destinations, so entering Explore hid Home/Query/Play/Lab/Billing and stranded
 * you there. Everything is a route now: the header is identical everywhere,
 * every surface has a URL (deep-link, refresh, back button), and no page can
 * hide another.
 */
const TABS: { id: string; label: string; path: string }[] = [
  { id: 'home', label: 'Home', path: '/' },
  { id: 'chat', label: 'Chat', path: '/chat' },
  { id: 'query', label: 'Query', path: '/query' },
  { id: 'explore', label: 'Explore', path: '/explore' },
  { id: 'chess', label: 'Chess', path: '/chess' },
  { id: 'play', label: 'Play', path: '/play' },
  { id: 'lab', label: 'Lab', path: '/lab' },
  { id: 'billing', label: 'Billing', path: '/billing' },
  { id: 'operator', label: 'Operator', path: '/operator' },
];

function isActive(pathname: string, tabPath: string): boolean {
  if (tabPath === '/') return pathname === '/';
  return pathname === tabPath || pathname.startsWith(`${tabPath}/`);
}

function Shell() {
  const { tenant, setTenant, authReady, authUser, authProviders, setAuth } = useAppStore();
  const nav = useNavigate();
  const { pathname } = useLocation();

  useEffect(() => {
    let live = true;
    void apiGet<{ authenticated: boolean; user: AuthUser | null; providers: AuthProvider[] }>('/v1/auth/me')
      .then((result) => {
        if (live) setAuth(result.authenticated ? result.user : null, result.providers ?? []);
      })
      .catch(() => {
        if (live) setAuth(null, []);
      });
    return () => { live = false; };
  }, [setAuth]);

  return (
    <div className={styles.shell}>
      <AppHeader
        title={<RouterLink to="/" className={styles.title}>Laplace</RouterLink>}
        tagline="witnessed consensus, not weights"
        nav={
          <NavTabs
            tabs={TABS.map((t) => ({
              id: t.id,
              label: t.label,
              active: isActive(pathname, t.path),
              onClick: () => nav(t.path),
            }))}
          />
        }
        tenant={
          authReady && (authUser || authProviders.length > 0)
            ? <AccountControls user={authUser} providers={authProviders} returnUrl={pathname} />
            : <TenantField value={tenant} onChange={setTenant} />
        }
      />
      <SubstrateStatusBanner />
      <main className={styles.main}>
        <Routes>
          <Route path="/" element={<HomeView onGoto={(t) => nav(`/${t}`)} />} />
          <Route path="/chat" element={<ChatView />} />
          <Route path="/query" element={<QueryConsole />} />
          <Route path="/topic" element={<TopicView />} />
          <Route path="/topic/:ref" element={<TopicView />} />
          <Route path="/explore/*" element={<ExploreView />} />
          <Route path="/chess/*" element={<ChessDbView />} />
          <Route path="/play" element={<ChessView />} />
          <Route path="/lab/*" element={<LabView />} />
          <Route path="/billing" element={<BillingView />} />
          <Route path="/operator" element={<AdminView />} />
        </Routes>
      </main>
      <AmbientFamiliar />
    </div>
  );
}

export function App() {
  return (
    <BrowserRouter>
      <Shell />
    </BrowserRouter>
  );
}
