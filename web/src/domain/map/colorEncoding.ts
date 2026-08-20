import type { ColorSpec, ScaleType } from './manifest';
import {
  DEFAULT_CATEGORICAL,
  DEFAULT_DIVERGING,
  DEFAULT_SEQUENTIAL,
  schemeHexes,
  type SchemeFamily,
} from './schemes';

export type ColorChannelKind = 'numeric' | 'categorical' | 'temporal';

export interface ColorChannelOption {
  name: string;
  kind: ColorChannelKind;
  detail: string;
}

const NUMERIC_TYPES = new Set(['f32', 'f64', 'u8', 'u16', 'i32', 'i64']);

export function colorChannelOption(channel: { name: string; role: string; type: string }): ColorChannelOption {
  const kind: ColorChannelKind = channel.type === 'dict'
    ? 'categorical'
    : channel.type === 'date' || channel.role === 'temporal'
      ? 'temporal'
      : NUMERIC_TYPES.has(channel.type) ? 'numeric' : 'categorical';
  return { name: channel.name, kind, detail: `${channel.role} · ${channel.type}` };
}

export function measureColorOption(name: string): ColorChannelOption {
  return { name, kind: 'numeric', detail: 'computed measure · numeric' };
}

export function recommendedColorSpec(channel: ColorChannelOption): ColorSpec {
  return channel.kind === 'categorical'
    ? { channel: channel.name, type: 'categorical', scheme: DEFAULT_CATEGORICAL }
    : { channel: channel.name, type: 'linear', scheme: DEFAULT_SEQUENTIAL };
}

export const NUMERIC_SCALE_TYPES: ScaleType[] = [
  'linear', 'log', 'sqrt', 'diverging', 'quantize', 'quantile', 'threshold', 'categorical',
];
export const CATEGORICAL_SCALE_TYPES: ScaleType[] = ['categorical', 'ordinal'];

export function scaleTypesFor(channel?: ColorChannelOption): ScaleType[] {
  return channel?.kind === 'categorical' ? CATEGORICAL_SCALE_TYPES : NUMERIC_SCALE_TYPES;
}

export function schemeFamily(type?: ScaleType): SchemeFamily {
  if (type === 'categorical') return 'categorical';
  if (type === 'diverging') return 'diverging';
  return 'sequential';
}

export function defaultScheme(type?: ScaleType): string {
  const family = schemeFamily(type);
  return family === 'categorical'
    ? DEFAULT_CATEGORICAL
    : family === 'diverging' ? DEFAULT_DIVERGING : DEFAULT_SEQUENTIAL;
}

export function withScaleType(spec: ColorSpec, type: ScaleType): ColorSpec {
  const next: ColorSpec = { channel: spec.channel, type, scheme: defaultScheme(type), unknown: spec.unknown };
  if (type === 'diverging') next.midpoint = 0;
  if (type === 'quantize' || type === 'quantile') next.bins = 5;
  if (type === 'threshold') next.thresholds = [25, 50, 75];
  if (type === 'categorical' || type === 'ordinal') {
    if (spec.domain && spec.domain.every(value => typeof value === 'string' || typeof value === 'number'))
      next.domain = spec.domain;
    if (type === 'categorical' && spec.overrides) next.overrides = spec.overrides;
  }
  return next;
}

export function withScheme(spec: ColorSpec, scheme: string): ColorSpec {
  const { range: _range, palette: _palette, ...rest } = spec;
  return { ...rest, scheme };
}

export function withCustomRange(spec: ColorSpec): ColorSpec {
  const family = schemeFamily(spec.type);
  const { scheme: _scheme, palette: _palette, ...rest } = spec;
  return { ...rest, range: schemeHexes(family, spec.scheme ?? defaultScheme(spec.type)) };
}

export function isHexColor(value: string): boolean {
  return /^#(?:[0-9a-f]{3}|[0-9a-f]{6})$/i.test(value);
}

export function colorInputValue(value: string | undefined, fallback = '#888888'): string {
  if (!value || !isHexColor(value)) return fallback;
  if (value.length === 7) return value;
  return `#${value[1]}${value[1]}${value[2]}${value[2]}${value[3]}${value[3]}`;
}

export function previewSpec(spec: ColorSpec): ColorSpec {
  const range = spec.range?.filter(isHexColor);
  const validOverrides = Object.fromEntries(Object.entries(spec.overrides ?? {}).filter(([key, value]) => key.trim() && isHexColor(value)));
  return {
    ...spec,
    range: range && range.length >= 2 ? range : undefined,
    unknown: spec.unknown && isHexColor(spec.unknown) ? spec.unknown : undefined,
    overrides: Object.keys(validOverrides).length ? validOverrides : undefined,
  };
}
