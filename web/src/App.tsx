import { lazy, Suspense, useEffect } from 'react';
import { BrowserRouter, Link as RouterLink, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { AppHeader, LoadingText, NavTabs, Panel, TenantField } from '@ui';
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

const ChatView = lazy(() => import('./chat/ChatView').then((m) => ({ default: m.ChatView })));
const QueryConsole = lazy(() => import('./query/QueryConsole').then((m) => ({ default: m.QueryConsole })));
const TopicView = lazy(() => import('./topic/TopicView').then((m) => ({ default: m.TopicView })));
const BillingView = lazy(() => import('./billing/BillingView').then((m) => ({ default: m.BillingView })));
const BillingReturnView = lazy(() => import('./auth/SettingsView').then((m) => ({ default: m.BillingReturnView })));
const SettingsView = lazy(() => import('./auth/SettingsView').then((m) => ({ default: m.SettingsView })));
const ChessView = lazy(() => import('./chess/ChessView').then((m) => ({ default: m.ChessView })));
const LabView = lazy(() => import('./chess/lab/LabView').then((m) => ({ default: m.LabView })));
const ChessDbView = lazy(() => import('./chess/db/ChessDbView').then((m) => ({ default: m.ChessDbView })));
const ExploreView = lazy(() => import('./explore/ExploreView').then((m) => ({ default: m.ExploreView })));
const StorageProofView = lazy(() => import('./explore/proof/StorageProofView').then((m) => ({ default: m.StorageProofView })));
const UnicodeGlomeView = lazy(() => import('./explore/unicode/UnicodeGlomeView').then((m) => ({ default: m.UnicodeGlomeView })));
const AdminView = lazy(() => import('./admin/AdminView').then((m) => ({ default: m.AdminView })));
const DataView = lazy(() => import('./data/DataView').then((m) => ({ default: m.DataView })));

const TABS: { id: string; label: string; path: string }[] = [
  { id: 'home', label: 'Home', path: '/' },
  { id: 'chat', label: 'Chat', path: '/chat' },
  { id: 'query', label: 'Query', path: '/query' },
  { id: 'explore', label: 'Explore', path: '/explore' },
  { id: 'proof', label: 'Storage Proof', path: '/proof' },
  { id: 'unicode', label: 'Unicode Glome', path: '/unicode' },
  { id: 'data', label: 'Data', path: '/data' },
  { id: 'chess', label: 'Chess', path: '/chess' },
  { id: 'play', label: 'Play', path: '/play' },
  { id: 'lab', label: 'Lab', path: '/lab' },
  { id: 'billing', label: 'Billing', path: '/billing' },
  { id: 'settings', label: 'Settings', path: '/settings' },
  { id: 'operator', label: 'Operator', path: '/operator' },
];
function isActive(pathname: string, tabPath: string): boolean {
  return tabPath === '/' ? pathname === '/' : pathname === tabPath || pathname.startsWith(`${tabPath}/`);
}
function Shell() {
  const { tenant, setTenant, authReady, authUser, authProviders, setAuth } = useAppStore();
  const navigate = useNavigate();
  const location = useLocation();
  const viewScope = JSON.stringify([tenant, authUser?.id]);
  useEffect(() => {
    let live = true;
    void apiGet<{ authenticated: boolean; user: AuthUser | null; providers: AuthProvider[] }>('/v1/auth/me')
      .then((result) => {
        if (!live) return;
        setApiWorkspace(result.authenticated ? result.user?.tenantId ?? null : null);
        setAuth(result.authenticated ? result.user : null, result.providers ?? []);
      })
      .catch(() => { if (live) setAuth(null, []); });
    return () => { live = false; };
  }, [setAuth]);
  return <UploadProvider><div className={styles.shell}>
    <a className={styles.skipLink} href="#main-content">Skip to workspace</a>
    <AppHeader title={<RouterLink to="/" className={styles.title}>Laplace</RouterLink>} tagline="witnessed consensus, not weights"
      nav={<NavTabs tabs={TABS.map((tab) => ({ id: tab.id, label: tab.label, href: tab.path, active: isActive(location.pathname, tab.path), onClick: () => navigate(tab.path) }))} />}
      tenant={authReady && (authUser || authProviders.length > 0)
        ? <AccountControls user={authUser} providers={authProviders} returnUrl={`${location.pathname}${location.search}${location.hash}`} />
        : <TenantField value={tenant} onChange={setTenant} />} />
    <SubstrateStatusBanner />
    <DataActivity />
    <main id="main-content" tabIndex={-1} className={styles.main}>
      <ViewErrorBoundary resetKey={JSON.stringify([viewScope, location.key])}>
        <Suspense fallback={<LoadingText>Loading workspace…</LoadingText>}>
        <Routes key={viewScope}>
          <Route path="/" element={<HomeView onGoto={(tab) => navigate(`/${tab}`)} />} />
          <Route path="/chat" element={<ChatView />} />
          <Route path="/query" element={<QueryConsole />} />
          <Route path="/data" element={<DataView />} />
          <Route path="/topic" element={<TopicView />} />
          <Route path="/topic/:ref" element={<TopicView />} />
          <Route path="/explore/*" element={<ExploreView />} />
          <Route path="/proof" element={<StorageProofView />} />
          <Route path="/unicode" element={<UnicodeGlomeView />} />
          <Route path="/chess/*" element={<ChessDbView />} />
          <Route path="/play" element={<ChessView />} />
          <Route path="/lab/*" element={<LabView />} />
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
