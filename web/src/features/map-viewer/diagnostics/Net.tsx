import { bytes } from './dashboardFormat';

/** A network stage's honest shape: bytes that actually crossed the wire, and how many requests didn't
 *  have to. `unknown` only appears when the browser refused to say (no Timing-Allow-Origin). */
export const Net = ({ label, n }: { label: string; n: { reqs: number; cold: number; warm: number; unknown: number; wire: number } }) => (
  <div className="perf-dashboard__row">
    <span className="perf-dashboard__label">{label}</span>
    <span className="perf-dashboard__spacer" />
    <strong className="perf-dashboard__value">{bytes(n.wire)}</strong>
    <span className="perf-dashboard__note">
      {n.cold}↓{n.warm ? ` ${n.warm}⚡` : ''}
      {n.unknown ? ` ${n.unknown}?` : ''}
    </span>
  </div>
);
