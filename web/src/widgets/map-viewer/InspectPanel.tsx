import { Button, Card } from '@shared/ui';

export interface InspectRow {
  name: string;
  value: string;
}

export interface Selection {
  title?: string;
  rows: InspectRow[];
}

/** Pinned readout of one clicked cell's configured inspection values. */
export default function InspectPanel({ selection, onClose }: { selection: Selection; onClose: () => void }) {
  return (
    <Card className="map-inspection">
      <div className="map-inspection__header">
        <strong>{selection.title ?? 'Cell'}</strong>
        <Button
          className="map-inspection__close"
          variant="ghost"
          size="sm"
          iconOnly
          onClick={onClose}
          aria-label="Close inspection"
        >
          ×
        </Button>
      </div>
      <table className="map-inspection__table">
        <tbody>
          {selection.rows.map((row) => (
            <tr key={row.name}>
              <th scope="row">{row.name}</th>
              <td>{row.value}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </Card>
  );
}
