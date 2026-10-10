import { stats } from './stats';
import type { PerfStage } from '@/lib/perf/events';

export const stat = (st: ReturnType<typeof stats>, s: PerfStage) => st.find((x) => x.stage === s);
export const count = (st: ReturnType<typeof stats>, s: PerfStage) => stat(st, s)?.n ?? 0;
export const ms = (v: number | null) => (v === null ? '—' : `${v.toFixed(1)} ms`);
export const bytes = (b: number): string =>
  b >= 1e6 ? `${(b / 1e6).toFixed(2)} MB` : b >= 1e3 ? `${(b / 1e3).toFixed(1)} KB` : `${b} B`;
