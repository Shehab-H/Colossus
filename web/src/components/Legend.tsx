import { formatValue, type Legend } from '../lib/colorScale';

const rgb = ([r, g, b]: [number, number, number]) => `rgb(${r},${g},${b})`;

/** The color legend: every map with a color-by shows one, stating the channel and how it's colored —
 *  a gradient bar for continuous/heat scales, labelled swatches for binned and categorical. */
export default function LegendBox({ legend }: { legend: Legend }) {
  return (
    <div style={box}>
      <div style={head}>
        <span style={{ fontWeight: 700 }}>{legend.channel}</span>
        <span style={note}>{legend.note}</span>
      </div>

      {legend.kind === 'continuous' && legend.gradient && (
        <>
          <div style={{ ...bar, background: `linear-gradient(to right, ${legend.gradient.map(rgb).join(',')})` }} />
          <div style={axis}>
            <span>{formatValue(legend.min ?? 0)}</span>
            {legend.midpoint !== undefined && <span>{formatValue(legend.midpoint)}</span>}
            <span>{formatValue(legend.max ?? 1)}</span>
          </div>
        </>
      )}

      {legend.kind !== 'continuous' && legend.items && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
          {legend.items.map((it, i) => (
            <div key={`${it.label}-${i}`} style={row}>
              <span style={{ ...swatch, background: rgb(it.color) }} />
              <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{it.label}</span>
            </div>
          ))}
          {legend.more ? <div style={{ opacity: 0.6, marginTop: 2 }}>+{legend.more} more</div> : null}
        </div>
      )}
    </div>
  );
}

const box: React.CSSProperties = {
  position: 'absolute',
  bottom: 12,
  left: 12,
  padding: '8px 10px',
  background: 'var(--card-bg)',
  color: 'var(--card-fg)',
  font: '12px system-ui, sans-serif',
  borderRadius: 8,
  border: '1px solid var(--card-border)',
  boxShadow: 'var(--card-shadow)',
  userSelect: 'none',
  minWidth: 140,
  maxWidth: 220,
};
const head: React.CSSProperties = { display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', gap: 10, marginBottom: 6 };
const note: React.CSSProperties = { color: 'var(--card-muted)', fontSize: 10, textTransform: 'uppercase', letterSpacing: 0.4 };
const bar: React.CSSProperties = { height: 10, borderRadius: 3, border: '1px solid var(--swatch-border)' };
const axis: React.CSSProperties = { display: 'flex', justifyContent: 'space-between', marginTop: 3, color: 'var(--card-muted)', fontVariantNumeric: 'tabular-nums' };
const row: React.CSSProperties = { display: 'flex', alignItems: 'center', gap: 6 };
const swatch: React.CSSProperties = { width: 12, height: 12, borderRadius: 3, flex: '0 0 auto', border: '1px solid var(--swatch-border)' };
