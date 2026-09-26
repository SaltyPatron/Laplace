import { lazy, Suspense, useEffect, useState } from 'react';
import { BrowserRouter, Link as RouterLink, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { AppHeader, Input, LoadingText, NavTabs, Panel, TenantField } from '@ui';
import { ChessSection } from './chess/ChessSection';
import { HomeView } from './home/HomeView';
import { DataActivity, UploadProvider } from './data/UploadProvider';
import { useAppStore } from './store';
import { SubstrateStatusBanner } from './layout/SubstrateStatusBanner';
import { AmbientFamiliar } from './layout/AmbientFamiliar';
import { ViewErrorBoundary } from './layout/ViewErrorBoundary';
import { AccountControls } from './auth/AccountControls';
import { apiGet, setApiWorkspace } from './api/client';
import type { AuthProvider, AuthUser } from './store';
import styles from './App.module.css';

const loadChat = () => import('./chat/ChatView');
const loadBilling = () => import('./billing/BillingView');
const loadSettings = () => import('./auth/SettingsView');
const loadPlay = () => import('./chess/ChessView');
const loadLab = () => import('./chess/lab/LabView');
const loadChess = () => import('./chess/db/ChessDbView');
const loadExplore = () => import('./explore/ExploreView');
const loadForwardProof = () => import('./forward/ForwardProofView');
const loadOperator = () => import('./admin/AdminView');
const loadData = () => import('./data/DataView');

const ChatView = lazy(() => loadChat().then((m) => ({ default: m.ChatView })));
const BillingView = lazy(() => loadBilling().then((m) => ({ default: m.BillingView })));
const BillingReturnView = lazy(() => loadSettings().then((m) => ({ default: m.BillingReturnView })));
const SettingsView = lazy(() => loadSettings().then((m) => ({ default: m.SettingsView })));
const ChessView = lazy(() => loadPlay().then((m) => ({ default: m.ChessView })));
const LabView = lazy(() => loadLab().then((m) => ({ default: m.LabView })));
const ChessDbView = lazy(() => loadChess().then((m) => ({ default: m.ChessDbView })));
const ExploreView = lazy(() => loadExplore().then((m) => ({ default: m.ExploreView })));
const ForwardProofView = lazy(() => loadForwardProof().then((m) => ({ default: m.ForwardProofView })));
const AdminView = lazy(() => loadOperator().then((m) => ({ default: m.AdminView })));
const DataView = lazy(() => loadData().then((m) => ({ default: m.DataView })));

const WORKSPACE_PREFETCH: Partial<Record<string, () => Promise<unknown>>> = {
  chat: loadChat, explore: loadExplore, data: loadData, chess: loadChess, operator: loadOperator,
};

/** Six places. Each owns the addresses beneath it, so deep links keep their tab lit. */
const TABS: { id: string; label: string; path: string; owns: string[] }[] = [
  { id: 'home', label: 'Home', path: '/', owns: [] },
  { id: 'chat', label: 'Chat', path: '/chat', owns: ['/chat', '/forward-proof'] },
  { id: 'explore', label: 'Explore', path: '/explore', owns: ['/explore', '/topic', '/proof', '/unicode', '/query'] },
  { id: 'data', label: 'Data', path: '/data', owns: ['/data'] },
  { id: 'chess', label: 'Chess', path: '/chess', owns: ['/chess', '/play', '/lab'] },
  { id: 'operator', label: 'Operator', path: '/operator', owns: ['/operator'] },
];
function isActive(pathname: string, tab: (typeof TABS)[number]): boolean {
  if (tab.path === '/') return pathname === '/';
  return tab.owns.some((prefix) => pathname === prefix || pathname.startsWith(`${prefix}/`));
}

/** Laplace discovery from anywhere: the text enters Browse's canonical decomposition. */
function HeaderSearch() {
  const navigate = useNavigate();
  const [text, setText] = useState('');
  return <form role="search" className={styles.search} onSubmit={(event) => {
    event.preventDefault();
    const query = text.trim();
    if (query) navigate(`/explore?q=${encodeURIComponent(query)}`);
  }}>
    <Input aria-label="Search the substrate" value={text} placeholder="Search anything witnessed…" onChange={(event) => setText(event.target.value)} />
  </form>;
}

function Shell() {
  const { tenant, setTenant, authReady, authUser, authProviders, devPrincipal, setAuth } = useAppStore();
  const navigate = useNavigate();
  const location = useLocation();
  const viewScope = JSON.stringify([tenant, authUser?.id]);
  useEffect(() => {
    let live = true;
    void apiGet<{ authenticated: boolean; user: AuthUser | null; providers: AuthProvider[]; devPrincipal?: string | null }>('/v1/auth/me')
      .then((result) => {
        if (!live) return;
        setApiWorkspace(result.authenticated ? result.user?.tenantId ?? null : null);
        setAuth(result.authenticated ? result.user : null, result.providers ?? [], result.devPrincipal ?? null);
      })
      .catch(() => { if (live) setAuth(null, []); });
    return () => { live = false; };
  }, [setAuth]);
  return <UploadProvider><div className={styles.shell}>
    <a className={styles.skipLink} href="#main-content">Skip to workspace</a>
    <AppHeader title={<RouterLink to="/" className={styles.title}>Laplace</RouterLink>} tagline="witnessed consensus, not weights"
      nav={<NavTabs tabs={TABS.map((tab) => ({ id: tab.id, label: tab.label, href: tab.path, active: isActive(location.pathname, tab), onClick: () => navigate(tab.path), onIntent: () => { void WORKSPACE_PREFETCH[tab.id]?.(); } }))} />}
      tenant={<div className={styles.headerTools}>
        <HeaderSearch />
        {devPrincipal && !authUser && <span className={styles.devPrincipal} title="LAPLACE_AUTH_DEV_PRINCIPAL: this host serves uncredentialed requests as this workspace">dev · {devPrincipal}</span>}
        {authReady && (authUser || authProviders.length > 0)
          ? <AccountControls user={authUser} providers={authProviders} returnUrl={`${location.pathname}${location.search}${location.hash}`} />
          : !devPrincipal && <TenantField value={tenant} onChange={setTenant} />}
        <nav className={styles.accountLinks} aria-label="Account">
          <RouterLink to="/billing">Billing</RouterLink>
          <RouterLink to="/settings">Settings</RouterLink>
        </nav>
      </div>} />
    <SubstrateStatusBanner />
    <DataActivity />
    <main id="main-content" tabIndex={-1} className={styles.main}>
      <ViewErrorBoundary resetKey={JSON.stringify([viewScope, location.key])}>
        <Suspense fallback={<LoadingText>Loading workspace…</LoadingText>}>
        <Routes key={viewScope}>
          <Route path="/" element={<HomeView onGoto={(tab) => navigate(`/${tab}`)} />} />
          <Route path="/chat" element={<ChatView />} />
          <Route path="/query" element={<ExploreView page="query" />} />
          <Route path="/data" element={<DataView />} />
          <Route path="/explore/*" element={<ExploreView />} />
          <Route path="/topic" element={<ExploreView page="topic" />} />
          <Route path="/topic/:ref" element={<ExploreView page="topic" />} />
          <Route path="/proof" element={<ExploreView page="proof" />} />
          <Route path="/unicode" element={<ExploreView page="unicode" />} />
          <Route path="/forward-proof" element={<ForwardProofView />} />
          <Route path="/chess/*" element={<ChessSection><ChessDbView /></ChessSection>} />
          <Route path="/play" element={<ChessSection><ChessView /></ChessSection>} />
          <Route path="/lab/*" element={<ChessSection><LabView /></ChessSection>} />
          <Route path="/billing" element={<BillingView />} />
          <Route path="/billing/success" element={<BillingReturnView />} />
          <Route path="/billing/cancel" element={<BillingReturnView />} />
          <Route path="/settings" element={<SettingsView />} />
          <Route path="/operator" element={<AdminView />} />
          <Route path="*" element={<Panel title="Workspace not found"><p>This address does not match a Laplace workspace. Use the navigation above or return Home.</p><RouterLink to="/">Return Home</RouterLink></Panel>} />
        </Routes>
        </Suspense>
      </ViewErrorBoundary>
    </main>
    <AmbientFamiliar />
  </div></UploadProvider>;
}
export function App() { return <BrowserRouter><Shell /></BrowserRouter>; }
