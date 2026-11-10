import { useEffect, useRef, useState, type PointerEvent } from 'react';
import { SIDEBAR_WIDTH, clampSidebarWidth } from '@shared/state';
import { useApp } from '../context';

/**
 * The session list's right edge: drag to resize (kept as a preference when let go), double-click for the normal
 * width, arrow keys in steps. The width is applied as --sidebar-width on `target` while dragging.
 */
export function SidebarResizer({ width }: { width: number }) {
  const { t } = useApp();
  const [dragged, setDragged] = useState<number | null>(null);
  const start = useRef<{ x: number; width: number } | null>(null);
  const shown = dragged ?? width;

  useEffect(() => {
    document.documentElement.style.setProperty('--sidebar-width', `${shown}px`);
  }, [shown]);

  const save = (next: number) => window.reframe.setPreferences({ sidebarWidth: clampSidebarWidth(next) });
  const onPointerDown = (event: PointerEvent<HTMLDivElement>) => {
    if (event.button !== 0) return;
    event.currentTarget.setPointerCapture(event.pointerId);
    start.current = { x: event.clientX, width };
    setDragged(width);
  };
  const onPointerMove = (event: PointerEvent<HTMLDivElement>) => {
    if (start.current) setDragged(clampSidebarWidth(start.current.width + event.clientX - start.current.x));
  };
  const onPointerUp = (event: PointerEvent<HTMLDivElement>) => {
    if (!start.current) return;
    const next = clampSidebarWidth(start.current.width + event.clientX - start.current.x);
    start.current = null;
    setDragged(null);
    if (next !== width) save(next);
  };

  return (
    <div
      className={`sidebar-resizer${dragged !== null ? ' is-dragging' : ''}`}
      role="separator"
      aria-orientation="vertical"
      aria-label={t('list.resize')}
      aria-valuemin={SIDEBAR_WIDTH.min}
      aria-valuemax={SIDEBAR_WIDTH.max}
      aria-valuenow={shown}
      tabIndex={0}
      title={t('list.resize')}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={() => {
        start.current = null;
        setDragged(null);
      }}
      onDoubleClick={() => save(SIDEBAR_WIDTH.normal)}
      onKeyDown={(event) => {
        const step = event.key === 'ArrowLeft' ? -16 : event.key === 'ArrowRight' ? 16 : 0;
        if (!step) return;
        event.preventDefault();
        save(width + step);
      }}
    />
  );
}
