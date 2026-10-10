import type { ReactNode } from 'react';

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
