import { useEffect, useState, type ReactNode } from 'react';
import { Link as RouterLink, useLocation } from 'react-router-dom';
import { Icon } from '../ui/icons';
import { cn } from '../ui/lib/cn';
import { Checkbox } from '../ui/primitives/Checkbox';
import { UnstyledButton } from '../ui/primitives/UnstyledButton';
import { CommandPalette, usePalette } from './CommandPalette';
import { InspectorDrawer } from './InspectorDrawer';
import { useInspector } from './inspector';
import { currentNav, NAV } from './nav';
import styles from './AppShell.module.css';

const FAMILIAR_KEY = 'laplace.familiar';
const readFamiliar = () => { try { return localStorage.getItem(FAMILIAR_KEY) === 'on'; } catch { return false; } };

export interface AppShellProps {
  /** Account controls, workspace selector and principal chips. */
  account: ReactNode;
  /** Status banners shown above the page (substrate status, uploads, auth errors). */
  banners?: ReactNode;
  /** Whether the viewer has platform tools; hides admin destinations otherwise. */
  showAdmin: boolean;
  /** Rendered only when the person has turned it on. */
  familiar?: ReactNode;
  children: ReactNode;
}

/** Rail + top bar + page + inspector drawer + command palette. Every page lives inside this one frame. */
export function AppShell({ account, banners, showAdmin, familiar, children }: AppShellProps) {
  const { pathname } = useLocation();
  const { item: currentItem, child: currentChild } = currentNav(pathname);
  const { current: inspecting } = useInspector();
  const showPalette = usePalette((state) => state.show);
  const [menuOpen, setMenuOpen] = useState(false);
  const [familiarOn, setFamiliarOn] = useState(readFamiliar);

  useEffect(() => { setMenuOpen(false); }, [pathname]);
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') { event.preventDefault(); showPalette(); }
      else if (event.key === '/' && !(event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement || (event.target as HTMLElement)?.isContentEditable)) { event.preventDefault(); showPalette(); }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [showPalette]);

  const items = NAV.filter((item) => !item.admin || showAdmin);
  return (
    <div className={cn(styles.shell, inspecting && styles.inspecting)}>
      <a className={styles.skipLink} href="#main-content">Skip to content</a>
      <nav className={cn(styles.rail, menuOpen && styles.railOpen)} aria-label="Primary navigation">
        <RouterLink to="/" className={styles.brand} aria-label="Laplace home"><span className={styles.mark} aria-hidden="true">◇</span><span>Laplace</span></RouterLink>
        <ul className={styles.navList}>
          {items.map((item) => {
            const active = item.id === currentItem?.id;
            return <li key={item.id}>
              <RouterLink to={item.path} className={cn(styles.navItem, active && styles.navActive)} aria-current={active && !currentChild ? 'page' : undefined} title={item.description} aria-label={item.label}>
                <Icon name={item.icon} /><span className={styles.navLabel}>{item.label}</span>
              </RouterLink>
              {active && item.children && <ul className={styles.subList}>
                {item.children.map((child) => <li key={child.id}>
                  <RouterLink to={child.path} className={cn(styles.subItem, child.id === currentChild?.id && styles.subActive)} aria-current={child.id === currentChild?.id ? 'page' : undefined}>{child.label}</RouterLink>
                </li>)}
              </ul>}
            </li>;
          })}
        </ul>
        <div className={styles.railFoot}>
          <RouterLink to="/settings" className={cn(styles.navItem, pathname.startsWith('/settings') && styles.navActive)} aria-label="Settings & billing"><Icon name="settings" /><span className={styles.navLabel}>Settings &amp; billing</span></RouterLink>
          <label className={styles.familiarToggle}>
            <Checkbox aria-label="Show familiar" checked={familiarOn} onChange={(event) => {
              setFamiliarOn(event.target.checked);
              try { localStorage.setItem(FAMILIAR_KEY, event.target.checked ? 'on' : 'off'); } catch { /* storage unavailable */ }
            }} />
            <span className={styles.navLabel}>Show familiar</span>
          </label>
        </div>
      </nav>
      {menuOpen && <div className={styles.scrim} onClick={() => setMenuOpen(false)} aria-hidden="true" />}
      <div className={styles.column}>
        <header className={styles.topbar}>
          <UnstyledButton type="button" className={styles.menuButton} aria-label="Open navigation" aria-expanded={menuOpen} onClick={() => setMenuOpen((open) => !open)}><Icon name="menu" /></UnstyledButton>
          <UnstyledButton type="button" className={styles.searchTrigger} onClick={() => showPalette()} aria-label="Search (Ctrl+K)">
            <Icon name="search" /><span>Search anything…</span><kbd>Ctrl K</kbd>
          </UnstyledButton>
          <div className={styles.account}>{account}</div>
        </header>
        {banners && <div className={styles.banners}>{banners}</div>}
        <main id="main-content" tabIndex={-1} className={styles.main}>{children}</main>
      </div>
      <InspectorDrawer />
      <CommandPalette />
      {familiarOn && familiar}
    </div>
  );
}
