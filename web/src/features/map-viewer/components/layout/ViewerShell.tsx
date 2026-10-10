import type { ReactNode } from 'react';
import { cx } from '@/utils/classNames';

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
