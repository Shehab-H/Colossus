import { useState } from 'react';
import { Button, ButtonLink, Textarea } from '@shared/ui';

export default function EmbedDisclosure({ getEmbed }: { getEmbed: () => { url: string; snippet: string } }) {
  const [embed, setEmbed] = useState<{ url: string; snippet: string } | null>(null);
  const [copied, setCopied] = useState(false);

  return (
    <details className="viewer-disclosure">
      <summary>Embed this map</summary>
      <p className="viewer-note">A stable, chrome-free iframe URL with this map's current state.</p>
      {!embed ? (
        <Button size="sm" onClick={() => setEmbed(getEmbed())}>Generate iframe</Button>
      ) : (
        <>
          <Textarea
            className="embed-code"
            aria-label="Iframe embed code"
            readOnly
            value={embed.snippet}
            onFocus={(event) => event.currentTarget.select()}
          />
          <div className="button-row">
            <Button
              size="sm"
              onClick={() => navigator.clipboard?.writeText(embed.snippet).then(() => setCopied(true), () => setCopied(false))}
            >
              {copied ? 'Copied' : 'Copy iframe'}
            </Button>
            <ButtonLink size="sm" href={embed.url} target="_blank" rel="noreferrer">Open embed</ButtonLink>
          </div>
        </>
      )}
    </details>
  );
}
