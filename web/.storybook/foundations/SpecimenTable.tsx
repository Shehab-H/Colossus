import type { ReactNode } from 'react';
import { resolvedValue, type Token } from '../support/cssTokens';
import { useActiveTheme } from './useTokens';

export function SpecimenTable({ caption, tokens, specimen }: {
  caption: string;
  tokens: Token[];
  specimen?: (token: Token) => ReactNode;
}) {
  useActiveTheme();
  return (
    <table className="tokens-table">
      <caption className="ui-sr-only">{caption}</caption>
      <thead>
        <tr>
          <th scope="col">Token</th>
          <th scope="col">Value</th>
          {specimen && <th scope="col">Specimen</th>}
        </tr>
      </thead>
      <tbody>
        {tokens.map((token) => (
          <tr key={token.name}>
            <th scope="row"><code>{token.name}</code></th>
            <td className="tokens-table__value">{resolvedValue(token.name)}</td>
            {specimen && <td>{specimen(token)}</td>}
          </tr>
        ))}
      </tbody>
    </table>
  );
}
