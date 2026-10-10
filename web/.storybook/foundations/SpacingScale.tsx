import { startsWith, tokensMatching } from '../support/cssTokens';
import { SpecimenTable } from './SpecimenTable';
import { TokenPage, TokenSection } from './TokenSection';
import { useTokens } from './useTokens';

const isLayoutDimension = (name: string) => name === '--content-max' || name === '--header-height';

export function SpacingScale() {
  const tokens = useTokens();
  return (
    <TokenPage>
      <TokenSection title="Space scale" description="Gaps, padding and margins come from this scale; components reference the tokens rather than literal lengths.">
        <SpecimenTable
          caption="Space tokens"
          tokens={tokensMatching(tokens, startsWith('--space-'))}
          specimen={(token) => <span className="tokens-bar" style={{ width: `var(${token.name})` }} />}
        />
      </TokenSection>

      <TokenSection title="Control heights" description="Minimum heights of buttons and form controls at each size.">
        <SpecimenTable
          caption="Control height tokens"
          tokens={tokensMatching(tokens, startsWith('--control-height-'))}
          specimen={(token) => <span className="tokens-control" style={{ height: `var(${token.name})` }} />}
        />
      </TokenSection>

      <TokenSection title="Layout dimensions" description="Maximum content width and the management header height.">
        <SpecimenTable
          caption="Layout dimension tokens"
          tokens={tokensMatching(tokens, isLayoutDimension)}
          specimen={(token) => <span className="tokens-bar tokens-bar--fluid" style={{ width: `var(${token.name})` }} />}
        />
      </TokenSection>
    </TokenPage>
  );
}
