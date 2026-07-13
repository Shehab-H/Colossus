import type { ChannelSpec } from '../lib/manifest';
import { ALL, makeDateRange, parseDateRange } from '../lib/channels';
import { select, sub } from './controlStyles';

export interface ControlsProps {
  colorChannels: ChannelSpec[];
  colorChannel: string;
  onColorChannelChange: (name: string) => void;
  channels: ChannelSpec[];
  options: Record<string, string[]>;
  filters: Record<string, string>;
  onFilterChange: (name: string, value: string) => void;
}

/** The color-by selector + one control per filterable channel. Shared by the full HUD and the slim
 *  showcase-embed panel, so an embedded map filters exactly like the app — just without the dataset
 *  tabs, branding, stats, or embed button. */
export default function Controls(p: ControlsProps) {
  return (
    <>
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
              <DateRange
                name={ch.name}
                value={p.filters[ch.name]}
                min={opts[0]}
                max={opts[opts.length - 1]}
                onChange={p.onFilterChange}
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
    </>
  );
}

/** A from/to date range for a temporal channel. The selection is stored as one `from..to` string (see
 *  makeDateRange), so the rest of the filter plumbing treats it like any other channel value. Each picker
 *  clamps to the data extent and to the other bound, so from can't exceed to. */
function DateRange(p: {
  name: string;
  value: string | undefined;
  min?: string;
  max?: string;
  onChange: (name: string, value: string) => void;
}) {
  const { from, to } = parseDateRange(p.value) ?? { from: '', to: '' };
  return (
    <div style={{ display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '4px 6px', alignItems: 'center' }}>
      <span style={sub}>from</span>
      <input
        type="date"
        aria-label={`${p.name} from`}
        value={from}
        min={p.min}
        max={to || p.max}
        onChange={(e) => p.onChange(p.name, makeDateRange(e.target.value, to))}
        style={select}
      />
      <span style={sub}>to</span>
      <input
        type="date"
        aria-label={`${p.name} to`}
        value={to}
        min={from || p.min}
        max={p.max}
        onChange={(e) => p.onChange(p.name, makeDateRange(from, e.target.value))}
        style={select}
      />
    </div>
  );
}
