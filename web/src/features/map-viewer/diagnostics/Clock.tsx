import type { StageStat } from './stats';
import { cx } from '@/utils/classNames';

/** One stage's clock: p50, with p95 and sample count alongside. Reads "—" until the stage has actually
 *  run — never 0.0, which would read as "instant" for a route that simply never executed. */
export const Clock = ({ label, s, note }: { label: string; s?: StageStat; note: string }) => (
  <div className="perf-dashboard__row">
    <span className="perf-dashboard__label">{label}</span>
    <span className="perf-dashboard__spacer" />
    <strong className={cx('perf-dashboard__value', !s && 'is-empty')}>
      {s ? `${s.p50.toFixed(1)} ms` : '—'}
    </strong>
    <span className="perf-dashboard__note perf-dashboard__note--wide">{s ? `p95 ${s.p95.toFixed(0)} · n${s.n}` : note}</span>
  </div>
);
