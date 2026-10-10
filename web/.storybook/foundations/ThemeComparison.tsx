import { isThemeDependent, type Token } from '../support/cssTokens';
import { TokenPage, TokenSection } from './TokenSection';
import { useTokens } from './useTokens';

const isColor = (token: Token) => token.name.startsWith('--color-');

function ThemedValue({ token, value }: { token: Token; value: string }) {
  return (
    <span className="tokens-themed">
      {isColor(token) && <span className="tokens-swatch__chip tokens-swatch__chip--small" aria-hidden="true"><span style={{ background: value }} /></span>}
      <span className="tokens-table__value">{value}</span>
    </span>
  );
}

export function ThemeComparison() {
  const tokens = useTokens();
  const redeclared = tokens.filter((token) => token.redeclaredInDark);
  const themed = tokens.filter(isThemeDependent);
  const shared = tokens.filter((token) => !isThemeDependent(token));
  const unchanged = redeclared.length - themed.length;
  return (
    <TokenPage>
      <TokenSection title="Tokens that change with the theme" description={`The dark block re-declares ${redeclared.length} tokens; ${themed.length} of them resolve to a different value.`}>
        <table className="tokens-table">
          <caption className="ui-sr-only">Tokens whose value differs between the light and dark themes</caption>
          <thead>
            <tr><th scope="col">Token</th><th scope="col">Light</th><th scope="col">Dark</th></tr>
          </thead>
          <tbody>
            {themed.map((token) => (
              <tr key={token.name}>
                <th scope="row"><code>{token.name}</code></th>
                <td><ThemedValue token={token} value={token.light} /></td>
                <td><ThemedValue token={token} value={token.dark} /></td>
              </tr>
            ))}
          </tbody>
        </table>
      </TokenSection>

      <TokenSection title="Tokens shared by both themes" description={`${shared.length} tokens resolve to the same value in light and dark, including ${unchanged} that the dark block re-declares unchanged.`}>
        <table className="tokens-table">
          <caption className="ui-sr-only">Tokens with the same value in both themes</caption>
          <thead>
            <tr><th scope="col">Token</th><th scope="col">Value</th></tr>
          </thead>
          <tbody>
            {shared.map((token) => (
              <tr key={token.name}>
                <th scope="row"><code>{token.name}</code></th>
                <td className="tokens-table__value">{token.light}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </TokenSection>
    </TokenPage>
  );
}
