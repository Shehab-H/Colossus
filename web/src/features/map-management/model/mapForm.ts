import type { BakePolicy, MapDraft } from '@entities/catalog';

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
  intervalHours: string;
  retention: string;
}

export const emptyMapForm = (): MapFormState => ({
  slug: '',
  title: '',
  datasetId: '',
  viewport: 'geo',
  mark: 'point',
  bakeFilters: '[]',
  filters: '[]',
  measures: '[]',
  encoding: '',
  inspect: '',
  scheduleEnabled: false,
  intervalHours: '24',
  retention: '3',
});

const pretty = (value: unknown): string => value === undefined || value === null ? '' : JSON.stringify(value, null, 2);

export function formFromDraft(draft: MapDraft, policy?: BakePolicy): MapFormState {
  return {
    slug: draft.slug,
    title: draft.title ?? '',
    datasetId: draft.datasetId,
    viewport: draft.viewport,
    mark: draft.mark,
    bakeFilters: pretty(draft.bakeFilters ?? []),
    filters: pretty(draft.filters ?? []),
    measures: pretty(draft.measures ?? []),
    encoding: pretty(draft.encoding),
    inspect: pretty(draft.inspect),
    scheduleEnabled: policy?.enabled ?? false,
    intervalHours: String(intervalHours(policy?.interval) ?? 24),
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
  const filters = parseJson<unknown[]>('filters', form.filters, [], errors);
  const measures = parseJson<unknown[]>('measures', form.measures, [], errors);
  const encoding = parseJson<unknown | undefined>('encoding', form.encoding, undefined, errors);
  const inspect = parseJson<unknown | undefined>('inspect', form.inspect, undefined, errors);
  if (!Array.isArray(bakeFilters) || bakeFilters.some((value) => typeof value !== 'string'))
    errors.bakeFilters = ['Bake filters must be a JSON array of channel names.'];
  if (!Array.isArray(filters)) errors.filters = ['Filters must be a JSON array.'];
  if (!Array.isArray(measures)) errors.measures = ['Measures must be a JSON array.'];
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

export function hoursToInterval(hoursText: string): string {
  const totalHours = Math.max(1, Math.floor(Number(hoursText) || 24));
  const days = Math.floor(totalHours / 24);
  const hours = totalHours % 24;
  return days > 0
    ? `${days}.${String(hours).padStart(2, '0')}:00:00`
    : `${String(hours).padStart(2, '0')}:00:00`;
}

export function intervalHours(interval?: string): number | null {
  if (!interval) return null;
  const match = interval.match(/^(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})$/);
  if (!match) return null;
  return Number(match[1] ?? 0) * 24 + Number(match[2]) + Number(match[3]) / 60 + Number(match[4]) / 3600;
}
