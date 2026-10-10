import { Button } from '@/components/ui';
import type { BakeRunCounts } from '@/types/bake';
import { runFilterCount, runFilters, type RunFilter } from '../utils/bakeRunStatus';

export default function BakeRunFilterBar({ value, counts, onChange }: {
  value: RunFilter;
  counts: BakeRunCounts;
  onChange: (filter: RunFilter) => void;
}) {
  return (
    <div className="run-history__filters" role="group" aria-label="Filter runs by status">
      {runFilters.map((filter) => {
        const selected = filter.id === value;
        return (
          <Button key={filter.id} size="sm" variant={selected ? 'secondary' : 'ghost'} aria-pressed={selected} onClick={() => onChange(filter.id)}>
            {filter.label}
            <span className="run-history__count">{runFilterCount(filter.id, counts).toLocaleString()}</span>
          </Button>
        );
      })}
    </div>
  );
}
