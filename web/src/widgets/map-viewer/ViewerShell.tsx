import type { ReactNode } from 'react';
import { cx } from '@shared/ui';

export function ViewerShell({ controlsOpen, detailsOpen, children }: {
  controlsOpen: boolean;
  detailsOpen: boolean;
  children: ReactNode;
}) {
  return (
    <main className={cx(
      'viewer-shell',
      !controlsOpen && 'viewer-shell--controls-collapsed',
      !detailsOpen && 'viewer-shell--details-collapsed',
    )}>
      {children}
    </main>
  );
}

export function ViewerHeader({ eyebrow, title, release, actions }: {
  eyebrow: ReactNode;
  title: ReactNode;
  release?: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <header className="viewer-header">
      <div>
        <span className="viewer-eyebrow">{eyebrow}</span>
        <h1>{title}</h1>
      </div>
      <div className="viewer-header__actions">
        {release && <span className="viewer-release">{release}</span>}
        {actions}
      </div>
    </header>
  );
}

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

export function PanelSection({ eyebrow, title, children }: {
  eyebrow: ReactNode;
  title: ReactNode;
  children: ReactNode;
}) {
  return (
    <section className="viewer-panel-section">
      <div className="viewer-section-heading">
        <span className="viewer-eyebrow">{eyebrow}</span>
        <h2>{title}</h2>
      </div>
      {children}
    </section>
  );
}

export function MapWindow({ map, status }: { map: ReactNode; status: ReactNode }) {
  return (
    <section className="map-window" aria-label="Map window">
      {map}
      <footer className="viewer-status">{status}</footer>
    </section>
  );
}

export function EmbedShell({ children }: { children: ReactNode }) {
  return <main className="embed-shell">{children}</main>;
}

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

export function EmbedStage({ map, title, status }: { map: ReactNode; title: ReactNode; status: ReactNode }) {
  return (
    <section className="embed-stage">
      {map}
      <footer className="embed-statusbar">
        <div className="embed-identity">
          <strong>{title}</strong>
          <div className="viewer-status">{status}</div>
        </div>
      </footer>
    </section>
  );
}
