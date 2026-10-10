import { resolvedValue, startsWith, tokensIn, type TokenFamily } from '../support/cssTokens';
import { TokenPage, TokenSection } from './TokenSection';
import { useActiveTheme, useTokens } from './useTokens';

const families: Array<TokenFamily & { description: string }> = [
  { id: 'surface', label: 'Surfaces', description: 'Page, card and raised backgrounds.', match: startsWith('--color-bg-') },
  { id: 'text', label: 'Text', description: 'Foreground colors for body copy and inverse content.', match: startsWith('--color-text-') },
  { id: 'border', label: 'Borders', description: 'Dividers and control outlines.', match: startsWith('--color-border-') },
  { id: 'brand', label: 'Brand', description: 'Primary actions and brand accents.', match: startsWith('--color-brand') },
  { id: 'success', label: 'Success', description: 'Positive state and its tinted backgrounds.', match: startsWith('--color-success') },
  { id: 'warning', label: 'Warning', description: 'Cautionary state and its tinted background.', match: startsWith('--color-warning') },
  { id: 'danger', label: 'Danger', description: 'Errors and destructive actions.', match: startsWith('--color-danger') },
  { id: 'info', label: 'Information', description: 'Neutral status and informational callouts.', match: startsWith('--color-info') },
  { id: 'interaction', label: 'Interaction and overlay', description: 'Focus ring color and the modal scrim.', match: (name) => name === '--color-focus' || name === '--color-overlay' },
];

export function ColorPalette() {
  const tokens = useTokens();
  useActiveTheme();
  return (
    <TokenPage>
      {families.map((family) => (
        <TokenSection key={family.id} title={family.label} description={family.description}>
          <ul className="tokens-swatches">
            {tokensIn(tokens, family).map((token) => (
              <li key={token.name} className="tokens-swatch">
                <span className="tokens-swatch__chip" aria-hidden="true"><span style={{ background: `var(${token.name})` }} /></span>
                <code>{token.name}</code>
                <span className="tokens-swatch__value">{resolvedValue(token.name)}</span>
              </li>
            ))}
          </ul>
        </TokenSection>
      ))}
    </TokenPage>
  );
}
