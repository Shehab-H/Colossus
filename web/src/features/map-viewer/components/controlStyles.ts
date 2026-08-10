// Shared control-panel styles, used by both the full HUD (chrome) and the slim showcase-embed panel.
// Kept in one place so the embed controls read identically to the app's.

export const panel: React.CSSProperties = {
  position: 'absolute',
  zIndex: 2, // above the deck canvas — MapboxOverlay mounts inside a MapLibre ctrl corner at z-index 2
  top: 12,
  left: 12,
  padding: '10px 12px',
  background: 'var(--card-bg)',
  color: 'var(--card-fg)',
  font: '12px system-ui, sans-serif',
  borderRadius: 8,
  border: '1px solid var(--card-border)',
  boxShadow: 'var(--card-shadow)',
  userSelect: 'none',
  minWidth: 160,
};

export const btn: React.CSSProperties = {
  padding: '4px 8px',
  background: 'var(--btn-bg)',
  color: 'var(--btn-fg)',
  border: '1px solid var(--btn-border)',
  borderRadius: 6,
  cursor: 'pointer',
};

export const btnOn: React.CSSProperties = {
  ...btn,
  background: 'var(--btn-on-bg)',
  color: 'var(--btn-on-fg)',
  border: '1px solid var(--btn-on-bg)',
  fontWeight: 700,
};

export const select: React.CSSProperties = {
  width: '100%',
  padding: '3px 4px',
  background: 'var(--input-bg)',
  color: 'var(--input-fg)',
  border: '1px solid var(--input-border)',
  borderRadius: 4,
};

export const sub: React.CSSProperties = { opacity: 0.6, fontSize: 11 };
