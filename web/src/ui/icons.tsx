/** A small, consistent icon set (24px grid, 1.75 stroke, currentColor). */
const PATHS = {
  search: 'M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14Zm9 16-4.35-4.35',
  compare: 'M8 3v18M16 3v18M3 8h5M16 16h5M3 16h5M16 8h5',
  globe: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18Zm-9 9h18M12 3c2.5 2.6 3.8 5.6 3.8 9s-1.3 6.4-3.8 9c-2.5-2.6-3.8-5.6-3.8-9S9.5 5.6 12 3Z',
  chat: 'M4 5h16v11H9l-5 4V5Z',
  upload: 'M12 16V4m0 0-5 5m5-5 5 5M4 16v4h16v-4',
  chess: 'M9 21h6M8 18h8l-1-6h-6l-1 6Zm2-6V9h4v3M12 3v3m-2-1.5h4',
  settings: 'M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6Zm8 3-2 .7-.6 1.5 1 1.9-1.4 1.4-1.9-1-1.5.6L13 20h-2l-.7-2-1.5-.6-1.9 1-1.4-1.4 1-1.9-.6-1.5L4 13v-2l2-.7.6-1.5-1-1.9 1.4-1.4 1.9 1 1.5-.6L11 4h2l.7 2 1.5.6 1.9-1 1.4 1.4-1 1.9.6 1.5 2 .7v2Z',
  menu: 'M4 7h16M4 12h16M4 17h16',
  close: 'M6 6l12 12M18 6 6 18',
  pin: 'M14 4l6 6-3 1-4 4 1 4-2 1-3-4-5 5m5-5-4-3 1-2 4 1 4-4 1-3Z',
  external: 'M14 4h6v6m0-6-9 9M18 14v6H4V6h6',
  chevron: 'm9 6 6 6-6 6',
  info: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18Zm0 8v5m0-8h.01',
} as const;

export type IconName = keyof typeof PATHS;

export function Icon({ name, size = 20, label }: { name: IconName; size?: number; label?: string }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.75}
      strokeLinecap="round" strokeLinejoin="round" role={label ? 'img' : undefined} aria-label={label} aria-hidden={label ? undefined : true}
      focusable="false">
      <path d={PATHS[name]} />
    </svg>
  );
}
