import type { ReactNode } from 'react';
import { cx } from '@/utils/classNames';

export function ViewerPanel({ side, open, onToggle, label, children }: {
  side: 'left' | 'right';
  open: boolean;
  onToggle: () => void;
  label: string;
  children: ReactNode;
}) {
  const chevron = side === 'left' ? (open ? '‹' : '›') : (open ? '›' : '‹');
  const toggleLabel = `${open ? 'Hide' : 'Show'} ${label.toLowerCase()}`;
  const icon = <span className="viewer-panel__chevron" aria-hidden="true">{chevron}</span>;
  return (
    <aside
      className={cx('viewer-panel', `viewer-panel--${side}`, open ? 'is-open' : 'is-collapsed')}
      aria-label={label}
    >
      <button className="viewer-panel__toggle" type="button" aria-expanded={open} onClick={onToggle}>
        {side === 'left' && icon}
        <span className="viewer-panel__toggle-label">{toggleLabel}</span>
        {side === 'right' && icon}
      </button>
      {open && <div className="viewer-panel__body">{children}</div>}
    </aside>
  );
}
