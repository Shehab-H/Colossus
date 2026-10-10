import type { HTMLAttributes, ReactNode } from 'react';
import { cx } from '@/utils/classNames';

export function Alert({ tone = 'info', className, ...props }: HTMLAttributes<HTMLDivElement> & { tone?: 'info' | 'warning' | 'danger' }) {
  return <div className={cx('ui-alert', `ui-alert--${tone}`, className)} role={tone === 'danger' ? 'alert' : 'status'} {...props} />;
}

export function LoadingState({ children = 'Loading…' }: { children?: ReactNode }) {
  return <div className="ui-loading-state" aria-live="polite">{children}</div>;
}

export function EmptyState({ eyebrow, title, description, action }: {
  eyebrow?: ReactNode;
  title: ReactNode;
  description: ReactNode;
  action?: ReactNode;
}) {
  return (
    <section className="ui-empty-state">
      {eyebrow && <span className="ui-page-header__eyebrow">{eyebrow}</span>}
      <h2>{title}</h2>
      <p>{description}</p>
      {action}
    </section>
  );
}
