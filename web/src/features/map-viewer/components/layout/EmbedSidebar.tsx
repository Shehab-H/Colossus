import type { ReactNode } from 'react';
import { cx } from '@/utils/classNames';

export function EmbedSidebar({ side, open, onToggle, label, children }: {
  side: 'left' | 'right';
  open: boolean;
  onToggle: () => void;
  label: string;
  children: ReactNode;
}) {
  const chevron = side === 'left' ? (open ? '‹' : '›') : (open ? '›' : '‹');
  const icon = <span className="embed-sidebar__chevron" aria-hidden="true">{chevron}</span>;
  return (
    <aside
      className={cx('embed-sidebar', `embed-sidebar--${side}`, open ? 'is-open' : 'is-collapsed')}
      aria-label={label}
    >
      <button className="embed-sidebar__toggle" type="button" aria-expanded={open} onClick={onToggle}>
        {side === 'left' && icon}
        <span className="embed-sidebar__label">{label}</span>
        {side === 'right' && icon}
      </button>
      {open && <div className="embed-sidebar__body">{children}</div>}
    </aside>
  );
}
