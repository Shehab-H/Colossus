import type { ReactNode } from 'react';
import type { CatalogDataset } from '@entities/catalog';
import type { MapFormState } from '../model/mapForm';
import { Alert, Button, Field, Input, Select, Textarea } from '@shared/ui';

interface FilterConfig {
  channel: string;
  control: 'select' | 'dateRange';
  default?: string;
}

interface MeasureConfig {
  name: string;
  expr: string;
}

interface ColorConfig {
  channel: string;
  type?: string;
  scheme?: string;
  [key: string]: unknown;
}

interface EncodingConfig {
  color?: ColorConfig;
  [key: string]: unknown;
}

interface InspectConfig {
  channels: string[];
  title?: string;
}

const scaleTypes = ['', 'linear', 'log', 'sqrt', 'diverging', 'quantize', 'quantile', 'threshold', 'ordinal', 'categorical'];
const schemes = ['', 'viridis', 'plasma', 'magma', 'inferno', 'cividis', 'turbo', 'blues', 'greens', 'oranges', 'greys', 'blueRed', 'redBlue', 'blueOrange', 'purpleGreen', 'spectral', 'okabeIto', 'category', 'status'];

export function MapConfigurationBuilder({
  form,
  dataset,
  errors,
  onChange,
}: {
  form: MapFormState;
  dataset?: CatalogDataset;
  errors: Record<string, string[]>;
  onChange: <K extends keyof MapFormState>(field: K, value: MapFormState[K]) => void;
}) {
  const channels = dataset?.draft.source.channels ?? [];
  const filterable = channels.filter((channel) => channel.role === 'dimension' || channel.role === 'temporal');
  const automaticFilters = !form.filters.trim();
  const filters = readArray<FilterConfig>(form.filters);
  const effectiveFilters = automaticFilters
    ? filterable.map((channel) => ({ channel: channel.name, control: channel.role === 'temporal' ? 'dateRange' : 'select' }) as FilterConfig)
    : filters;
  const measures = readArray<MeasureConfig>(form.measures);
  const encoding = readObject<EncodingConfig>(form.encoding);
  const inspect = readObject<InspectConfig>(form.inspect);
  const bakePredicates = readArray<string>(form.bakeFilters);
  const availableChannels = [...channels.map((channel) => channel.name), ...measures.map((measure) => measure.name).filter(Boolean)];
  const keyedGeometry = dataset?.draft.source.geometry.kind === 'quadkey';

  const write = <K extends 'bakeFilters' | 'filters' | 'measures' | 'encoding' | 'inspect'>(field: K, value: unknown, empty = false) => {
    onChange(field, (empty ? '' : JSON.stringify(value, null, 2)) as MapFormState[K]);
  };

  const setFilterEnabled = (channelName: string, enabled: boolean) => {
    const channel = filterable.find((candidate) => candidate.name === channelName);
    const next = effectiveFilters.filter((filter) => filter.channel !== channelName);
    if (enabled && channel) next.push({ channel: channelName, control: channel.role === 'temporal' ? 'dateRange' : 'select' });
    write('filters', next);
  };

  const updateFilter = (channelName: string, patch: Partial<FilterConfig>) => {
    write('filters', effectiveFilters.map((filter) => filter.channel === channelName ? { ...filter, ...patch } : filter));
  };

  const updateMeasure = (index: number, patch: Partial<MeasureConfig>) => {
    write('measures', measures.map((measure, candidate) => candidate === index ? { ...measure, ...patch } : measure));
  };

  const updateColor = (patch: Partial<ColorConfig>, remove: (keyof ColorConfig)[] = []) => {
    const current = { ...(encoding.color ?? { channel: '' }), ...patch };
    for (const key of remove) delete current[key];
    const next = { ...encoding };
    if (current.channel) next.color = current;
    else delete next.color;
    write('encoding', next, Object.keys(next).length === 0);
  };

  const setInspectChannel = (channel: string, enabled: boolean) => {
    const current = inspect.channels ?? [];
    const nextChannels = enabled ? [...new Set([...current, channel])] : current.filter((name) => name !== channel);
    if (nextChannels.length === 0) write('inspect', undefined, true);
    else write('inspect', { ...inspect, channels: nextChannels, title: nextChannels.includes(inspect.title ?? '') ? inspect.title : undefined });
  };

  if (!dataset) {
    return <Alert>Select a dataset to configure filters, measures, encoding, and inspection.</Alert>;
  }

  return (
    <div className="maps-config">
      <section className="maps-config__section">
        <header><h3>Bake-time row predicates</h3><p>Optional SQL predicates applied to the dataset query during extraction. Enter one predicate per line; predicates are combined with AND.</p></header>
        <Field label="Predicates" error={errors.bakeFilters?.join(' ')}>
          <Textarea
            className="maps-config__dsl"
            value={bakePredicates.join('\n')}
            placeholder={"country_code = 'EG'\nquality_score >= 0.8"}
            onChange={(event) => write('bakeFilters', event.target.value.split(/\r?\n/).map((line) => line.trim()).filter(Boolean))}
            spellCheck={false}
          />
        </Field>
      </section>

      <section className="maps-config__section">
        <header className="maps-config__section-heading"><div><h3>Interactive filters</h3><p>Choose the carried dimensions and temporal channels exposed in the map controls.</p></div>{!automaticFilters && <Button size="sm" variant="ghost" onClick={() => write('filters', undefined, true)}>Use automatic controls</Button>}</header>
        {automaticFilters && <div className="maps-config__mode-note"><strong>Automatic controls</strong><span>All dimension and temporal channels are enabled. Change a selection to create an explicit curated list.</span></div>}
        {filterable.length === 0 ? <div className="maps-config__empty">This dataset has no dimension or temporal channels.</div> : (
          <div className="maps-config__rows">
            {filterable.map((channel) => {
              const filter = effectiveFilters.find((candidate) => candidate.channel === channel.name);
              return <div className="maps-config__row maps-config__row--filter" key={channel.name}>
                <label className="maps-config__toggle"><input type="checkbox" checked={Boolean(filter)} onChange={(event) => setFilterEnabled(channel.name, event.target.checked)} /><span><strong>{channel.name}</strong><small>{channel.role} · {channel.type}</small></span></label>
                <Select aria-label={`${channel.name} filter control`} disabled={!filter} value={filter?.control ?? ''} onChange={(event) => updateFilter(channel.name, { control: event.target.value as FilterConfig['control'] })}>
                  {(channel.role === 'temporal' ? ['dateRange'] : ['select']).map((control) => <option key={control} value={control}>{controlLabel(control)}</option>)}
                </Select>
                <Input aria-label={`${channel.name} default value`} disabled={!filter} value={filter?.default ?? ''} placeholder="Optional default" onChange={(event) => updateFilter(channel.name, { default: event.target.value || undefined })} />
              </div>;
            })}
          </div>
        )}
        {errors.filters && <span className="maps-config__error">{errors.filters.join(' ')}</span>}
      </section>

      <section className="maps-config__section">
        <header className="maps-config__section-heading"><div><h3>Computed measures</h3><p>Each expression uses the closed per-geometry measure DSL, for example <code>sum(tests)</code> or <code>argmax(operator, sum(tests))</code>.</p></div><Button size="sm" variant="secondary" disabled={!keyedGeometry} onClick={() => write('measures', [...measures, { name: '', expr: '' }])}>Add measure</Button></header>
        <details className="maps-config__reference">
          <summary>Measure DSL reference</summary>
          <p><code>count()</code> · <code>sum(channel)</code> · <code>avg(channel)</code> · <code>wavg(value, weight)</code> · <code>min(channel)</code> · <code>max(channel)</code></p>
          <p><code>share(sum(channel)) where dimension = 'value'</code> · <code>argmax(dimension, sum(channel))</code> · <code>argmin(dimension, sum(channel))</code></p>
          <span>The geometry is the implicit group key. This is a closed fold grammar, not SQL.</span>
        </details>
        {!keyedGeometry && <Alert>Computed measures require keyed geometry. The current engine supports them for quadkey datasets.</Alert>}
        {measures.length > 0 && <div className="maps-config__rows">
          {measures.map((measure, index) => <div className="maps-config__row maps-config__row--measure" key={index}>
            <Input aria-label={`Measure ${index + 1} name`} value={measure.name} placeholder="total_tests" onChange={(event) => updateMeasure(index, { name: event.target.value })} />
            <Input aria-label={`Measure ${index + 1} expression`} value={measure.expr} placeholder="sum(tests)" onChange={(event) => updateMeasure(index, { expr: event.target.value })} />
            <Button size="sm" variant="ghost" onClick={() => write('measures', measures.filter((_, candidate) => candidate !== index))}>Remove</Button>
          </div>)}
        </div>}
        {errors.measures && <span className="maps-config__error">{errors.measures.join(' ')}</span>}
      </section>

      <section className="maps-config__section">
        <header><h3>Visual encoding</h3><p>Bind a dataset channel or computed measure to color. Scale type and scheme can be inferred or set explicitly.</p></header>
        <div className="maps-config__fields">
          <Field label="Color channel" error={errors.encoding?.join(' ')}><Select value={encoding.color?.channel ?? ''} onChange={(event) => updateColor({ channel: event.target.value })}><option value="">No color encoding</option>{availableChannels.map((channel) => <option key={channel} value={channel}>{channel}</option>)}</Select></Field>
          <Field label="Scale type"><Select disabled={!encoding.color?.channel} value={encoding.color?.type ?? ''} onChange={(event) => updateColor(event.target.value ? { type: event.target.value } : {}, event.target.value ? [] : ['type'])}>{scaleTypes.map((type) => <option key={type || 'infer'} value={type}>{type || 'Infer from channel'}</option>)}</Select></Field>
          <Field label="Color scheme"><Select disabled={!encoding.color?.channel} value={encoding.color?.scheme ?? ''} onChange={(event) => updateColor(event.target.value ? { scheme: event.target.value } : {}, event.target.value ? [] : ['scheme'])}>{schemes.map((scheme) => <option key={scheme || 'default'} value={scheme}>{scheme || 'Default for scale'}</option>)}</Select></Field>
        </div>
      </section>

      <section className="maps-config__section">
        <header><h3>Click inspection</h3><p>Select the values shown when an operator clicks a rendered mark.</p></header>
        <div className="maps-config__channel-grid">
          {availableChannels.map((channel) => <label className="maps-config__toggle" key={channel}><input type="checkbox" checked={inspect.channels?.includes(channel) ?? false} onChange={(event) => setInspectChannel(channel, event.target.checked)} /><span>{channel}</span></label>)}
        </div>
        <Field label="Panel title channel"><Select disabled={!inspect.channels?.length} value={inspect.title ?? ''} onChange={(event) => write('inspect', { ...inspect, title: event.target.value || undefined })}><option value="">No title channel</option>{(inspect.channels ?? []).map((channel) => <option key={channel} value={channel}>{channel}</option>)}</Select></Field>
        {errors.inspect && <span className="maps-config__error">{errors.inspect.join(' ')}</span>}
      </section>

      <details className="maps-config__advanced">
        <summary>Advanced configuration JSON</summary>
        <p>The guided controls above write the same domain configuration. Direct editing remains available for supported fields that are not yet modeled by the builder.</p>
        <div className="maps-editor__json-grid">
          <JsonField label="Bake filters" field="bakeFilters" value={form.bakeFilters} onChange={onChange} errors={errors} />
          <JsonField label="Interactive filters" field="filters" value={form.filters} onChange={onChange} errors={errors} />
          <JsonField label="Measures" field="measures" value={form.measures} onChange={onChange} errors={errors} />
          <JsonField label="Encoding" field="encoding" value={form.encoding} onChange={onChange} errors={errors} />
          <JsonField label="Inspection" field="inspect" value={form.inspect} onChange={onChange} errors={errors} />
        </div>
      </details>
    </div>
  );
}

function JsonField<K extends 'bakeFilters' | 'filters' | 'measures' | 'encoding' | 'inspect'>({
  label, field, value, onChange, errors,
}: {
  label: string;
  field: K;
  value: string;
  onChange: <T extends keyof MapFormState>(field: T, value: MapFormState[T]) => void;
  errors: Record<string, string[]>;
}) {
  const error: ReactNode = errors[field]?.join(' ');
  return <Field label={label} error={error}><Textarea className="maps-editor__json" value={value} onChange={(event) => onChange(field, event.target.value)} spellCheck={false} aria-invalid={Boolean(error)} /></Field>;
}

function readArray<T>(source: string): T[] {
  try {
    const value = JSON.parse(source || '[]');
    return Array.isArray(value) ? value as T[] : [];
  } catch {
    return [];
  }
}

function readObject<T extends object>(source: string): Partial<T> {
  try {
    const value: unknown = JSON.parse(source || '{}');
    return value && typeof value === 'object' && !Array.isArray(value) ? value as Partial<T> : {};
  } catch {
    return {};
  }
}

function controlLabel(control: string): string {
  if (control === 'dateRange') return 'Date range';
  return 'Single select';
}
