import type { ReactNode } from 'react';

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
