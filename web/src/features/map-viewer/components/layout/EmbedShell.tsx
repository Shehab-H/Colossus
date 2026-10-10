import type { ReactNode } from 'react';

export function EmbedShell({ children }: { children: ReactNode }) {
  return <main className="embed-shell">{children}</main>;
}
