import type { ChannelSpec, FilterSpec } from '@domain/map/manifest';
import { ALL, makeDateRange, parseDateRange } from '@features/map-viewer/engine/channels';
import { makeNumericRange } from '@features/map-viewer/engine/dates';
import { Field, Input, Select } from '@shared/ui';

export interface ControlsProps {
  colorChannels: ChannelSpec[];
  colorChannel: string;
  onColorChannelChange: (name: string) => void;
  channels: ChannelSpec[];
  controls: Record<string, FilterSpec['control'] | null | undefined>;
  options: Record<string, string[]>;
  filters: Record<string, string>;
  onFilterChange: (name: string, value: string) => void;
}

/** The color selector plus one design-system control per filterable channel. */
export default function Controls(p: ControlsProps) {
  return (
    <div className="viewer-controls">
      {p.colorChannels.length > 0 && (
        <Field label="Color by" className="viewer-controls__field">
          <Select value={p.colorChannel} onChange={(event) => p.onColorChannelChange(event.target.value)}>
            {p.colorChannels.map((channel) => (
              <option key={channel.name} value={channel.name}>{channel.name}</option>
            ))}
          </Select>
        </Field>
      )}

      {p.channels.map((channel) => {
        const options = p.options[channel.name] ?? [];
        const control = p.controls[channel.name];
        if (control === 'dateRange' || control === 'range') {
          return (
            <fieldset className="viewer-controls__field viewer-controls__fieldset" key={channel.name}>
              <legend>{channel.name}</legend>
              {control === 'range'
                ? <NumericRange name={channel.name} value={p.filters[channel.name]} min={options[0]} max={options[options.length - 1]} onChange={p.onFilterChange} />
                : <DateRange name={channel.name} value={p.filters[channel.name]} min={options[0]} max={options[options.length - 1]} onChange={p.onFilterChange} />}
            </fieldset>
          );
        }

        return (
          <Field label={channel.name} className="viewer-controls__field" key={channel.name}>
            <Select
              value={p.filters[channel.name] ?? ALL}
              onChange={(event) => p.onFilterChange(channel.name, event.target.value)}
            >
              <option value={ALL}>{ALL}</option>
              {options.map((value) => <option key={value} value={value}>{value}</option>)}
            </Select>
          </Field>
        );
      })}
    </div>
  );
}

/** The numeric counterpart of DateRange. It deliberately uses the same `from..to` route value, so
 *  filter state, embeds, GPU predicates, and reducer fold context all share one representation. */
function NumericRange(p: {
  name: string;
  value: string | undefined;
  min?: string;
  max?: string;
  onChange: (name: string, value: string) => void;
}) {
  const { from, to } = parseDateRange(p.value) ?? { from: '', to: '' };
  return (
    <div className="viewer-date-range">
      <label htmlFor={`${p.name}-from`}>From</label>
      <Input
        id={`${p.name}-from`}
        type="number"
        step="any"
        value={from}
        min={p.min}
        max={to || p.max}
        onChange={(event) => p.onChange(p.name, makeNumericRange(event.target.value, to))}
      />
      <label htmlFor={`${p.name}-to`}>To</label>
      <Input
        id={`${p.name}-to`}
        type="number"
        step="any"
        value={to}
        min={from || p.min}
        max={p.max}
        onChange={(event) => p.onChange(p.name, makeNumericRange(from, event.target.value))}
      />
    </div>
  );
}

/** A bounded from/to picker stored as the viewer's existing `from..to` route value. */
function DateRange(p: {
  name: string;
  value: string | undefined;
  min?: string;
  max?: string;
  onChange: (name: string, value: string) => void;
}) {
  const { from, to } = parseDateRange(p.value) ?? { from: '', to: '' };
  return (
    <div className="viewer-date-range">
      <label htmlFor={`${p.name}-from`}>From</label>
      <Input
        id={`${p.name}-from`}
        type="date"
        value={from}
        min={p.min}
        max={to || p.max}
        onChange={(event) => p.onChange(p.name, makeDateRange(event.target.value, to))}
      />
      <label htmlFor={`${p.name}-to`}>To</label>
      <Input
        id={`${p.name}-to`}
        type="date"
        value={to}
        min={from || p.min}
        max={p.max}
        onChange={(event) => p.onChange(p.name, makeDateRange(from, event.target.value))}
      />
    </div>
  );
}
