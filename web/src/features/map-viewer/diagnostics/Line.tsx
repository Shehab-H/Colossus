export const Line = ({ label, v, note }: { label: string; v: string; note?: string }) => (
  <div className="perf-dashboard__row">
    <span className="perf-dashboard__label">{label}</span>
    <span className="perf-dashboard__spacer" />
    <strong className="perf-dashboard__value">{v}</strong>
    {note && <span className="perf-dashboard__note">{note}</span>}
  </div>
);
