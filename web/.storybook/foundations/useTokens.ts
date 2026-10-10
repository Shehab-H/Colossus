import { useMemo, useSyncExternalStore } from 'react';
import { readTokens, type Token } from '../support/cssTokens';

type ActiveTheme = 'light' | 'dark';

function subscribeToTheme(onChange: () => void): () => void {
  const observer = new MutationObserver(onChange);
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
  return () => observer.disconnect();
}

const currentTheme = (): ActiveTheme => (document.documentElement.dataset.theme === 'dark' ? 'dark' : 'light');

export function useActiveTheme(): ActiveTheme {
  return useSyncExternalStore(subscribeToTheme, currentTheme);
}

export function useTokens(): Token[] {
  return useMemo(readTokens, []);
}
