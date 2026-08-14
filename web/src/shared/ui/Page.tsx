import type { ReactNode } from 'react';
import { cx } from './classNames';

export function PageHeader({ eyebrow, title, description, actions, className }: {
  eyebrow?: ReactNode;
  title: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
  className?: string;
}) {
  return (
    <header className={cx('ui-page-header', className)}>
      <div className="ui-page-header__copy">
        {eyebrow && <span className="ui-page-header__eyebrow">{eyebrow}</span>}
        <h1>{title}</h1>
        {description && <p>{description}</p>}
      </div>
      {actions && <div className="ui-page-header__actions">{actions}</div>}
    </header>
  );
}

export function StatCard({ label, value, note, danger = false }: {
  label: ReactNode;
  value: ReactNode;
  note?: ReactNode;
  danger?: boolean;
}) {
  return (
    <div className={cx('ui-stat', danger && 'ui-stat--danger')}>
      <span className="ui-stat__label">{label}</span>
      <strong className="ui-stat__value">{value}</strong>
      {note && <small className="ui-stat__note">{note}</small>}
    </div>
  );
}
