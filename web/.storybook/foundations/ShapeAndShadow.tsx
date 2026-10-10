import { startsWith, tokensMatching } from '../support/cssTokens';
import { SpecimenTable } from './SpecimenTable';
import { TokenPage, TokenSection } from './TokenSection';
import { useTokens } from './useTokens';

export function ShapeAndShadow() {
  const tokens = useTokens();
  return (
    <TokenPage>
      <TokenSection title="Corner radius" description="Radii from subtle control corners to the fully rounded pill used by buttons and badges.">
        <SpecimenTable
          caption="Radius tokens"
          tokens={tokensMatching(tokens, startsWith('--radius-'))}
          specimen={(token) => <span className="tokens-box" style={{ borderRadius: `var(${token.name})` }} />}
        />
      </TokenSection>

      <TokenSection title="Elevation" description="Shadows are re-declared for the dark theme, so the same token reads correctly on both backgrounds.">
        <SpecimenTable
          caption="Shadow tokens"
          tokens={tokensMatching(tokens, startsWith('--shadow-'))}
          specimen={(token) => <span className="tokens-box tokens-box--raised" style={{ boxShadow: `var(${token.name})` }} />}
        />
      </TokenSection>
    </TokenPage>
  );
}
