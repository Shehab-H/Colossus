export interface Token {
  name: string;
  light: string;
  dark: string;
  redeclaredInDark: boolean;
}

export interface TokenFamily {
  id: string;
  label: string;
  match: (name: string) => boolean;
}

const LIGHT_SELECTOR = ':root';
const DARK_SELECTOR = ':root[data-theme="dark"]';

function rulesOf(sheet: CSSStyleSheet): CSSRule[] {
  try {
    return Array.from(sheet.cssRules);
  } catch {
    return [];
  }
}

function declarationsOf(selector: string): Map<string, string> {
  const declared = new Map<string, string>();
  for (const sheet of Array.from(document.styleSheets)) {
    for (const rule of rulesOf(sheet)) {
      if (!(rule instanceof CSSStyleRule) || rule.selectorText !== selector) continue;
      for (const property of Array.from(rule.style)) {
        if (property.startsWith('--')) declared.set(property, rule.style.getPropertyValue(property).trim());
      }
    }
  }
  return declared;
}

export function readTokens(): Token[] {
  const light = declarationsOf(LIGHT_SELECTOR);
  const dark = declarationsOf(DARK_SELECTOR);
  return [...new Set([...light.keys(), ...dark.keys()])].map((name) => {
    const lightValue = light.get(name) ?? 'unset';
    return { name, light: lightValue, dark: dark.get(name) ?? lightValue, redeclaredInDark: dark.has(name) };
  });
}

export function resolvedValue(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

export function tokensIn(tokens: Token[], family: TokenFamily): Token[] {
  return tokens.filter((token) => family.match(token.name));
}

export function tokensMatching(tokens: Token[], match: TokenFamily['match']): Token[] {
  return tokensIn(tokens, { id: 'group', label: 'Group', match });
}

export function startsWith(prefix: string): TokenFamily['match'] {
  return (name) => name.startsWith(prefix);
}

export function isThemeDependent(token: Token): boolean {
  return token.light !== token.dark;
}
