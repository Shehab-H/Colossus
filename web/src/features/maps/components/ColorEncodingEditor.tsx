import { useEffect, useState } from 'react';
import type { ColorDomain } from '@/lib/color/scale';
import {
  colorInputValue,
  defaultScheme,
  isHexColor,
  previewSpec,
  recommendedColorSpec,
  scaleTypesFor,
  schemeFamily,
  withCustomRange,
  withScaleType,
  withScheme,
  type ColorChannelOption,
} from '@/lib/color/colorEncoding';
import { schemeHexes, schemeNames } from '@/lib/color/schemes';
import { Alert, Button, Field, Input, Select } from '@/components/ui';
import type { ColorSpec, ScaleType } from '@/types/viewConfig';
import { describeLegend, type Legend } from '@/lib/color/legend';

const SCALE_LABELS: Record<ScaleType, string> = {
  linear: 'Linear gradient',
  log: 'Logarithmic gradient',
  sqrt: 'Square-root gradient',
  diverging: 'Diverging around a midpoint',
  quantize: 'Equal-width bins',
  quantile: 'Equal-count quantiles',
  threshold: 'Authored thresholds',
  ordinal: 'Ordered categories',
  categorical: 'Unordered categories',
};

export function ColorEncodingEditor({
  value,
  channels,
  error,
  disabled = false,
  onChange,
}: {
  value?: ColorSpec;
  channels: ColorChannelOption[];
  error?: string;
  disabled?: boolean;
  onChange: (value?: ColorSpec) => void;
}) {
  const selected = channels.find((channel) => channel.name === value?.channel);
  const type = value?.type;
  const family = schemeFamily(type);
  const presets = schemeNames()[family];
  const compatibleTypes = scaleTypesFor(selected);
  const currentTypeIsLegacy = Boolean(type && !compatibleTypes.includes(type));

  const chooseChannel = (channelName: string) => {
    if (!channelName) onChange(undefined);
    else {
      const channel = channels.find((candidate) => candidate.name === channelName);
      if (channel) onChange(recommendedColorSpec(channel));
    }
  };

  const update = (patch: Partial<ColorSpec>, remove: (keyof ColorSpec)[] = []) => {
    if (!value) return;
    const next = { ...value, ...patch };
    for (const key of remove) delete next[key];
    onChange(next);
  };

  const chooseType = (nextType: ScaleType) => value && onChange(withScaleType(value, nextType));
  const choosePreset = (scheme: string) => value && onChange(withScheme(value, scheme));
  const customize = () => value && onChange(withCustomRange(value));

  const setRangeColor = (index: number, color: string) => {
    if (!value?.range) return;
    const range = [...value.range];
    range[index] = color;
    update({ range });
  };

  const removeRangeColor = (index: number) => {
    if (!value?.range || value.range.length <= 2) return;
    update({ range: value.range.filter((_, candidate) => candidate !== index) });
  };

  const addRangeColor = () => {
    if (!value?.range || value.range.length >= 12) return;
    update({ range: [...value.range, value.range.at(-1) ?? '#888888'] });
  };

  const setDomainBound = (index: 0 | 1, text: string) => {
    if (!text) return update({}, ['domain']);
    const existing = value?.domain?.length === 2 && value.domain.every(candidate => typeof candidate === 'number')
      ? [...value.domain] as number[]
      : [0, 100];
    existing[index] = Number(text);
    update({ domain: existing });
  };

  const renameOverride = (oldKey: string, nextKey: string) => {
    if (!value) return;
    const overrides = { ...(value.overrides ?? {}) };
    const color = overrides[oldKey];
    delete overrides[oldKey];
    overrides[nextKey] = color;
    update({ overrides });
  };

  const setOverrideColor = (key: string, color: string) =>
    update({ overrides: { ...(value?.overrides ?? {}), [key]: color } });

  const removeOverride = (key: string) => {
    const overrides = { ...(value?.overrides ?? {}) };
    delete overrides[key];
    update(Object.keys(overrides).length ? { overrides } : {}, Object.keys(overrides).length ? [] : ['overrides']);
  };

  const addOverride = () => {
    if (!value) return;
    const overrides = { ...(value.overrides ?? {}) };
    let index = Object.keys(overrides).length + 1;
    while (`Category ${index}` in overrides) index++;
    const colors = value.range ?? schemeHexes('categorical', value.scheme ?? defaultScheme('categorical'));
    overrides[`Category ${index}`] = colors[(index - 1) % colors.length];
    update({ overrides });
  };

  const numericDomain = value?.domain?.length === 2 && value.domain.every(candidate => typeof candidate === 'number')
    ? value.domain as number[]
    : undefined;
  const categoryDomain = value?.domain?.filter(candidate => typeof candidate === 'string' || typeof candidate === 'number').map(String) ?? [];
  const legend = value ? previewLegend(value, categoryDomain) : null;

  return (
    <div className="color-editor" aria-disabled={disabled || undefined}>
      <div className="maps-config__fields color-editor__primary">
        <Field label="Color channel" error={error}>
          <Select disabled={disabled} value={value?.channel ?? ''} onChange={(event) => chooseChannel(event.target.value)}>
            <option value="">No color encoding</option>
            {channels.map((channel) => <option key={channel.name} value={channel.name}>{channel.name} — {channel.detail}</option>)}
          </Select>
        </Field>
        <Field label="Scale type">
          <Select disabled={disabled || !value} value={type ?? ''} onChange={(event) => chooseType(event.target.value as ScaleType)}>
            {!type && <option value="">Inferred by existing configuration</option>}
            {currentTypeIsLegacy && type && <option value={type}>Existing: {type}</option>}
            {compatibleTypes.map((candidate) => <option key={candidate} value={candidate}>{SCALE_LABELS[candidate]}</option>)}
          </Select>
        </Field>
      </div>

      {value && (
        <>
          {currentTypeIsLegacy && <Alert tone="warning">This saved scale is unusual for the selected channel. It is preserved until you choose a replacement.</Alert>}
          <div className="color-editor__section">
            <div className="color-editor__heading"><div><h4>Color palette</h4><p>Pick a compatible preset or customize its individual colors.</p></div>{!value.range && <Button size="sm" variant="secondary" type="button" onClick={customize}>Customize colors</Button>}</div>
            <div className="color-editor__presets" role="radiogroup" aria-label={`${family} color palettes`}>
              {presets.map((scheme) => {
                const colors = schemeHexes(family, scheme);
                return (
                  <button
                    className="color-editor__preset"
                    type="button"
                    role="radio"
                    aria-checked={!value.range && value.scheme === scheme}
                    key={scheme}
                    disabled={disabled}
                    onClick={() => choosePreset(scheme)}
                    onKeyDown={(event) => {
                      if (event.key !== ' ' && event.key !== 'Enter') return;
                      event.preventDefault();
                      choosePreset(scheme);
                    }}
                  >
                    <span className="color-editor__preset-colors" style={{ background: gradient(colors, family === 'categorical') }} />
                    <span>{displayName(scheme)}</span>
                  </button>
                );
              })}
            </div>

            {value.range && (
              <div className="color-editor__custom">
                <div className="color-editor__heading"><strong>Custom colors</strong><Button size="sm" variant="ghost" type="button" disabled={value.range.length >= 12} onClick={addRangeColor}>Add color</Button></div>
                <div className="color-editor__color-list">
                  {value.range.map((color, index) => (
                    <div className="color-editor__color-row" key={index}>
                      <ColorValue label={`Color ${index + 1}`} value={color} disabled={disabled} onChange={(next) => setRangeColor(index, next)} />
                      <Button size="sm" variant="ghost" type="button" disabled={disabled || value.range!.length <= 2} onClick={() => removeRangeColor(index)}>Remove</Button>
                    </div>
                  ))}
                </div>
              </div>
            )}
          </div>

          <div className="color-editor__preview" aria-label="Color scale preview">
            <div className="color-editor__heading"><div><h4>Style preview</h4><p>Uses an illustrative domain; the published legend uses the baked full-dataset domain.</p></div><span>{legend?.note}</span></div>
            {legend ? <ScalePreview legend={legend} /> : <span className="color-editor__muted">Choose a valid scale to preview it.</span>}
          </div>

          <details className="color-editor__advanced">
            <summary>Advanced color controls</summary>
            <div className="color-editor__advanced-grid">
              {type !== 'categorical' && <label className="maps-config__toggle"><input type="checkbox" disabled={disabled} checked={value.reverse ?? false} onChange={(event) => update({ reverse: event.target.checked }, event.target.checked ? [] : ['reverse'])} /><span>Reverse color order</span></label>}

              {type !== 'categorical' && type !== 'ordinal' && (
                <div className="color-editor__domain">
                  <Field label="Domain minimum"><Input type="number" disabled={disabled} value={numericDomain?.[0] ?? ''} placeholder="Automatic" onChange={(event) => setDomainBound(0, event.target.value)} /></Field>
                  <Field label="Domain maximum"><Input type="number" disabled={disabled} value={numericDomain?.[1] ?? ''} placeholder="Automatic" onChange={(event) => setDomainBound(1, event.target.value)} /></Field>
                </div>
              )}

              {(type === 'categorical' || type === 'ordinal') && (
                <Field label="Category order" description="Optional; enter one value per line.">
                  <textarea className="color-editor__category-order" disabled={disabled} value={categoryDomain.join('\n')} onChange={(event) => update(event.target.value.trim() ? { domain: event.target.value.split(/\r?\n/).map(item => item.trim()).filter(Boolean) } : {}, event.target.value.trim() ? [] : ['domain'])} />
                </Field>
              )}

              {type === 'diverging' && <Field label="Midpoint"><Input type="number" disabled={disabled} value={value.midpoint ?? ''} placeholder="Domain midpoint" onChange={(event) => update(event.target.value ? { midpoint: Number(event.target.value) } : {}, event.target.value ? [] : ['midpoint'])} /></Field>}
              {(type === 'quantize' || type === 'quantile') && <Field label="Number of bins"><Input type="number" min="2" max="12" disabled={disabled} value={value.bins ?? 5} onChange={(event) => update({ bins: Number(event.target.value) })} /></Field>}
              {type === 'threshold' && <ThresholdInput disabled={disabled} value={value.thresholds ?? []} onChange={(thresholds) => update({ thresholds })} />}

              <Field label="Unknown or null color">
                <ColorValue label="Unknown or null color" value={value.unknown ?? '#888888'} disabled={disabled} onChange={(unknown) => update({ unknown })} />
              </Field>

              {type === 'categorical' && (
                <div className="color-editor__overrides">
                  <div className="color-editor__heading"><div><strong>Category overrides</strong><p>Type only the values that need a specific color.</p></div><Button type="button" size="sm" variant="secondary" onClick={addOverride}>Add override</Button></div>
                  {value.palette && <Alert tone="warning">This configuration uses a legacy exact palette. Pick a preset to switch to automatic colors with partial overrides.</Alert>}
                  {Object.entries(value.overrides ?? {}).map(([key, color]) => (
                    <div className="color-editor__override-row" key={key}>
                      <Input aria-label={`Category value ${key}`} disabled={disabled} value={key} onChange={(event) => renameOverride(key, event.target.value)} />
                      <ColorValue label={`${key || 'Category'} color`} value={color} disabled={disabled} onChange={(next) => setOverrideColor(key, next)} />
                      <Button type="button" size="sm" variant="ghost" onClick={() => removeOverride(key)}>Remove</Button>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </details>
        </>
      )}
    </div>
  );
}

function ColorValue({ label, value, disabled, onChange }: { label: string; value: string; disabled?: boolean; onChange: (value: string) => void }) {
  return (
    <div className="color-editor__color-value">
      <input type="color" aria-label={`${label} picker`} disabled={disabled} value={colorInputValue(value)} onChange={(event) => onChange(event.target.value)} />
      <Input aria-label={`${label} hex`} disabled={disabled} value={value} aria-invalid={!isHexColor(value)} onChange={(event) => onChange(event.target.value)} />
    </div>
  );
}

function ThresholdInput({ value, disabled, onChange }: { value: number[]; disabled?: boolean; onChange: (value: number[]) => void }) {
  const [text, setText] = useState(value.join(', '));
  useEffect(() => setText(value.join(', ')), [value]);
  const commit = () => {
    const parsed = text.split(',').map(item => Number(item.trim())).filter(Number.isFinite).sort((a, b) => a - b);
    if (parsed.length) onChange([...new Set(parsed)]);
  };
  return <Field label="Thresholds" description="Comma-separated ascending values."><Input disabled={disabled} value={text} onChange={(event) => setText(event.target.value)} onBlur={commit} /></Field>;
}

function previewLegend(spec: ColorSpec, categories: string[]): Legend | null {
  const safe = previewSpec(spec);
  const domain: ColorDomain = safe.type === 'categorical' || safe.type === 'ordinal'
    ? { kind: 'categorical', categories: [...new Set([...categories, ...Object.keys(safe.overrides ?? {}), 'Category 1', 'Category 2', 'Category 3', 'Category 4'])] }
    : { kind: 'numeric', min: 0, max: 100, sample: Array.from({ length: 21 }, (_, index) => index * 5) };
  try { return describeLegend(safe, domain, safe.channel); }
  catch { return null; }
}

function ScalePreview({ legend }: { legend: Legend }) {
  if (legend.kind === 'continuous' && legend.gradient) {
    return <div><div className="color-editor__preview-gradient" style={{ background: gradient(legend.gradient.map(([r, g, b]) => `rgb(${r}, ${g}, ${b})`), false) }} /><div className="color-editor__preview-axis"><span>{legend.min}</span>{legend.midpoint !== undefined && <span>{legend.midpoint}</span>}<span>{legend.max}</span></div></div>;
  }
  return <div className="color-editor__preview-swatches">{legend.items?.map((item) => <span key={item.label}><i style={{ background: `rgb(${item.color.join(',')})` }} />{item.label}</span>)}</div>;
}

function gradient(colors: string[], categorical: boolean): string {
  if (categorical) {
    const width = 100 / colors.length;
    return `linear-gradient(to right, ${colors.flatMap((color, index) => [`${color} ${index * width}%`, `${color} ${(index + 1) * width}%`]).join(', ')})`;
  }
  return `linear-gradient(to right, ${colors.join(', ')})`;
}

function displayName(value: string): string {
  return value.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, character => character.toUpperCase());
}
