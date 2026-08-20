import { formatValue, type Legend } from '@domain/map/colorScale';
import { Card } from '@shared/ui';

const rgb = ([red, green, blue]: [number, number, number]) => `rgb(${red},${green},${blue})`;

/** The active color scale, presented as either a continuous gradient or labelled swatches. */
export default function LegendBox({ legend }: { legend: Legend }) {
  return (
    <Card className="map-legend" data-testid="map-legend">
      <div className="map-legend__header">
        <strong>{legend.channel}</strong>
        <span>{legend.note}</span>
      </div>

      {legend.kind === 'continuous' && legend.gradient && (
        <>
          <div
            className="map-legend__gradient"
            style={{ background: `linear-gradient(to right, ${legend.gradient.map(rgb).join(',')})` }}
          />
          <div className="map-legend__axis">
            <span>{legend.minClamped ? '≤ ' : ''}{formatValue(legend.min ?? 0)}</span>
            {legend.midpoint !== undefined && <span>{formatValue(legend.midpoint)}</span>}
            <span>{formatValue(legend.max ?? 1)}{legend.maxClamped ? '+' : ''}</span>
          </div>
        </>
      )}

      {legend.kind !== 'continuous' && legend.items && (
        <div className="map-legend__items">
          {legend.items.map((item, index) => (
            <div key={`${item.label}-${index}`} className="map-legend__item">
              <span className="map-legend__swatch" style={{ background: rgb(item.color) }} />
              <span>{item.label}</span>
            </div>
          ))}
          {legend.more ? <div className="map-legend__more">+{legend.more} more</div> : null}
        </div>
      )}
    </Card>
  );
}
