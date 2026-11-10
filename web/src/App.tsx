import { lazy, Suspense, useEffect, useState } from 'react';
import { BrowserRouter, Link as RouterLink, Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { Button, LoadingText, Panel, TenantField } from '@ui';
import { AppShell } from './shell/AppShell';
import { ChessSection } from './chess/ChessSection';
import { DataActivity, UploadProvider } from './data/UploadProvider';
import { useAppStore } from './store';
import { SubstrateStatusBanner } from './layout/SubstrateStatusBanner';
import { AmbientFamiliar } from './layout/AmbientFamiliar';
import { ViewErrorBoundary } from './layout/ViewErrorBoundary';
import { AccountControls } from './auth/AccountControls';
import { apiGet, describeFailure, setApiWorkspace } from './api/client';
import type { AuthProvider, AuthUser, ServicePrincipal } from './store';
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

function Shell() {
  const { tenant, setTenant, authReady, authUser, authProviders, devPrincipal, servicePrincipal, setAuth } = useAppStore();
  const location = useLocation();
  const viewScope = JSON.stringify([tenant, authUser?.id]);
  const [authError, setAuthError] = useState<string | null>(null);
  const [authAttempt, setAuthAttempt] = useState(0);
  useEffect(() => {
    let live = true;
    const controller = new AbortController();
    setAuthError(null);
    void apiGet<{ authenticated: boolean; user: AuthUser | null; providers: AuthProvider[]; devPrincipal?: string | null;
      principal?: { kind: string; tenantId: string; label?: string | null } | null }>('/v1/auth/me', { signal: controller.signal })
      .then((result) => {
        if (!live) return;
        const service: ServicePrincipal | null = result.authenticated && !result.user && result.principal
          && (result.principal.kind === 'api_key' || result.principal.kind === 'operator')
          ? { kind: result.principal.kind, tenantId: result.principal.tenantId, label: result.principal.label ?? null } : null;
        setApiWorkspace(result.authenticated ? result.user?.tenantId ?? service?.tenantId ?? null : null);
        setAuth(result.authenticated ? result.user : null, result.providers ?? [], result.devPrincipal ?? null, service);
      })
      .catch((error: unknown) => { if (live) setAuthError(describeFailure(error).message); });
    return () => { live = false; controller.abort(); };
  }, [setAuth, authAttempt]);
  const signedIn = !!(authUser || servicePrincipal || devPrincipal);
  return <UploadProvider><AppShell showAdmin={signedIn} familiar={<AmbientFamiliar />}
    account={<>
      {devPrincipal && !authUser && !servicePrincipal && <span className={styles.devPrincipal} title="LAPLACE_AUTH_DEV_PRINCIPAL: this host serves uncredentialed requests as this workspace">dev · {devPrincipal}</span>}
      {servicePrincipal && !authUser && <span className={styles.devPrincipal} title="Signed in with a service credential">
        {servicePrincipal.kind === 'operator' ? 'operator' : `API key · ${servicePrincipal.label ?? servicePrincipal.tenantId}`}</span>}
      {authReady && !servicePrincipal && (authUser || authProviders.length > 0)
        ? <AccountControls user={authUser} providers={authProviders} returnUrl={`${location.pathname}${location.search}${location.hash}`} />
        : !devPrincipal && !servicePrincipal && <TenantField value={tenant} onChange={setTenant} />}
    </>}
    banners={<>
      {authError && <Panel title="Account information could not be loaded">
        <p role="alert">{authError}</p>
        <Button onClick={() => setAuthAttempt((attempt) => attempt + 1)}>Retry account information</Button>
      </Panel>}
      <SubstrateStatusBanner />
      <DataActivity />
    </>}>
      <ViewErrorBoundary resetKey={JSON.stringify([viewScope, location.key])}>
        <Suspense fallback={<LoadingText>Loading workspace…</LoadingText>}>
        <Routes key={viewScope}>
          <Route path="/" element={<Navigate to="/explore" replace />} />
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
          <Route path="*" element={<Panel title="Page not found"><p>This address does not match a page. Use the navigation or search (Ctrl K).</p><RouterLink to="/">Go to Look up</RouterLink></Panel>} />
        </Routes>
        </Suspense>
      </ViewErrorBoundary>
  </AppShell></UploadProvider>;
}
export function App() { return <BrowserRouter><Shell /></BrowserRouter>; }
