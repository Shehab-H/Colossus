import type { ChannelSpec, Manifest } from '../lib/manifest';
import { ALL } from '../lib/channels';
import type { ViewSummary } from '../lib/views';

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
}

/** The control/status panel: view picker, measure + filter controls, and the fidelity readout. */
export default function Hud(p: HudProps) {
  const viewIds = p.views.length > 0 ? p.views.map((v) => v.id) : p.viewId ? [p.viewId] : [];
  return (
    <div style={hud}>
      <div style={{ fontWeight: 700, marginBottom: 6 }}>Colossus</div>
      <div style={{ display: 'flex', gap: 6, marginBottom: 8, flexWrap: 'wrap' }}>
        {viewIds.map((id) => (
          <button key={id} onClick={() => p.onViewChange(id)} style={id === p.viewId ? btnOn : btn}>
            {id}
          </button>
        ))}
      </div>

      {p.error && <div style={{ color: 'var(--error)', marginBottom: 8, maxWidth: 220 }}>{p.error}</div>}

      {p.colorChannels.length > 0 && (
        <div style={{ marginBottom: 6 }}>
          <label style={{ opacity: 0.7, display: 'block', fontSize: 11 }}>color by</label>
          <select value={p.colorChannel} onChange={(e) => p.onColorChannelChange(e.target.value)} style={select}>
            {p.colorChannels.map((c) => (
              <option key={c.name} value={c.name}>
                {c.name}
              </option>
            ))}
          </select>
        </div>
      )}

      {p.channels.map((ch) => {
        const opts = p.options[ch.name] ?? [];
        return (
          <div key={ch.name} style={{ marginBottom: 6 }}>
            <label style={{ opacity: 0.7, display: 'block', fontSize: 11 }}>{ch.name}</label>
            {ch.role === 'temporal' ? (
              <input
                type="date"
                value={p.filters[ch.name] && p.filters[ch.name] !== ALL ? p.filters[ch.name] : ''}
                min={opts[0]}
                max={opts[opts.length - 1]}
                onChange={(e) => p.onFilterChange(ch.name, e.target.value || ALL)}
                style={select}
              />
            ) : (
              <select
                value={p.filters[ch.name] ?? ALL}
                onChange={(e) => p.onFilterChange(ch.name, e.target.value)}
                style={select}
              >
                <option value={ALL}>{ALL}</option>
                {opts.map((v) => (
                  <option key={v} value={v}>
                    {v}
                  </option>
                ))}
              </select>
            )}
          </div>
        );
      })}

      {p.manifest && (
        <div style={{ opacity: 0.85, lineHeight: 1.6, marginTop: 8 }}>
          <div>cells total: {p.manifest.totalPoints.toLocaleString()}</div>
          <div>
            in view: {p.tilesInView} tiles · {p.marksLoaded.toLocaleString()} cells
          </div>
          <div style={{ marginTop: 4, fontWeight: 600, color: p.atFullFidelity ? 'var(--good)' : 'var(--warn)' }}>
            {p.atFullFidelity ? '● full fidelity — every cell' : '◐ aggregated — zoom in to resolve'}
          </div>
          <div style={{ opacity: 0.5, fontSize: 11, marginTop: 4 }}>
            {p.manifest.reduction} · {p.manifest.version}
          </div>
        </div>
      )}
    </div>
  );
}

const hud: React.CSSProperties = {
  position: 'absolute',
  top: 12,
  left: 12,
  padding: '10px 12px',
  background: 'var(--card-bg)',
  color: 'var(--card-fg)',
  font: '12px system-ui, sans-serif',
  borderRadius: 8,
  border: '1px solid var(--card-border)',
  boxShadow: 'var(--card-shadow)',
  userSelect: 'none',
  minWidth: 160,
};
const btn: React.CSSProperties = {
  padding: '4px 8px',
  background: 'var(--btn-bg)',
  color: 'var(--btn-fg)',
  border: '1px solid var(--btn-border)',
  borderRadius: 6,
  cursor: 'pointer',
};
const btnOn: React.CSSProperties = { ...btn, background: 'var(--btn-on-bg)', color: 'var(--btn-on-fg)', border: '1px solid var(--btn-on-bg)', fontWeight: 700 };
const select: React.CSSProperties = {
  width: '100%',
  padding: '3px 4px',
  background: 'var(--input-bg)',
  color: 'var(--input-fg)',
  border: '1px solid var(--input-border)',
  borderRadius: 4,
};
