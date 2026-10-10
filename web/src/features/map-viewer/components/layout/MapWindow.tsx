import type { ReactNode } from 'react';

export function MapWindow({ map, status }: { map: ReactNode; status: ReactNode }) {
  return (
    <section className="map-window" aria-label="Map window">
      {map}
      <footer className="viewer-status">{status}</footer>
    </section>
  );
}
