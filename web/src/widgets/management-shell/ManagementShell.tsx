import type { ReactNode } from 'react';
import { useTheme } from '@shared/theme/useTheme';
import ThemeToggle from '@shared/ui/ThemeToggle';

export default function ManagementShell({ children, actions }: { children: ReactNode; actions?: ReactNode }) {
  const { theme, toggle } = useTheme();
  return (
    <main className="management-shell">
      <a className="management-skip-link" href="#management-content">Skip to content</a>
      <div className="management-brand-rail" aria-hidden="true" />
      <header className="management-header">
        <div className="management-header__inner">
          <a className="management-brand" href="/maps" aria-label="Colossus Maps home">
            <span className="management-brand__mark" aria-hidden="true">C</span>
            <span className="management-brand__copy"><strong>Colossus</strong><small>Maps control plane</small></span>
          </a>
          <div className="management-header__actions">
            {actions}
            <ThemeToggle theme={theme} onToggle={toggle} />
          </div>
        </div>
      </header>
      <div className="management-content" id="management-content">{children}</div>
    </main>
  );
}
