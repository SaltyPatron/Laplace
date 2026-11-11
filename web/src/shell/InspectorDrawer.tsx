import { lazy, Suspense, useEffect, useRef } from 'react';
import { LoadingText } from '../ui/primitives/Text';
import { Icon } from '../ui/icons';
import { UnstyledButton } from '../ui/primitives/UnstyledButton';
import { useEntityLabels, useInspector } from './inspector';
import styles from './InspectorDrawer.module.css';

const SubjectView = lazy(() => import('../lenses/SubjectView').then((m) => ({ default: m.SubjectView })));

/** Right-hand drawer: the subject under inspection, from anywhere, without leaving the page. */
export function InspectorDrawer() {
  const { current, close, pin } = useInspector();
  const label = useEntityLabels((state) => (current ? state.labels[current] : undefined));
  const panel = useRef<HTMLElement>(null);

  useEffect(() => {
    if (!current) return;
    panel.current?.focus();
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape' && !document.querySelector('[role="dialog"][aria-modal="true"]')) close(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [current, close]);

  if (!current) return null;
  return (
    <aside ref={panel} tabIndex={-1} className={styles.drawer} aria-label={`Inspector: ${label ?? current}`} data-inspector>
      <header className={styles.head}>
        <div className={styles.title}>
          <span className={styles.eyebrow}>Inspecting</span>
          <h2>{label ?? current.slice(0, 12)}</h2>
        </div>
        <UnstyledButton type="button" className={styles.iconButton} onClick={pin} title="Open as a full page" aria-label="Open as a full page"><Icon name="external" /></UnstyledButton>
        <UnstyledButton type="button" className={styles.iconButton} onClick={close} title="Close (Esc)" aria-label="Close inspector"><Icon name="close" /></UnstyledButton>
      </header>
      <div className={styles.body}>
        <Suspense fallback={<LoadingText>Opening…</LoadingText>}>
          <SubjectView key={current} id={current} />
        </Suspense>
      </div>
    </aside>
  );
}
