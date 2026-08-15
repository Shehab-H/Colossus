import type { SourcePreview } from '@entities/catalog';
import { Badge, EmptyState, PaddedCard } from '@shared/ui';

export function SourcePreviewPanel({ preview, datasetName }: { preview?: SourcePreview; datasetName?: string }) {
  if (!preview) return <PaddedCard><EmptyState eyebrow="Preview" title="Choose a dataset" description="Preview is capped at 50 returned rows, 1 MB, 50 million scanned rows, and 10 seconds." /></PaddedCard>;
  const bounds = preview.geometry.bounds;
  return (
    <PaddedCard className="source-preview">
      <div className="source-preview__heading">
        <div><span>Bounded preview</span><h2>{datasetName}</h2></div>
        <Badge tone={preview.truncated ? 'warning' : 'success'}>{preview.truncated ? 'Row limit reached' : 'Within budget'}</Badge>
      </div>
      <div className="source-preview__stats">
        <span><small>Rows</small><strong>{preview.geometry.sampledRows}</strong></span>
        <span><small>Distinct geometry</small><strong>{preview.geometry.distinctGeometries}</strong></span>
        <span><small>Payload</small><strong>{formatBytes(preview.resultBytes)}</strong></span>
        <span><small>Bounds</small><strong>{formatBounds(bounds)}</strong></span>
      </div>
      {preview.warnings && preview.warnings.length > 0 && (
        <div className="source-preview__warnings" role="status">
          {preview.warnings.map((warning) => <Badge key={warning} tone="warning">{warning}</Badge>)}
        </div>
      )}
      <div className="source-preview__table-wrap">
        <table className="source-preview__table">
          <thead><tr>{preview.columns.map((column) => <th key={column.name}><span>{column.name}</span><small>{column.sourceType}</small></th>)}</tr></thead>
          <tbody>{preview.rows.map((row, index) => <tr key={index}>{preview.columns.map((column) => <td key={column.name}>{row[column.name] ?? <em>null</em>}</td>)}</tr>)}</tbody>
        </table>
      </div>
    </PaddedCard>
  );
}

const formatBytes = (bytes: number) => bytes < 1024 ? `${bytes} B` : `${(bytes / 1024).toFixed(1)} KiB`;
const formatBounds = (bounds: SourcePreview['geometry']['bounds']) =>
  `${bounds.minX.toFixed(2)}, ${bounds.minY.toFixed(2)} → ${bounds.maxX.toFixed(2)}, ${bounds.maxY.toFixed(2)}`;
