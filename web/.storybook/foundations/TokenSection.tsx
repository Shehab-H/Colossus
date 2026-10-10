import type { ReactNode } from 'react';

export function TokenSection({ title, description, children }: { title: string; description?: ReactNode; children: ReactNode }) {
  return (
    <section className="tokens-section">
      <header>
        <h2>{title}</h2>
        {description && <p>{description}</p>}
      </header>
      {children}
    </section>
  );
}

export function TokenPage({ children }: { children: ReactNode }) {
  return <div className="tokens-page">{children}</div>;
}
