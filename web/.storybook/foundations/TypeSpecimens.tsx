import { startsWith, tokensMatching } from '../support/cssTokens';
import { SpecimenTable } from './SpecimenTable';
import { TokenPage, TokenSection } from './TokenSection';
import { useTokens } from './useTokens';

const sample = 'Map releases publish immutable tiles';
const isFontFamily = (name: string) => name === '--font-sans' || name === '--font-mono';

export function TypeSpecimens() {
  const tokens = useTokens();
  return (
    <TokenPage>
      <TokenSection title="Font families" description="The sans family is the interface face; the mono family is used for identifiers, queries and code.">
        <SpecimenTable
          caption="Font family tokens"
          tokens={tokensMatching(tokens, isFontFamily)}
          specimen={(token) => <span style={{ fontFamily: `var(${token.name})` }}>{sample}</span>}
        />
      </TokenSection>

      <TokenSection title="Type scale" description="Sizes used by headings, body copy and dense tables. The largest step is fluid.">
        <SpecimenTable
          caption="Font size tokens"
          tokens={tokensMatching(tokens, startsWith('--font-size-'))}
          specimen={(token) => <span style={{ fontSize: `var(${token.name})` }}>{sample}</span>}
        />
      </TokenSection>

      <TokenSection title="Font weights" description="Named weights; the interface relies on semibold and bold for hierarchy.">
        <SpecimenTable
          caption="Font weight tokens"
          tokens={tokensMatching(tokens, startsWith('--font-weight-'))}
          specimen={(token) => <span style={{ fontWeight: `var(${token.name})` }}>{sample}</span>}
        />
      </TokenSection>

      <TokenSection title="Line height" description="Tight leading for headings, body leading for running text.">
        <SpecimenTable
          caption="Line height tokens"
          tokens={tokensMatching(tokens, startsWith('--line-height-'))}
          specimen={(token) => <p className="tokens-paragraph" style={{ lineHeight: `var(${token.name})` }}>{sample}. {sample}. {sample}.</p>}
        />
      </TokenSection>
    </TokenPage>
  );
}
