import type { Theme } from '../engine/theme';

/** Small dark/light switch for the application header. Shows the theme you'll switch to. */
export default function ThemeToggle({ theme, onToggle }: { theme: Theme; onToggle: () => void }) {
  const next = theme === 'dark' ? 'light' : 'dark';
  return (
    <button style={btn} onClick={onToggle} title={`Switch to ${next} mode`} aria-label={`Switch to ${next} mode`}>
      {theme === 'dark' ? '☀' : '☾'}
    </button>
  );
}

const btn: React.CSSProperties = {
  width: 32,
  height: 32,
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
  background: 'var(--card-bg)',
  color: 'var(--card-fg)',
  border: '1px solid var(--card-border)',
  borderRadius: 8,
  cursor: 'pointer',
  fontSize: 16,
  lineHeight: 1,
  padding: 0,
};
