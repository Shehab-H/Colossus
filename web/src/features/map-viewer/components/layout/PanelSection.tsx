import type { ReactNode } from 'react';

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
