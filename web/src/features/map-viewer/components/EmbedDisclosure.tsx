import { useState } from 'react';
import { btn } from './controlStyles';

export default function EmbedDisclosure({ getEmbed }: { getEmbed: () => { url: string; snippet: string } }) {
  const [embed, setEmbed] = useState<{ url: string; snippet: string } | null>(null);
  const [copied, setCopied] = useState(false);

  return (
    <details className="viewer-disclosure">
      <summary>Embed this map</summary>
      <p className="viewer-note">A stable, chrome-free iframe URL with this map's current state.</p>
      {!embed ? (
        <button style={btn} onClick={() => setEmbed(getEmbed())}>Generate iframe</button>
      ) : (
        <>
          <textarea className="embed-code" readOnly value={embed.snippet} onFocus={(e) => e.currentTarget.select()} />
          <div className="button-row">
            <button
              style={btn}
              onClick={() => navigator.clipboard?.writeText(embed.snippet).then(() => setCopied(true), () => setCopied(false))}
            >
              {copied ? 'Copied' : 'Copy iframe'}
            </button>
            <a className="button-link" href={embed.url} target="_blank" rel="noreferrer">Open embed</a>
          </div>
        </>
      )}
    </details>
  );
}
