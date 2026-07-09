export interface InspectRow {
  name: string;
  value: string;
}

export interface Selection {
  title?: string;
  rows: InspectRow[];
}

/** Pinned readout of one clicked cell's channel values (driven by the view's `inspect` config).
 *  Absent selection renders nothing; the ✕ (or a click on empty map) clears it. */
export default function InspectPanel({ selection, onClose }: { selection: Selection; onClose: () => void }) {
  return (
    <div style={panel}>
      <div style={head}>
        <span style={{ fontWeight: 700 }}>{selection.title ?? 'cell'}</span>
        <button onClick={onClose} style={close} aria-label="close">
          ✕
        </button>
      </div>
      <table style={{ borderCollapse: 'collapse', width: '100%' }}>
        <tbody>
          {selection.rows.map((r) => (
            <tr key={r.name}>
              <td style={keyCell}>{r.name}</td>
              <td style={valCell}>{r.value}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

const panel: React.CSSProperties = {
  position: 'absolute',
  top: 12,
  right: 12,
  padding: '10px 12px',
  background: 'rgba(20,20,20,0.82)',
  color: '#eee',
  font: '12px system-ui, sans-serif',
  borderRadius: 8,
  border: '1px solid #333',
  minWidth: 180,
};
const head: React.CSSProperties = {
  display: 'flex',
  justifyContent: 'space-between',
  alignItems: 'center',
  gap: 12,
  marginBottom: 8,
};
const close: React.CSSProperties = {
  background: 'transparent',
  color: '#aaa',
  border: 'none',
  cursor: 'pointer',
  fontSize: 13,
  lineHeight: 1,
  padding: 0,
};
const keyCell: React.CSSProperties = { opacity: 0.7, padding: '2px 12px 2px 0', whiteSpace: 'nowrap' };
const valCell: React.CSSProperties = { textAlign: 'right', fontVariantNumeric: 'tabular-nums', fontWeight: 600 };
