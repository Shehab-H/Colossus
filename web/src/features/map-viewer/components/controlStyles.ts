// Shared form controls. Page and panel composition live in the application shell.

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
