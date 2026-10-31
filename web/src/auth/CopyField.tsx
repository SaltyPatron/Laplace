import { useRef, useState } from 'react';
import { Button, Input, Muted } from '@ui';
import styles from './SettingsView.module.css';

export function CopyField({ label, value, secret = false }: { label: string; value: string; secret?: boolean }) {
  const [visible, setVisible] = useState(!secret);
  const [feedback, setFeedback] = useState('');
  const field = useRef<HTMLInputElement>(null);
  async function copy() {
    try { await navigator.clipboard.writeText(value); setFeedback('Copied to clipboard.'); }
    catch {
      setVisible(true); field.current?.focus(); field.current?.select();
      setFeedback('Clipboard access is unavailable. Select and copy the value from this field.');
    }
  }
  return <div className={styles.copyField}>
    <label className={styles.field}>{label}<Input ref={field} readOnly value={value} type={visible ? 'text' : 'password'} autoComplete="off" spellCheck={false} onFocus={(event) => event.currentTarget.select()} /></label>
    <div className={styles.actions}>
      <Button variant="ghost" onClick={() => void copy()}>Copy {secret ? 'key' : 'link'}</Button>
      {secret && <Button variant="ghost" onClick={() => setVisible((value) => !value)}>{visible ? 'Conceal key' : 'Show key'}</Button>}
    </div>
    {feedback && <Muted role="status">{feedback}</Muted>}
  </div>;
}
