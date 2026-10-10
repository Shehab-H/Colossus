export type Viewport = 'geo' | 'orthographic';
export type Mark = 'point' | 'line' | 'arc' | 'rect' | 'polygon' | 'heat' | 'text';

export interface Bbox {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

export interface ChannelSpec {
  name: string;
  column: string;
  role: 'measure' | 'dimension' | 'temporal' | 'identity';
  type: string;
}

export interface GeometrySpec {
  kind: string;
}

export interface SourceSpec {
  adapter: string;
  query: string;
  geometry: GeometrySpec;
  channels: ChannelSpec[];
}

/** A data channel bound to a visual channel, optionally with a named scheme (e.g. color ramp). */
export interface ChannelRef {
  channel: string;
  scheme?: string;
}

export type ScaleType =
  | 'linear'
  | 'log'
  | 'sqrt'
  | 'diverging'
  | 'quantize'
  | 'quantile'
  | 'threshold'
  | 'ordinal'
  | 'categorical';

/** How a data channel maps to color. A superset scale spec (à la Vega-Lite): pick the `type`, a named
 *  `scheme` or explicit `range`, and the modifiers that type uses. Everything but `channel` is optional
 *  — the client infers a sensible scale from the channel's datatype when omitted. */
export interface ColorSpec {
  channel: string;
  type?: ScaleType;
  scheme?: string;
  range?: string[]; // explicit hex list — overrides scheme
  domain?: (number | string)[]; // numeric [min,max] or explicit category order
  reverse?: boolean;
  midpoint?: number; // diverging
  bins?: number; // quantize / quantile
  thresholds?: number[]; // threshold
  palette?: Record<string, string>; // categorical: explicit value → hex
  overrides?: Record<string, string>; // categorical: partial value → hex overrides over the selected palette
  unknown?: string; // color for unmapped / null values
}

export interface EncodingSpec {
  color?: ColorSpec;
  size?: ChannelRef;
}

/** Click-to-inspect config. When absent, marks aren't pickable and clicks do nothing. */
export interface InspectSpec {
  channels: string[];
  title?: string;
}

/** A computed per-mark value (VIEW_CONFIG §4). Its presence opts the view into the group regime. */
export interface MeasureSpec {
  name: string;
  expr: string;
}

export interface FilterSpec {
  channel: string;
  control: 'select' | 'multiSelect' | 'dateRange' | 'range';
  default?: string;
}

export interface ViewConfig {
  id: string;
  title?: string;
  viewport: Viewport;
  mark: Mark;
  reduction?: string;
  source: SourceSpec;
  bakeFilters?: string[];
  filters?: FilterSpec[];
  measures?: MeasureSpec[];
  encoding?: EncodingSpec;
  inspect?: InspectSpec;
}
