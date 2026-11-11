/** Board pointer-interaction state for the play view: selection, drag ghost, marks and user arrows. */
import { useEffect, useRef, useState } from 'react';

import { useBoardRef } from './Board';

export function useBoardInteraction() {
  const [sel, setSel] = useState<string | null>(null);
  const [marks, setMarks] = useState<Set<string>>(new Set());
  const [userArrows, setUserArrows] = useState<{ from: string; to: string }[]>([]);
  const [drag, setDrag] = useState<{ from: string; piece: string; x: number; y: number } | null>(null);
  const rdrag = useRef<string | null>(null);
  const boardRef = useBoardRef();

  useEffect(() => {
    const move = (e: PointerEvent) => setDrag((d) => (d ? { ...d, x: e.clientX, y: e.clientY } : d));
    const up = () => { setDrag(null); rdrag.current = null; };
    window.addEventListener('pointermove', move);
    window.addEventListener('pointerup', up);
    return () => { window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', up); };
  }, []);

  return { sel, setSel, marks, setMarks, userArrows, setUserArrows, drag, setDrag, rdrag, boardRef };
}

export type BoardInteraction = ReturnType<typeof useBoardInteraction>;
