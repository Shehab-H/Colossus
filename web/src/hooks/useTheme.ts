import { useCallback, useEffect, useState } from 'react';
import type { Theme } from '@/types/theme';

const KEY = 'colossus-theme';

/** Read the initial theme: an explicit ?theme= (embed override) wins, then a saved choice, then the
 * OS preference, then the light product theme. */
function initialTheme(): Theme {
  if (typeof window === 'undefined') return 'light';
  const url = new URLSearchParams(window.location.search).get('theme');
  if (url === 'dark' || url === 'light') return url;
  const saved = localStorage.getItem(KEY);
  if (saved === 'dark' || saved === 'light') return saved;
  if (window.matchMedia?.('(prefers-color-scheme: dark)').matches) return 'dark';
  return 'light';
}

/** App theme: stamps `data-theme` on <html> so the CSS variables flip, and persists the choice. */
export function useTheme() {
  const [theme, setTheme] = useState<Theme>(initialTheme);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    try {
      localStorage.setItem(KEY, theme);
    } catch {
      // private mode / disabled storage — the data-theme attribute still applies for this session
    }
  }, [theme]);

  const toggle = useCallback(() => setTheme((t) => (t === 'dark' ? 'light' : 'dark')), []);
  return { theme, setTheme, toggle };
}
