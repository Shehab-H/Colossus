import { isHexColor } from '@/lib/color/colorEncoding';
import type { BakePolicy } from '@/types/bake';
import type { MapDraft } from '@/types/map';

export interface MapFormState {
  slug: string;
  title: string;
  datasetId: string;
  viewport: MapDraft['viewport'];
  mark: MapDraft['mark'];
  bakeFilters: string;
  filters: string;
  measures: string;
  encoding: string;
  inspect: string;
  scheduleEnabled: boolean;
  intervalDays: string;
  intervalHours: string;
  intervalMinutes: string;
  intervalSeconds: string;
  retention: string;
}

export const emptyMapForm = (): MapFormState => ({
  slug: '',
  title: '',
  datasetId: '',
  viewport: 'geo',
  mark: 'point',
  bakeFilters: '[]',
  filters: '',
  measures: '[]',
  encoding: '',
  inspect: '',
  scheduleEnabled: false,
  intervalDays: '1',
  intervalHours: '0',
  intervalMinutes: '0',
  intervalSeconds: '0',
  retention: '3',
});

const pretty = (value: unknown): string => value === undefined || value === null ? '' : JSON.stringify(value, null, 2);

export function formFromDraft(draft: MapDraft, policy?: BakePolicy): MapFormState {
  const interval = intervalParts(policy?.interval);
  return {
    slug: draft.slug,
    title: draft.title ?? '',
    datasetId: draft.datasetId,
    viewport: draft.viewport,
    mark: draft.mark,
    bakeFilters: pretty(draft.bakeFilters ?? []),
    filters: draft.filters === undefined ? '' : pretty(draft.filters),
    measures: pretty(draft.measures ?? []),
    encoding: pretty(draft.encoding),
    inspect: pretty(draft.inspect),
    scheduleEnabled: policy?.enabled ?? false,
    intervalDays: String(interval?.days ?? 1),
    intervalHours: String(interval?.hours ?? 0),
    intervalMinutes: String(interval?.minutes ?? 0),
    intervalSeconds: String(interval?.seconds ?? 0),
    retention: String(policy?.retainSuccessfulReleases ?? 3),
  };
}

function parseJson<T>(field: keyof MapFormState, source: string, fallback: T, errors: Record<string, string[]>): T {
  if (!source.trim()) return fallback;
  try { return JSON.parse(source) as T; }
  catch (reason) {
    errors[field] = [reason instanceof Error ? reason.message : 'Invalid JSON.'];
    return fallback;
  }
}

export function draftFromForm(form: MapFormState): { draft?: MapDraft; errors: Record<string, string[]> } {
  const errors: Record<string, string[]> = {};
  if (!/^[a-z0-9-]+$/.test(form.slug)) errors.slug = ['Use lowercase kebab-case.'];
  if (!form.datasetId) errors.datasetId = ['Select a dataset.'];
  const bakeFilters = parseJson<string[]>('bakeFilters', form.bakeFilters, [], errors);
  const filters = form.filters.trim() ? parseJson<unknown[]>('filters', form.filters, [], errors) : undefined;
  const measures = parseJson<unknown[]>('measures', form.measures, [], errors);
  const encoding = parseJson<unknown | undefined>('encoding', form.encoding, undefined, errors);
  const inspect = parseJson<unknown | undefined>('inspect', form.inspect, undefined, errors);
  if (!Array.isArray(bakeFilters) || bakeFilters.some((value) => typeof value !== 'string'))
    errors.bakeFilters = ['Bake filters must be a list of SQL predicates.'];
  if (filters !== undefined && !Array.isArray(filters)) errors.filters = ['Filters must be a JSON array.'];
  if (!Array.isArray(measures)) errors.measures = ['Measures must be a JSON array.'];
  if (!errors.encoding) validateEncodingColors(encoding, errors);
  if (Object.keys(errors).length > 0) return { errors };

  return {
    errors,
    draft: {
      schemaVersion: 1,
      slug: form.slug,
      title: form.title.trim() || undefined,
      datasetId: form.datasetId,
      viewport: form.viewport,
      mark: form.mark,
      bakeFilters,
      filters,
      measures,
      encoding,
      inspect,
    },
  };
}

function validateEncodingColors(encoding: unknown, errors: Record<string, string[]>): void {
  if (!encoding || typeof encoding !== 'object' || Array.isArray(encoding)) return;
  const color = (encoding as Record<string, unknown>).color;
  if (!color || typeof color !== 'object' || Array.isArray(color)) return;
  const spec = color as Record<string, unknown>;
  const colors = [
    ...(Array.isArray(spec.range) ? spec.range : []),
    ...(typeof spec.unknown === 'string' ? [spec.unknown] : []),
    ...mapColors(spec.palette),
    ...mapColors(spec.overrides),
  ];
  if (colors.some(value => typeof value !== 'string' || !isHexColor(value)))
    errors.encoding = ['Colors must use #RGB or #RRGGBB hexadecimal notation.'];
}

function mapColors(value: unknown): unknown[] {
  return value && typeof value === 'object' && !Array.isArray(value)
    ? Object.values(value as Record<string, unknown>)
    : [];
}

export function mapDraftsEqual(left: MapDraft, right: MapDraft): boolean {
  return JSON.stringify(canonical(left)) === JSON.stringify(canonical(right));
}

function canonical(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(canonical);
  if (!value || typeof value !== 'object') return value;
  return Object.fromEntries(Object.entries(value as Record<string, unknown>)
    .filter(([, candidate]) => candidate !== undefined)
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([key, candidate]) => [key, canonical(candidate)]));
}

export function hoursToInterval(hoursText: string): string {
  const totalHours = Math.max(1, Math.floor(Number(hoursText) || 24));
  const days = Math.floor(totalHours / 24);
  const hours = totalHours % 24;
  return days > 0
    ? `${days}.${String(hours).padStart(2, '0')}:00:00`
    : `${String(hours).padStart(2, '0')}:00:00`;
}

export function intervalFromParts(parts: Pick<MapFormState, 'intervalDays' | 'intervalHours' | 'intervalMinutes' | 'intervalSeconds'>): string {
  const value = (source: string) => Math.max(0, Math.floor(Number(source) || 0));
  const totalSeconds = Math.max(1,
    value(parts.intervalDays) * 86_400
      + value(parts.intervalHours) * 3_600
      + value(parts.intervalMinutes) * 60
      + value(parts.intervalSeconds));
  const days = Math.floor(totalSeconds / 86_400);
  const remainder = totalSeconds % 86_400;
  const hours = Math.floor(remainder / 3_600);
  const minutes = Math.floor((remainder % 3_600) / 60);
  const seconds = remainder % 60;
  const clock = `${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`;
  return days > 0 ? `${days}.${clock}` : clock;
}

export function intervalParts(interval?: string): { days: number; hours: number; minutes: number; seconds: number } | null {
  if (!interval) return null;
  const match = interval.match(/^(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})$/);
  if (!match) return null;
  return {
    days: Number(match[1] ?? 0),
    hours: Number(match[2]),
    minutes: Number(match[3]),
    seconds: Number(match[4]),
  };
}

export function intervalHours(interval?: string): number | null {
  const parts = intervalParts(interval);
  if (!parts) return null;
  return parts.days * 24 + parts.hours + parts.minutes / 60 + parts.seconds / 3600;
}
