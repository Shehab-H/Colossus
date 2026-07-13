import { useState } from 'react';
import type { ChannelSpec, Manifest } from '../lib/manifest';
import type { ViewSummary } from '../lib/views';
import Controls from './Controls';
import { btn, panel } from './controlStyles';

export interface HudProps {
  views: ViewSummary[];
  viewId: string | null;
  onViewChange: (id: string) => void;
  error: string | null;
  colorChannels: ChannelSpec[];
  colorChannel: string;
  onColorChannelChange: (name: string) => void;
  channels: ChannelSpec[];
  options: Record<string, string[]>;
  filters: Record<string, string>;
  onFilterChange: (name: string, value: string) => void;
  manifest: Manifest | null;
  tilesInView: number;
  marksLoaded: number;
  atFullFidelity: boolean;
  getEmbed?: () => { url: string; snippet: string };
}

/** The control/status panel: view picker, measure + filter controls, and the fidelity readout. */
export default function Hud(p: HudProps) {
  const viewIds = p.views.length > 0 ? p.views.map((v) => v.id) : p.viewId ? [p.viewId] : [];
  const [embed, setEmbed] = useState<{ url: string; snippet: string } | null>(null);
  const [copied, setCopied] = useState(false);
  return (
    <div style={panel}>
      <div style={{ fontWeight: 700, marginBottom: 6 }}>Colossus</div>
      <div style={{ display: 'flex', gap: 6, marginBottom: 8, flexWrap: 'wrap' }}>
        {viewIds.map((id) => (
          <button key={id} onClick={() => p.onViewChange(id)} style={id === p.viewId ? btnOn : btn}>
            {id}
          </button>
        ))}
      </div>

      {p.error && <div style={{ color: 'var(--error)', marginBottom: 8, maxWidth: 220 }}>{p.error}</div>}

      <Controls
        colorChannels={p.colorChannels}
        colorChannel={p.colorChannel}
        onColorChannelChange={p.onColorChannelChange}
        channels={p.channels}
        options={p.options}
        filters={p.filters}
        onFilterChange={p.onFilterChange}
      />

      {p.manifest && (
        <div style={{ opacity: 0.85, lineHeight: 1.6, marginTop: 8 }}>
          <div>cells total: {p.manifest.totalPoints.toLocaleString()}</div>
          <div>
            in view: {p.tilesInView} tiles · {p.marksLoaded.toLocaleString()} cells resident
          </div>
          <div style={{ marginTop: 4, fontWeight: 600, color: p.atFullFidelity ? 'var(--good)' : 'var(--warn)' }}>
            {p.atFullFidelity ? '● full fidelity — every cell' : '◐ aggregated — zoom in to resolve'}
          </div>
          <div style={{ opacity: 0.5, fontSize: 11, marginTop: 4 }}>
            {p.manifest.reduction} · {p.manifest.version}
          </div>
        </div>
      )}

      {p.getEmbed && (
        <div style={{ marginTop: 10, borderTop: '1px solid var(--card-border)', paddingTop: 8 }}>
          <button
            style={btn}
            onClick={() => {
              setEmbed((e) => (e ? null : p.getEmbed!()));
              setCopied(false);
            }}
          >
            {embed ? '✕ close embed' : '⧉ embed this map'}
          </button>
          {embed && (
            <div style={{ marginTop: 6 }}>
              <textarea readOnly value={embed.snippet} onFocus={(e) => e.currentTarget.select()} style={code} />
              <div style={{ display: 'flex', gap: 6, marginTop: 4 }}>
                <button
                  style={btn}
                  onClick={() => {
                    navigator.clipboard?.writeText(embed.snippet).then(
                      () => setCopied(true),
                      () => setCopied(false),
                    );
                  }}
                >
                  {copied ? '✓ copied' : 'copy'}
                </button>
                <a href={embed.url} target="_blank" rel="noreferrer" style={{ ...btn, textDecoration: 'none', display: 'inline-block' }}>
                  open ↗
                </a>
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

const btnOn: React.CSSProperties = { ...btn, background: 'var(--btn-on-bg)', color: 'var(--btn-on-fg)', border: '1px solid var(--btn-on-bg)', fontWeight: 700 };
const code: React.CSSProperties = {
  width: '100%',
  height: 64,
  resize: 'vertical',
  padding: '4px 6px',
  background: 'var(--input-bg)',
  color: 'var(--input-fg)',
  border: '1px solid var(--input-border)',
  borderRadius: 4,
  font: '11px ui-monospace, SFMono-Regular, Menlo, monospace',
  whiteSpace: 'pre-wrap',
  wordBreak: 'break-all',
};
